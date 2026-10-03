import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { DetailLayoutComponent } from '@smart-agri/ui';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { AssistantStatus } from '../../../core/domain/assistant.model';
import {
  DATABASE_UPCOMING_FEATURE_MESSAGE,
  type DatabaseDetailView,
  type DatabaseFieldError,
  type DatabaseFieldView,
  type DatabaseTrialAnswers,
  type DatabaseUpcomingFeature,
} from '../../../core/domain/database.model';
import type { PreviewDatabaseEntryResult } from '../../../core/repositories/demo-repository';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { fmtDateTime } from '../../../core/date-utils';
import { FIELD_TYPE_LABELS } from '../database-labels';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent, type StatusTone } from '../../../shared/ui/status-badge/status-badge.component';
import { DatabaseAccessComponent } from '../database-access/database-access.component';
import { FormDesignerComponent } from '../form-designer/form-designer.component';
import { FormTrialComponent } from '../form-designer/form-trial/form-trial.component';
import { PeriodicReportComponent } from '../periodic-report/periodic-report.component';
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
    DetailLayoutComponent,
    PageHeaderComponent,
    StatePanelComponent,
    StatusBadgeComponent,
    DatabaseAccessComponent,
    FormDesignerComponent,
    FormTrialComponent,
    PeriodicReportComponent,
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
  /** 表單、試填與收集紀錄仍是同步 mock，異動後遞增此值讓畫面重新讀取。 */
  private readonly revision = signal(0);

  protected readonly tabs = TABS;
  protected readonly databaseId = computed(() => this.params().get('id') ?? '');
  protected readonly activeTab = computed<DatabaseTab>(
    () => TABS.find((tab) => tab.id === this.params().get('tab')) ?? TABS[0],
  );
  /** 換身分或網址 id 就重新讀取；儲存表單或權限後 `reload()`。 */
  private readonly detail = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, databaseId: this.databaseId() } : undefined;
    },
    stream: ({ databaseId }) => this.repository.getDatabaseDetail(databaseId),
  });
  protected readonly view = this.detail.view;

  /** API 模式尚未開放的功能（`DatabaseUpcomingFeature`）；mock 模式是空集合。 */
  protected readonly upcoming = computed<ReadonlySet<DatabaseUpcomingFeature>>(() => {
    const result = this.view();
    return new Set(result.status === 'ready' || result.status === 'partial-failure' ? result.data.upcomingFeatures : []);
  });
  protected readonly upcomingMessage = DATABASE_UPCOMING_FEATURE_MESSAGE;

  /** 只在紀錄與趨勢頁籤讀取收集紀錄；非指定資料管理者會得到 permission-denied。 */
  protected readonly tracking = computed(() => {
    this.revision();
    const tab = this.activeTab().id;
    const accountId = this.session.activeAccountId();
    if (!accountId || (tab !== 'records' && tab !== 'trends') || this.upcoming().has('records')) return null;
    return this.repository.getDatabaseTracking(accountId, this.databaseId());
  });

  protected readonly subjects = computed(() => {
    const tracking = this.tracking();
    return tracking?.status === 'ready' || tracking?.status === 'partial-failure' ? tracking.data.subjects : [];
  });

  /** 助理規則開啟「定期回報」時才有；排程與摘要都由 repository 算好。 */
  protected readonly periodicReports = computed(() => {
    const tracking = this.tracking();
    return tracking?.status === 'ready' || tracking?.status === 'partial-failure'
      ? tracking.data.periodicReports
      : [];
  });

  protected readonly selectedSubject = computed(() => {
    const subjects = this.subjects();
    const requested = this.query().get('subject');
    return subjects.find((subject) => subject.id === requested) ?? subjects[0] ?? null;
  });

  protected readonly fieldErrors = signal<readonly DatabaseFieldError[]>([]);
  protected readonly designerFeedback = signal('');
  protected readonly trialResult = signal<PreviewDatabaseEntryResult | null>(null);

  /** 權限變更後重新讀取詳情，讓「你的權限」與紀錄頁籤立刻跟著變。 */
  protected reload(): void {
    this.revision.update((value) => value + 1);
    this.detail.reload();
  }

  protected updatedAt(iso: string): string {
    return fmtDateTime(iso);
  }

  protected fieldTypeLabel(field: DatabaseFieldView): string {
    return FIELD_TYPE_LABELS[field.type];
  }

  /** 唯讀欄位清單的補充說明：選項、量尺範圍或單位；沒有時為空字串。 */
  protected fieldDetail(field: DatabaseFieldView): string {
    if (field.options.length > 0) return `選項：${field.options.join('、')}`;
    if (field.scale !== null) {
      return `${field.scale.min}（${field.scale.minLabel}）到 ${field.scale.max}（${field.scale.maxLabel}）`;
    }
    return field.unit ? `單位：${field.unit}` : '';
  }

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
      this.reload();
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
