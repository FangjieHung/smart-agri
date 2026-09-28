import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { fmtDateTime } from '../../../../core/date-utils';
import {
  isAwaitingApprovalKnowledgeDocument,
  KNOWLEDGE_VERSION_STATE_LABELS,
  type KnowledgeChunkView,
  type KnowledgeDocumentDetailView,
  type KnowledgeDocumentView,
  type KnowledgeVersionPreviewView,
} from '../../../../core/domain/knowledge-base.model';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { ITEM_KIND_LABELS } from '../knowledge-labels';

export interface ReviewDialogData {
  readonly knowledgeBaseId: string;
  readonly document: KnowledgeDocumentView;
  readonly canManage: boolean;
}

/** 讀取中或載入失敗的簡化狀態，只有這個對話框自己用得到。 */
type SimpleView<T> = RepositoryView<T> | { readonly status: 'error' };

const KNOWLEDGE_ACTIVITY_LABELS: Readonly<Record<string, string>> = {
  'document-uploaded': '上傳文件',
  'version-uploaded': '上傳新版本',
  'version-retried': '重新處理',
  'version-approved': '確認生效',
  'chunk-excluded': '排除段落',
  'chunk-included': '取消排除段落',
  'document-disabled': '緊急停用',
  'document-enabled': '恢復使用',
  'document-deleted': '刪除',
};

@Component({
  selector: 'app-knowledge-review-dialog',
  imports: [MatDialogModule],
  templateUrl: './review-dialog.component.html',
  styleUrl: './review-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewDialogComponent {
  protected readonly data = inject<ReviewDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ReviewDialogComponent>);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly detailView = signal<SimpleView<KnowledgeDocumentDetailView>>({ status: 'loading' });
  protected readonly previewView = signal<SimpleView<KnowledgeVersionPreviewView>>({ status: 'loading' });

  protected readonly effectiveDate = signal('');
  protected readonly approving = signal(false);
  protected readonly approveError = signal('');

  protected readonly disableReason = signal('');
  protected readonly disabling = signal(false);
  protected readonly disableError = signal('');
  protected readonly enabling = signal(false);

  protected readonly busyChunkId = signal<string | null>(null);
  protected readonly chunkError = signal('');

  /** 這次開啟對話框以後是否真的變更過資料；關閉時告訴呼叫端要不要重新讀取列表。 */
  private changed = false;

  protected readonly activityLabels = KNOWLEDGE_ACTIVITY_LABELS;
  protected readonly versionStateLabels = KNOWLEDGE_VERSION_STATE_LABELS;
  protected readonly kindLabels = ITEM_KIND_LABELS;
  protected readonly fmtDateTime = fmtDateTime;
  protected readonly isAwaitingApproval = isAwaitingApprovalKnowledgeDocument;

  constructor() {
    this.reloadDetail();
  }

  protected close(): void {
    this.dialogRef.close(this.changed);
  }

  protected retryLoad(): void {
    this.reloadDetail();
  }

  private reloadDetail(): void {
    this.detailView.set({ status: 'loading' });
    this.repository
      .getKnowledgeDocumentDetail(this.data.knowledgeBaseId, this.data.document.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (view) => {
          this.detailView.set(view);
          if (view.status === 'ready' || view.status === 'partial-failure') {
            this.loadPreview(view.data.document.latestVersionId);
          }
        },
        error: () => this.detailView.set({ status: 'error' }),
      });
  }

  private loadPreview(versionId: string): void {
    this.previewView.set({ status: 'loading' });
    this.repository
      .previewKnowledgeVersion(this.data.knowledgeBaseId, this.data.document.id, versionId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (view) => this.previewView.set(view),
        error: () => this.previewView.set({ status: 'error' }),
      });
  }

  protected approve(document: KnowledgeDocumentView): void {
    if (this.approving()) return;
    this.approving.set(true);
    this.approveError.set('');
    const effectiveFrom = this.effectiveDate().trim();
    this.repository
      .approveKnowledgeVersions(
        this.data.knowledgeBaseId,
        [document.latestVersionId],
        effectiveFrom.length === 0 ? undefined : new Date(effectiveFrom).toISOString(),
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.approving.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.changed = true;
            this.effectiveDate.set('');
            this.reloadDetail();
          } else if (result.status !== 'loading') {
            this.approveError.set(result.message);
          }
        },
        error: () => {
          this.approving.set(false);
          this.approveError.set('目前無法確認生效，請稍後再試。');
        },
      });
  }

  protected disable(document: KnowledgeDocumentView): void {
    if (this.disabling()) return;
    const reason = this.disableReason().trim();
    if (reason.length === 0) {
      this.disableError.set('請說明緊急停用的原因。');
      return;
    }
    this.disabling.set(true);
    this.disableError.set('');
    this.repository
      .disableKnowledgeDocument(this.data.knowledgeBaseId, document.id, reason)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.disabling.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.changed = true;
            this.disableReason.set('');
            this.reloadDetail();
          } else if (result.status !== 'loading') {
            this.disableError.set(result.message);
          }
        },
        error: () => {
          this.disabling.set(false);
          this.disableError.set('目前無法停用，請稍後再試。');
        },
      });
  }

  protected enable(document: KnowledgeDocumentView): void {
    if (this.enabling()) return;
    this.enabling.set(true);
    this.disableError.set('');
    this.repository
      .enableKnowledgeDocument(this.data.knowledgeBaseId, document.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.enabling.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.changed = true;
            this.reloadDetail();
          } else if (result.status !== 'loading') {
            this.disableError.set(result.message);
          }
        },
        error: () => {
          this.enabling.set(false);
          this.disableError.set('目前無法恢復使用，請稍後再試。');
        },
      });
  }

  protected toggleChunk(document: KnowledgeDocumentView, chunk: KnowledgeChunkView): void {
    if (this.busyChunkId() !== null) return;
    this.busyChunkId.set(chunk.id);
    this.chunkError.set('');
    this.repository
      .updateKnowledgeChunkExclusion(
        this.data.knowledgeBaseId,
        document.id,
        document.latestVersionId,
        chunk.id,
        !chunk.excluded,
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busyChunkId.set(null);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.changed = true;
            this.loadPreview(document.latestVersionId);
          } else if (result.status !== 'loading') {
            this.chunkError.set(result.message);
          }
        },
        error: () => {
          this.busyChunkId.set(null);
          this.chunkError.set('目前無法變更這個段落，請稍後再試。');
        },
      });
  }
}
