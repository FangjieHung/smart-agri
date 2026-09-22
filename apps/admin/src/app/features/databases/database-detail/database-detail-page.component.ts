import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { AssistantStatus } from '../../../core/domain/assistant.model';
import type {
  DatabaseDetailView,
  DatabaseFieldError,
  DatabaseFieldView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';
import type { PreviewDatabaseEntryResult } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent, type StatusTone } from '../../../shared/ui/status-badge/status-badge.component';
import { FormDesignerComponent } from '../form-designer/form-designer.component';
import { FormTrialComponent } from '../form-designer/form-trial/form-trial.component';
import { RecordsTableComponent } from '../records-table/records-table.component';
import { TrendViewComponent } from '../trend-view/trend-view.component';

type DatabaseTabId = 'form' | 'records' | 'trends' | 'assistants' | 'access';

interface DatabaseTab {
  readonly id: DatabaseTabId;
  readonly label: string;
}

const TABS: readonly DatabaseTab[] = [
  { id: 'form', label: '表單設計' },
  { id: 'records', label: '收集紀錄' },
  { id: 'trends', label: '趨勢比較' },
  { id: 'assistants', label: '已連接助理' },
  { id: 'access', label: '權限' },
];

const ASSISTANT_STATUS: Record<AssistantStatus, { readonly label: string; readonly tone: StatusTone }> = {
  draft: { label: '草稿', tone: 'neutral' },
  ready: { label: '可發布', tone: 'info' },
  published: { label: '已發布', tone: 'success' },
  paused: { label: '已暫停', tone: 'warning' },
};

@Component({
  selector: 'app-database-detail-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    StatusBadgeComponent,
    FormDesignerComponent,
    FormTrialComponent,
    RecordsTableComponent,
    TrendViewComponent,
  ],
  templateUrl: './database-detail-page.component.html',
  styleUrl: './database-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  /** repository 為同步 mock，異動後遞增此值讓畫面重新讀取。 */
  private readonly revision = signal(0);

  protected readonly tabs = TABS;
  protected readonly databaseId = computed(() => this.params().get('id') ?? '');
  protected readonly activeTab = computed<DatabaseTab>(
    () => TABS.find((tab) => tab.id === this.params().get('tab')) ?? TABS[0],
  );
  protected readonly view = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.getDatabaseDetail(accountId, this.databaseId()) : null;
  });

  /** 只在紀錄與趨勢頁籤讀取收集紀錄；非指定資料管理者會得到 permission-denied。 */
  protected readonly tracking = computed(() => {
    const tab = this.activeTab().id;
    const accountId = this.session.activeAccountId();
    if (!accountId || (tab !== 'records' && tab !== 'trends')) return null;
    return this.repository.getDatabaseTracking(accountId, this.databaseId());
  });

  protected readonly subjects = computed(() => {
    const tracking = this.tracking();
    return tracking?.status === 'ready' || tracking?.status === 'partial-failure' ? tracking.data.subjects : [];
  });

  protected readonly selectedSubject = computed(() => {
    const subjects = this.subjects();
    const requested = this.query().get('subject');
    return subjects.find((subject) => subject.id === requested) ?? subjects[0] ?? null;
  });

  protected readonly fieldErrors = signal<readonly DatabaseFieldError[]>([]);
  protected readonly designerFeedback = signal('');
  protected readonly trialResult = signal<PreviewDatabaseEntryResult | null>(null);

  protected assistantStatus(status: AssistantStatus) {
    return ASSISTANT_STATUS[status];
  }

  protected saveFields(detail: DatabaseDetailView, fields: readonly DatabaseFieldView[]): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.updateDatabaseFields(accountId, detail.summary.id, fields);

    if (result.status === 'validation-failed') {
      this.fieldErrors.set(result.errors);
      this.designerFeedback.set('');
    } else if (result.status === 'permission-denied') {
      this.fieldErrors.set([{ fieldId: null, message: result.message }]);
      this.designerFeedback.set('');
    } else if (result.status !== 'loading') {
      this.fieldErrors.set([]);
      this.trialResult.set(null);
      this.designerFeedback.set(`表單已儲存：共 ${result.data.length} 個欄位。`);
      this.revision.update((value) => value + 1);
    }
  }

  protected runTrial(detail: DatabaseDetailView, answers: DatabaseTrialAnswers): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    this.trialResult.set(this.repository.previewDatabaseEntry(accountId, detail.summary.id, answers));
  }

  protected selectSubject(event: Event): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { subject: (event.target as HTMLSelectElement).value },
    });
  }

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/databases');
  }
}
