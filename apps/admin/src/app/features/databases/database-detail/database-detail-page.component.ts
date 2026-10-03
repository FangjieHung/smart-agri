import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
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
import { FormDesignerComponent, type FormDesignerFailure } from '../form-designer/form-designer.component';
import { FormTrialComponent } from '../form-designer/form-trial/form-trial.component';
import { PeriodicReportComponent } from '../periodic-report/periodic-report.component';
import { PeriodSummaryComponent } from '../period-summary/period-summary.component';
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
    PeriodSummaryComponent,
    RecordsTableComponent,
    TrendViewComponent,
  ],
  templateUrl: './database-detail-page.component.html',
  styleUrl: './database-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseDetailPageComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

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

  /**
   * 只在紀錄與趨勢頁籤讀取收集紀錄與每位追蹤對象的比較；非指定資料管理者會得到 permission-denied。
   * 換身分、換網址 id 就重新讀取。
   */
  private readonly trackingResource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      const tab = this.activeTab().id;
      const detail = this.view();
      if (!accountId || (tab !== 'records' && tab !== 'trends')) return undefined;
      if (detail.status !== 'ready' && detail.status !== 'partial-failure') return undefined;
      return { accountId, databaseId: this.databaseId() };
    },
    stream: ({ databaseId }) => this.repository.getDatabaseTracking(databaseId),
  });
  protected readonly tracking = this.trackingResource.view;

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
  protected readonly designerFailure = signal<FormDesignerFailure | null>(null);
  protected readonly saving = signal(false);
  protected readonly trialResult = signal<PreviewDatabaseEntryResult | null>(null);
  protected readonly trialFailure = signal('');
  protected readonly trialRunning = signal(false);
  /** 這個畫面剛存成功的版本：詳情重新讀回來之前，連續儲存要以它為基準，不會被當成過期。 */
  private readonly savedFormVersion = signal(0);

  /** 權限變更或表單版本過期後重新讀取詳情；草稿會換成伺服器上最新的表單。 */
  protected reload(): void {
    this.designerFailure.set(null);
    this.detail.reload();
    this.trackingResource.reload();
  }

  /** 收集紀錄讀取失敗（5xx、連線中斷）後重試；與「沒有紀錄」分開顯示。 */
  protected reloadTracking(): void {
    this.trackingResource.reload();
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
    if (this.saving()) return;
    this.saving.set(true);
    this.designerFeedback.set('');
    this.designerFailure.set(null);
    const baseVersion = Math.max(detail.formVersion, this.savedFormVersion());

    this.repository
      .updateDatabaseFields(detail.summary.id, fields, baseVersion)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          if (result.status === 'validation-failed') {
            this.fieldErrors.set(result.errors);
          } else if (result.status === 'conflict') {
            this.fieldErrors.set([]);
            this.designerFailure.set({ kind: 'conflict', message: result.message });
          } else if (result.status === 'permission-denied') {
            this.fieldErrors.set([]);
            this.designerFailure.set({ kind: 'denied', message: result.message });
          } else if (result.status !== 'loading') {
            this.fieldErrors.set([]);
            this.trialResult.set(null);
            this.trialFailure.set('');
            this.savedFormVersion.set(result.data.formVersion);
            this.designerFeedback.set(`表單已儲存：共 ${result.data.fields.length} 個欄位。`);
            this.reload();
          }
        },
        // 5xx、連線中斷：什麼都沒存。草稿還在畫面上，按「儲存表單」就能再試一次。
        error: () => {
          this.saving.set(false);
          this.designerFailure.set({
            kind: 'failed',
            message: '目前無法儲存表單，你的修改還留在畫面上。請稍後再按一次「儲存表單」。',
          });
        },
      });
  }

  protected runTrial(detail: DatabaseDetailView, answers: DatabaseTrialAnswers): void {
    if (this.trialRunning()) return;
    this.trialRunning.set(true);
    this.trialFailure.set('');

    this.repository
      .previewDatabaseEntry(detail.summary.id, answers)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.trialRunning.set(false);
          this.trialResult.set(result);
        },
        error: () => {
          this.trialRunning.set(false);
          this.trialResult.set(null);
          this.trialFailure.set('目前無法試填，請稍後再試一次。你填的內容還在畫面上。');
        },
      });
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
