import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { AssistantStatus } from '../../../core/domain/assistant.model';
import type {
  KnowledgeBaseDetailView,
  KnowledgeDocumentId,
  KnowledgeSharingView,
} from '../../../core/domain/knowledge-base.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent, type StatusTone } from '../../../shared/ui/status-badge/status-badge.component';
import { DocumentRowComponent } from '../components/document-row/document-row.component';
import { DOCUMENT_STATUS_LABELS, SHARING_SCOPE_LABELS } from '../components/knowledge-labels';
import { SharingPanelComponent } from '../components/sharing-panel/sharing-panel.component';

/** Demo 模擬處理時，每一個狀態停留的時間。 */
export const DEMO_PROCESSING_STEP_MS = 900;

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

@Component({
  selector: 'app-knowledge-detail-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    StatusBadgeComponent,
    DocumentRowComponent,
    SharingPanelComponent,
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
  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  /** repository 為同步 mock，異動後遞增此值讓畫面重新讀取。 */
  private readonly revision = signal(0);
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();

  protected readonly tabs = TABS;
  protected readonly knowledgeBaseId = computed(() => this.params().get('id') ?? '');
  protected readonly activeTab = computed<KnowledgeTab>(
    () => TABS.find((tab) => tab.id === this.params().get('tab')) ?? TABS[0],
  );
  protected readonly view = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId
      ? this.repository.getKnowledgeBaseDetail(accountId, this.knowledgeBaseId())
      : null;
  });
  protected readonly liveMessage = signal('');
  protected readonly sharingFeedback = signal('');

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.timers.forEach((timer) => clearTimeout(timer));
      this.timers.clear();
    });
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

  protected addDemoDocument(detail: KnowledgeBaseDetailView): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.addDemoKnowledgeDocument(accountId, detail.summary.id);
    if (result.status !== 'ready') return;

    this.announce(result.data.name, result.data.status);
    this.revision.update((value) => value + 1);
    this.scheduleStep(detail, result.data.id);
  }

  protected retryDocument(detail: KnowledgeBaseDetailView, documentId: KnowledgeDocumentId): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.retryKnowledgeDocument(accountId, detail.summary.id, documentId);
    if (result.status !== 'ready') return;

    this.announce(result.data.name, result.data.status);
    this.revision.update((value) => value + 1);
    this.scheduleStep(detail, documentId);
  }

  protected saveSharing(detail: KnowledgeBaseDetailView, sharing: KnowledgeSharingView): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.updateKnowledgeSharing(accountId, detail.summary.id, sharing);

    if (result.status === 'ready') {
      this.sharingFeedback.set(`分享設定已儲存：${SHARING_SCOPE_LABELS[result.data.scope]}。`);
      this.revision.update((value) => value + 1);
    } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.sharingFeedback.set(result.message);
    }
  }

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/knowledge');
  }

  /** Demo：以計時器逐步推進狀態，不會上傳或讀取任何檔案。 */
  private scheduleStep(detail: KnowledgeBaseDetailView, documentId: KnowledgeDocumentId): void {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      const accountId = this.session.activeAccountId();
      if (!accountId) return;
      const result = this.repository.advanceKnowledgeDocument(accountId, detail.summary.id, documentId);
      if (result.status !== 'ready') return;

      this.announce(result.data.name, result.data.status);
      this.revision.update((value) => value + 1);
      if (result.data.status === 'queued' || result.data.status === 'processing') {
        this.scheduleStep(detail, documentId);
      }
    }, DEMO_PROCESSING_STEP_MS);
    this.timers.add(timer);
  }

  private announce(name: string, status: keyof typeof DOCUMENT_STATUS_LABELS): void {
    this.liveMessage.set(`「${name}」${DOCUMENT_STATUS_LABELS[status].label}`);
  }
}
