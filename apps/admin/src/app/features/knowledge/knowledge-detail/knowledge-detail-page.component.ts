import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  TemplateRef,
  viewChild,
  DOCUMENT,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { DetailLayoutComponent } from '@smart-agri/ui';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { Observable } from 'rxjs';
import type { AssistantStatus } from '../../../core/domain/assistant.model';
import {
  isAwaitingApprovalKnowledgeDocument,
  isPendingKnowledgeDocument,
  type KnowledgeBaseDetailView,
  type KnowledgeDocumentStatus,
  type KnowledgeDocumentView,
  type KnowledgeSharingView,
} from '../../../core/domain/knowledge-base.model';
import type {
  ApproveKnowledgeVersionsResult,
  DeleteKnowledgeResult,
  RepositoryView,
  RetryKnowledgeDocumentResult,
  UpdateKnowledgeSharingResult,
} from '../../../core/repositories/demo-repository';
import {
  pollWhile,
  repositoryResource,
  type LoadedView,
} from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent, type StatusTone } from '../../../shared/ui/status-badge/status-badge.component';
import { DocumentRowComponent } from '../components/document-row/document-row.component';
import { DOCUMENT_STATUS_LABELS, SHARING_SCOPE_LABELS } from '../components/knowledge-labels';
import { ReviewDialogComponent, type ReviewDialogData } from '../components/review-dialog/review-dialog.component';
import { SharingPanelComponent } from '../components/sharing-panel/sharing-panel.component';
import { UploadPanelComponent } from '../components/upload-panel/upload-panel.component';

/** 有等待中或處理中的項目時，每隔這麼久重新讀取一次詳情（分頁隱藏時暫停）。 */
export const KNOWLEDGE_DETAIL_POLL_MS = 3000;

type KnowledgeTabId = 'content' | 'assistants' | 'sharing';

interface KnowledgeTab {
  readonly id: KnowledgeTabId;
  readonly label: string;
}

const TABS: readonly KnowledgeTab[] = [
  { id: 'content', label: '內容' },
  { id: 'assistants', label: '已連接助理' },
  { id: 'sharing', label: '分享權限' },
];

const ASSISTANT_STATUS: Record<AssistantStatus, { readonly label: string; readonly tone: StatusTone }> = {
  draft: { label: '草稿', tone: 'neutral' },
  ready: { label: '可發布', tone: 'info' },
  published: { label: '已發布', tone: 'success' },
  paused: { label: '已暫停', tone: 'warning' },
};

/** 刪除前的確認對象：整個知識庫，或其中一份文件／FAQ。 */
type PendingDeletion =
  | { readonly kind: 'knowledge-base'; readonly name: string; readonly itemCount: number }
  | { readonly kind: 'document'; readonly document: KnowledgeDocumentView };

function hasPendingDocuments(view: RepositoryView<KnowledgeBaseDetailView>): boolean {
  return (
    (view.status === 'ready' || view.status === 'partial-failure') &&
    view.data.documents.some((document) => isPendingKnowledgeDocument(document.status))
  );
}

@Component({
  selector: 'app-knowledge-detail-page',
  imports: [
    RouterLink,
    MatDialogModule,
    DetailLayoutComponent,
    PageHeaderComponent,
    StatePanelComponent,
    StatusBadgeComponent,
    DocumentRowComponent,
    SharingPanelComponent,
    UploadPanelComponent,
  ],
  templateUrl: './knowledge-detail-page.component.html',
  styleUrl: './knowledge-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KnowledgeDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly apiSession = inject(ApiSessionService);
  private readonly document = inject(DOCUMENT);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly deleteDialog = viewChild<TemplateRef<unknown>>('deleteDialog');
  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });

  protected readonly tabs = TABS;
  /**
   * 已連接助理清單在 API 模式由 mock 助理推得（issue #49）；畫面用這個判斷是否要加註
   * 「助理設定仍為示範資料」，避免讓人誤以為那份清單也是真實資料。
   */
  protected readonly isApiMode = this.apiSession.apiMode;
  protected readonly knowledgeBaseId = computed(() => this.params().get('id') ?? '');
  protected readonly activeTab = computed<KnowledgeTab>(
    () => TABS.find((tab) => tab.id === this.params().get('tab')) ?? TABS[0],
  );

  /**
   * 讀取並在有等待中或處理中的項目時輪詢（`pollWhile`）：處理進度由 repository 算好
   * （mock 依經過時間、API 由後端工作），畫面只是重新讀取，不分辨是哪一種模式。
   * 換頁籤不會重新讀取；換知識庫或換身分才會。
   */
  private readonly detail = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, knowledgeBaseId: this.knowledgeBaseId() } : undefined;
    },
    stream: ({ knowledgeBaseId }) =>
      pollWhile(() => this.repository.getKnowledgeBaseDetail(knowledgeBaseId), hasPendingDocuments, {
        intervalMs: KNOWLEDGE_DETAIL_POLL_MS,
        document: this.document,
      }),
  });
  protected readonly view = this.detail.view;

  protected readonly liveMessage = signal('');
  protected readonly actionError = signal('');
  protected readonly sharingFeedback = signal('');
  protected readonly savingSharing = signal(false);
  /** 有請求進行中的文件 id（重新處理或刪除）。 */
  protected readonly busyDocuments = signal<ReadonlySet<string>>(new Set());
  protected readonly pendingDeletion = signal<PendingDeletion | null>(null);
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal('');

  /** 只顯示待確認／排程中的文件（issue #47：詳情頁「只看待確認」篩選）。 */
  protected readonly showOnlyAwaitingApproval = signal(false);
  /** 目前勾選要批次確認生效的文件 id。 */
  protected readonly selectedForApproval = signal<ReadonlySet<string>>(new Set());
  protected readonly approvingBatch = signal(false);
  protected readonly batchApproveError = signal('');
  protected readonly isAwaitingApproval = isAwaitingApprovalKnowledgeDocument;

  /** 上一次看到的各文件狀態；輪詢讀到狀態改變時才朗讀，第一次載入不朗讀。 */
  private previousStatuses: ReadonlyMap<string, KnowledgeDocumentStatus> | null = null;

  constructor() {
    effect(() => this.announceStatusChanges(this.view()));
  }

  protected attention(detail: KnowledgeBaseDetailView) {
    const counts = detail.summary.statusCounts;
    return {
      needs: counts['partially-readable'] + counts.failed,
      pending: counts.queued + counts.processing,
      ready: counts.ready,
    };
  }

  protected scopeLabel(detail: KnowledgeBaseDetailView): string {
    return SHARING_SCOPE_LABELS[detail.sharing.scope];
  }

  protected assistantStatus(status: AssistantStatus) {
    return ASSISTANT_STATUS[status];
  }

  protected isBusy(document: KnowledgeDocumentView): boolean {
    return this.busyDocuments().has(document.id);
  }

  /** 上傳成功後重新讀取：處理進度改由既有的輪詢接手（issue #46）。 */
  protected handleUploaded(): void {
    this.detail.reload();
  }

  protected retryDocument(detail: KnowledgeBaseDetailView, document: KnowledgeDocumentView): void {
    if (this.isBusy(document)) return;
    this.actionError.set('');
    this.runForDocument(
      document,
      this.repository.retryKnowledgeDocument(detail.summary.id, document.id, document.latestVersionId),
      (result: RetryKnowledgeDocumentResult) => {
        if (result.status === 'ready' || result.status === 'partial-failure') {
          this.announce(result.data.name, result.data.status);
          this.detail.reload();
        } else if (result.status !== 'loading') {
          this.actionError.set(result.message);
        }
      },
      '目前無法重新處理，請稍後再試。',
    );
  }

  /** 「只看待確認」篩選；不篩選版本歷程與活動紀錄，只影響內容頁籤的清單。 */
  protected visibleDocuments(detail: KnowledgeBaseDetailView): readonly KnowledgeDocumentView[] {
    return this.showOnlyAwaitingApproval()
      ? detail.documents.filter((document) => isAwaitingApprovalKnowledgeDocument(document))
      : detail.documents;
  }

  protected toggleOnlyAwaitingApproval(): void {
    this.showOnlyAwaitingApproval.update((value) => !value);
  }

  protected isSelectedForApproval(document: KnowledgeDocumentView): boolean {
    return this.selectedForApproval().has(document.id);
  }

  protected toggleApprovalSelection(document: KnowledgeDocumentView): void {
    this.selectedForApproval.update((ids) => {
      const next = new Set(ids);
      if (next.has(document.id)) next.delete(document.id);
      else next.add(document.id);
      return next;
    });
  }

  protected approveSelected(detail: KnowledgeBaseDetailView): void {
    const selectedIds = this.selectedForApproval();
    if (this.approvingBatch() || selectedIds.size === 0) return;
    const versionIds = detail.documents
      .filter((document) => selectedIds.has(document.id))
      .map((document) => document.latestVersionId);

    this.approvingBatch.set(true);
    this.batchApproveError.set('');
    this.repository
      .approveKnowledgeVersions(detail.summary.id, versionIds)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result: ApproveKnowledgeVersionsResult) => {
          this.approvingBatch.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.selectedForApproval.set(new Set());
            this.liveMessage.set(`已確認 ${result.data.length} 個版本生效。`);
            this.detail.reload();
          } else if (result.status !== 'loading') {
            this.batchApproveError.set(result.message);
          }
        },
        error: () => {
          this.approvingBatch.set(false);
          this.batchApproveError.set('目前無法批次確認生效，請稍後再試。');
        },
      });
  }

  protected openReview(detail: KnowledgeBaseDetailView, document: KnowledgeDocumentView): void {
    const data: ReviewDialogData = {
      knowledgeBaseId: detail.summary.id,
      document,
      canManage: detail.summary.viewerCanManage,
    };
    this.dialog
      .open(ReviewDialogComponent, {
        data,
        width: 'min(48rem, calc(100vw - 2rem))',
        ariaLabelledBy: 'knowledge-review-title',
        restoreFocus: true,
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((changed: boolean | undefined) => {
        if (changed) this.detail.reload();
      });
  }

  protected saveSharing(detail: KnowledgeBaseDetailView, sharing: KnowledgeSharingView): void {
    if (this.savingSharing()) return;
    this.savingSharing.set(true);
    this.repository
      .updateKnowledgeSharing(detail.summary.id, sharing)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result: UpdateKnowledgeSharingResult) => {
          this.savingSharing.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.sharingFeedback.set(`分享設定已儲存：${SHARING_SCOPE_LABELS[result.data.scope]}。`);
            this.detail.reload();
          } else if (result.status !== 'loading') {
            this.sharingFeedback.set(result.message);
          }
        },
        error: () => {
          this.savingSharing.set(false);
          this.sharingFeedback.set('目前無法儲存分享設定，請稍後再試。');
        },
      });
  }

  protected confirmDeleteKnowledgeBase(detail: KnowledgeBaseDetailView): void {
    this.openDeleteDialog({
      kind: 'knowledge-base',
      name: detail.summary.name,
      itemCount: detail.documents.length,
    });
  }

  protected confirmDeleteDocument(document: KnowledgeDocumentView): void {
    this.openDeleteDialog({ kind: 'document', document });
  }

  protected closeDeleteDialog(): void {
    this.dialog.closeAll();
  }

  protected deleteConfirmed(): void {
    const pending = this.pendingDeletion();
    const knowledgeBaseId = this.knowledgeBaseId();
    if (pending === null || this.deleting()) return;

    this.deleting.set(true);
    this.deleteError.set('');
    const request: Observable<DeleteKnowledgeResult> =
      pending.kind === 'knowledge-base'
        ? this.repository.deleteKnowledgeBase(knowledgeBaseId)
        : this.repository.deleteKnowledgeDocument(knowledgeBaseId, pending.document.id);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => this.deleted(pending, result),
      error: () => {
        this.deleting.set(false);
        this.deleteError.set('目前無法刪除，請稍後再試。');
      },
    });
  }

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/knowledge');
  }

  protected deletionTitle(pending: PendingDeletion): string {
    return pending.kind === 'knowledge-base'
      ? `刪除知識庫「${pending.name}」？`
      : `刪除「${pending.document.name}」？`;
  }

  protected deletionDetail(pending: PendingDeletion): string {
    return pending.kind === 'knowledge-base'
      ? `其中的 ${pending.itemCount} 項文件與 FAQ、分享設定都會一併刪除，已連接的助理將無法再引用。這個動作無法復原。`
      : '這份內容的所有版本都會刪除，助理將無法再引用。這個動作無法復原。';
  }

  private openDeleteDialog(pending: PendingDeletion): void {
    const content = this.deleteDialog();
    if (!content) return;
    this.pendingDeletion.set(pending);
    this.deleteError.set('');
    this.dialog
      .open(content, {
        width: 'min(32rem, calc(100vw - 2rem))',
        autoFocus: '#knowledge-delete-cancel',
        restoreFocus: true,
        ariaLabelledBy: 'knowledge-delete-title',
        ariaDescribedBy: 'knowledge-delete-detail',
        role: 'alertdialog',
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (!this.deleting()) this.pendingDeletion.set(null);
      });
  }

  private deleted(pending: PendingDeletion, result: DeleteKnowledgeResult): void {
    this.deleting.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.pendingDeletion.set(null);
      this.closeDeleteDialog();
      if (pending.kind === 'knowledge-base') {
        this.returnToList();
      } else {
        this.liveMessage.set(`已刪除「${pending.document.name}」。`);
        this.detail.reload();
      }
    } else if (result.status !== 'loading') {
      this.deleteError.set(result.message);
    }
  }

  /** 送出單一文件的請求：進行中停用該列按鈕，元件銷毀時取消。 */
  private runForDocument<T>(
    document: KnowledgeDocumentView,
    request: Observable<T>,
    handle: (result: T) => void,
    failureMessage: string,
  ): void {
    const settle = () =>
      this.busyDocuments.update((ids) => new Set([...ids].filter((id) => id !== document.id)));
    this.busyDocuments.update((ids) => new Set([...ids, document.id]));
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        settle();
        handle(result);
      },
      error: () => {
        settle();
        this.actionError.set(failureMessage);
      },
    });
  }

  private announceStatusChanges(view: LoadedView<KnowledgeBaseDetailView>): void {
    if (view.status !== 'ready' && view.status !== 'partial-failure') return;
    const previous = this.previousStatuses;
    this.previousStatuses = new Map(view.data.documents.map((document) => [document.id, document.status]));
    if (previous === null) return;
    const changed = view.data.documents.filter((document) => {
      const before = previous.get(document.id);
      return before !== undefined && before !== document.status;
    });
    const last = changed.at(-1);
    if (last !== undefined) this.announce(last.name, last.status);
  }

  private announce(name: string, status: KnowledgeDocumentStatus): void {
    this.liveMessage.set(`「${name}」${DOCUMENT_STATUS_LABELS[status].label}`);
  }
}
