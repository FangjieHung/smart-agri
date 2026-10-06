import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, type ParamMap } from '@angular/router';
import { finalize, map } from 'rxjs';
import {
  dueAtFromHours,
  formatDueHours,
  fromDateTimeLocalValue,
  isDueInPast,
  toDateTimeLocalValue,
} from '../../core/domain/case-due-time';
import {
  CASE_DESCRIPTION_MAX_LENGTH,
  CASE_DUE_IN_PAST_MESSAGE,
  CASE_DUE_REQUIRED_MESSAGE,
  CASE_GROUP_REQUIRED_MESSAGE,
  CASE_STATUSES,
  CASE_THREAD_UNAVAILABLE_TEXT,
  CASE_TITLE_MAX_LENGTH,
  CASE_TITLE_REQUIRED_MESSAGE,
  CASE_TYPE_REQUIRED_MESSAGE,
  caseEventLabel,
  caseOriginLabel,
  caseCreatedByText,
  caseRecordStateLabel,
  caseStatusLabel,
  isCaseOverdue,
  type CaseEventView,
  type CaseListScope,
  type CaseListStatus,
  type CaseSummaryView,
  type CaseView,
} from '../../core/domain/case.model';
import type { CaseTypeView } from '../../core/domain/case-settings.model';
import { CaseSettingsRepository } from '../../core/repositories/case-settings.repository';
import { CasesRepository, type CaseField } from '../../core/repositories/cases.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';
import { CaseActionsComponent } from './case-actions.component';
import { CaseStatisticsComponent } from './case-statistics.component';

type FormField = Extract<CaseField, 'typeId' | 'groupId' | 'dueAt' | 'title' | 'description'>;

/**
 * 案件頁（issue #248；M7 計畫第 3 節 J）：清單＋詳情，以 `?case=<id>` 選取（比照 `/app/issues`），
 * 以及「建立案件」表單：選類型 → 帶入承辦組與時限（可修改），時限早於現在時送出前就提示。
 * 連結到紀錄與對話串只顯示「能不能開啟」，從不顯示對話內容。詳情依身分與狀態只顯示能做的動作
 * （`CaseActionsComponent`，issue #249）與交接軌跡；已結案的案件只剩「另開新案」。管理者另有「瓶頸統計」
 * 分頁（`?view=statistics`，`CaseStatisticsComponent`，issue #251），從統計點開的數字以網址參數帶入清單篩選。
 * 只透過 lazy 路由載入。
 */
@Component({
  selector: 'app-cases-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent, CaseActionsComponent, CaseStatisticsComponent],
  templateUrl: './cases-page.component.html',
  styleUrl: './cases-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CasesPageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly cases = inject(CasesRepository);
  private readonly settings = inject(CaseSettingsRepository);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly statuses = CASE_STATUSES;
  protected readonly titleMaxLength = CASE_TITLE_MAX_LENGTH;
  protected readonly descriptionMaxLength = CASE_DESCRIPTION_MAX_LENGTH;
  protected readonly threadUnavailableText = CASE_THREAD_UNAVAILABLE_TEXT;
  protected readonly dueInPastMessage = CASE_DUE_IN_PAST_MESSAGE;
  protected readonly statusLabel = caseStatusLabel;
  protected readonly originLabel = caseOriginLabel;
  protected readonly recordStateLabel = caseRecordStateLabel;
  protected readonly createdByText = caseCreatedByText;
  protected readonly formatDueHours = formatDueHours;

  /**
   * 篩選可以從網址帶入（首頁「案件」卡片，issue #250；瓶頸統計點開的數字，issue #251）：
   * `?scope=&status=&overdue=&typeId=&groupId=&closedFrom=&closedTo=`。網址的這些參數改變時才套用，
   * 所以選取案件（只改 `case`）不會蓋掉使用者之後自己改的篩選。
   */
  protected readonly scope = signal<CaseListScope>('all');
  protected readonly status = signal<CaseListStatus>('open');
  protected readonly typeFilter = signal('');
  protected readonly groupFilter = signal('');
  protected readonly overdueOnly = signal(false);
  /** 只列這段期間（UTC 日）完成或取消的案件：只從統計點開時才有，畫面提供「清除」。 */
  protected readonly closedFrom = signal('');
  protected readonly closedTo = signal('');

  /** 管理者才有「瓶頸統計」分頁；其他人帶著 `?view=statistics` 進來也只看到清單。 */
  protected readonly isManager = computed(() => this.session.activeAccountId() !== null && this.cases.viewerIsManager());
  private readonly viewParam = toSignal(this.route.queryParamMap.pipe(map((params) => params.get('view'))), {
    initialValue: this.route.snapshot.queryParamMap.get('view'),
  });
  protected readonly showStatistics = computed(() => this.viewParam() === 'statistics' && this.isManager());

  protected readonly selectedId = toSignal(this.route.queryParamMap.pipe(map((params) => params.get('case'))), {
    initialValue: this.route.snapshot.queryParamMap.get('case'),
  });
  protected readonly creating = signal(false);

  constructor() {
    // 只比對篩選參數（不用 rxjs 的 distinctUntilChanged：初始 bundle 的共用 chunk 會因此多一個匯出）。
    let applied: string | null = null;
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const key = filterKey(params);
      if (key === applied) return;
      applied = key;
      this.applyFilters(params);
    });
  }

  // 建立案件表單
  protected readonly formTypeId = signal('');
  protected readonly formGroupId = signal('');
  protected readonly formDueAt = signal('');
  protected readonly formTitle = signal('');
  protected readonly formDescription = signal('');
  protected readonly formErrors = signal<Partial<Record<FormField, string>>>({});
  protected readonly formMessage = signal('');
  protected readonly saving = signal(false);
  /** 「另開新案」接續的舊案件（建立時以 `previousCaseId` 連結）。 */
  protected readonly formPreviousCase = signal<Pick<CaseView, 'id' | 'title'> | null>(null);

  /** 統計分頁顯示時不讀清單。 */
  protected readonly listResource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId && !this.showStatistics()
        ? {
          accountId, scope: this.scope(), status: this.status(), typeId: this.typeFilter(), groupId: this.groupFilter(),
          overdue: this.overdueOnly(), closedFrom: this.closedFrom(), closedTo: this.closedTo(),
        }
        : undefined;
    },
    stream: (params) => this.cases.list({
      scope: params.scope,
      status: params.status,
      typeId: params.typeId || undefined,
      groupId: params.groupId || undefined,
      ...(params.overdue ? { overdue: true } : {}),
      ...(params.closedFrom ? { closedFrom: params.closedFrom } : {}),
      ...(params.closedTo ? { closedTo: params.closedTo } : {}),
    }),
  });
  protected readonly listView = this.listResource.view;

  protected readonly detailResource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      const id = this.selectedId();
      return accountId && id ? { accountId, id } : undefined;
    },
    stream: (params) => this.cases.get(params.id),
  });
  protected readonly detailView = this.detailResource.view;

  /** 可以用來建立案件的類型（啟用中）與可選的承辦組（未封存）；也當清單的篩選選項。 */
  private readonly typesResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.settings.listCaseTypes(),
  });
  private readonly groupsResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.settings.listCaseGroups(),
  });
  protected readonly types = computed(() => {
    const view = this.typesResource.view();
    return view.status === 'ready' ? view.data.types : [];
  });
  protected readonly groups = computed(() => {
    const view = this.groupsResource.view();
    return view.status === 'ready' ? view.data.groups : [];
  });
  protected readonly selectedType = computed<CaseTypeView | undefined>(() =>
    this.types().find((type) => type.id === this.formTypeId()));

  /** 時限早於現在：輸入當下就提示（決定 H），送出前再檢查一次。 */
  protected readonly dueInPast = computed(() => {
    const due = fromDateTimeLocalValue(this.formDueAt());
    return due !== null && isDueInPast(due, new Date());
  });

  /** 篩選選項裡沒有的類型（已停用）與承辦組（已封存）：從統計點開時仍顯示為已選取。 */
  protected readonly unlistedType = computed(() => {
    const id = this.typeFilter();
    return id !== '' && !this.types().some((type) => type.id === id);
  });
  protected readonly unlistedGroup = computed(() => {
    const id = this.groupFilter();
    return id !== '' && !this.groups().some((group) => group.id === id);
  });

  protected clearClosedRange(): void {
    this.closedFrom.set('');
    this.closedTo.set('');
  }

  /** 清單上的「已逾期」：與後端 `CaseAttention.Overdue` 相同的判斷，以畫面讀取時的時間計算。 */
  protected isOverdue(item: CaseSummaryView): boolean {
    return isCaseOverdue(item, new Date());
  }

  protected select(item: Pick<CaseSummaryView, 'id'>): void {
    this.creating.set(false);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { case: item.id }, queryParamsHandling: 'merge' });
  }

  protected startCreate(): void {
    this.creating.set(true);
    this.formTypeId.set('');
    this.formGroupId.set('');
    this.formDueAt.set('');
    this.formTitle.set('');
    this.formDescription.set('');
    this.formErrors.set({});
    this.formMessage.set('');
    this.formPreviousCase.set(null);
  }

  /** 已結案的案件不能重開：另開新案，帶入舊案件的類型（仍啟用時）與標題，並連結舊案件。 */
  protected startFollowUp(item: CaseView): void {
    this.startCreate();
    if (this.types().some((type) => type.id === item.type.id)) this.chooseType(item.type.id);
    this.formTitle.set(item.title);
    this.formPreviousCase.set({ id: item.id, title: item.title });
  }

  /** 動作成功：重新讀取詳情與清單（狀態、承辦組、負責人都可能改變）。 */
  protected actionDone(): void {
    this.detailResource.reload();
    this.listResource.reload();
  }

  protected cancelCreate(): void {
    this.creating.set(false);
  }

  /** 選類型 → 帶入預設承辦組與「現在＋預設處理時限」，兩者都可以再修改。 */
  protected chooseType(typeId: string): void {
    this.formTypeId.set(typeId);
    const type = this.types().find((candidate) => candidate.id === typeId);
    if (type) {
      if (this.groups().some((group) => group.id === type.defaultGroup.id)) this.formGroupId.set(type.defaultGroup.id);
      this.formDueAt.set(toDateTimeLocalValue(dueAtFromHours(new Date(), type.defaultDueHours)));
    }
    this.clearError('typeId');
  }

  protected setField(field: FormField, value: string): void {
    const setters: Record<FormField, (next: string) => void> = {
      typeId: (next) => this.chooseType(next),
      groupId: (next) => this.formGroupId.set(next),
      dueAt: (next) => this.formDueAt.set(next),
      title: (next) => this.formTitle.set(next),
      description: (next) => this.formDescription.set(next),
    };
    setters[field](value);
    this.clearError(field);
    this.formMessage.set('');
  }

  protected submit(): void {
    if (this.saving()) return;
    const errors: Partial<Record<FormField, string>> = {};
    const due = fromDateTimeLocalValue(this.formDueAt());
    if (!this.formTypeId()) errors.typeId = CASE_TYPE_REQUIRED_MESSAGE;
    if (!this.formGroupId()) errors.groupId = CASE_GROUP_REQUIRED_MESSAGE;
    if (due === null) errors.dueAt = CASE_DUE_REQUIRED_MESSAGE;
    else if (isDueInPast(due, new Date())) errors.dueAt = CASE_DUE_IN_PAST_MESSAGE;
    if (this.formTitle().trim().length === 0) errors.title = CASE_TITLE_REQUIRED_MESSAGE;
    this.formErrors.set(errors);
    if (Object.keys(errors).length > 0 || due === null) {
      this.formMessage.set(Object.values(errors)[0] ?? '');
      return;
    }
    this.saving.set(true);
    this.formMessage.set('');
    const previous = this.formPreviousCase();
    this.cases.create({
      typeId: this.formTypeId(),
      groupId: this.formGroupId(),
      dueAt: due.toISOString(),
      title: this.formTitle().trim(),
      description: this.formDescription().trim(),
      ...(previous ? { previousCaseId: previous.id } : {}),
    }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: (result) => {
        if (result.status === 'ready') {
          this.formPreviousCase.set(null);
          this.listResource.reload();
          this.select(result.data.case);
        } else if (result.status === 'validation-failed') {
          this.formErrors.set(pickFormErrors(result.fieldErrors));
          this.formMessage.set(result.message);
        } else if (result.status === 'permission-denied') {
          this.formMessage.set(result.message);
        }
      },
      error: () => this.formMessage.set('目前無法建立案件，請稍後再試。'),
    });
  }

  protected eventLabel(event: CaseEventView): string {
    return caseEventLabel(event.action);
  }

  private applyFilters(params: ParamMap): void {
    this.scope.set(queryScope(params.get('scope')));
    this.status.set(queryStatus(params.get('status')));
    this.typeFilter.set(params.get('typeId') ?? '');
    this.groupFilter.set(params.get('groupId') ?? '');
    this.overdueOnly.set(params.get('overdue') === 'true');
    this.closedFrom.set(params.get('closedFrom') ?? '');
    this.closedTo.set(params.get('closedTo') ?? '');
  }

  private clearError(field: FormField): void {
    if (!this.formErrors()[field]) return;
    const next = { ...this.formErrors() };
    delete next[field];
    this.formErrors.set(next);
  }
}

const SCOPES: readonly CaseListScope[] = ['all', 'created', 'owned', 'my-groups'];

/** 網址裡屬於清單篩選的參數（不含 `case`、`view`）。 */
const FILTER_PARAMS = ['scope', 'status', 'typeId', 'groupId', 'overdue', 'closedFrom', 'closedTo'] as const;

function filterKey(params: ParamMap): string {
  return FILTER_PARAMS.map((name) => params.get(name) ?? '').join('&');
}

function queryScope(value: string | null): CaseListScope {
  return SCOPES.find((scope) => scope === value) ?? 'all';
}

function queryStatus(value: string | null): CaseListStatus {
  const statuses: readonly CaseListStatus[] = ['open', 'closed', 'all', ...CASE_STATUSES];
  return statuses.find((status) => status === value) ?? 'open';
}

function pickFormErrors(errors: Readonly<Partial<Record<CaseField, string>>>): Partial<Record<FormField, string>> {
  const picked: Partial<Record<FormField, string>> = {};
  for (const field of ['typeId', 'groupId', 'dueAt', 'title', 'description'] as const) {
    if (errors[field]) picked[field] = errors[field];
  }
  return picked;
}
