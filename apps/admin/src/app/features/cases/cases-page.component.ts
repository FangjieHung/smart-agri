import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
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
  caseRecordStateLabel,
  caseStatusLabel,
  type CaseEventView,
  type CaseListScope,
  type CaseListStatus,
  type CaseSummaryView,
} from '../../core/domain/case.model';
import type { CaseTypeView } from '../../core/domain/case-settings.model';
import { CaseSettingsRepository } from '../../core/repositories/case-settings.repository';
import { CasesRepository, type CaseField } from '../../core/repositories/cases.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';

type FormField = Extract<CaseField, 'typeId' | 'groupId' | 'dueAt' | 'title' | 'description'>;

/**
 * 案件頁（issue #248；M7 計畫第 3 節 J）：清單＋詳情，以 `?case=<id>` 選取（比照 `/app/issues`），
 * 以及「建立案件」表單：選類型 → 帶入承辦組與時限（可修改），時限早於現在時送出前就提示。
 * 連結到紀錄與對話串只顯示「能不能開啟」，從不顯示對話內容。只透過 lazy 路由載入。
 */
@Component({
  selector: 'app-cases-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent],
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
  protected readonly formatDueHours = formatDueHours;

  protected readonly scope = signal<CaseListScope>('all');
  protected readonly status = signal<CaseListStatus>('open');
  protected readonly typeFilter = signal('');
  protected readonly groupFilter = signal('');

  protected readonly selectedId = toSignal(this.route.queryParamMap.pipe(map((params) => params.get('case'))), {
    initialValue: this.route.snapshot.queryParamMap.get('case'),
  });
  protected readonly creating = signal(false);

  // 建立案件表單
  protected readonly formTypeId = signal('');
  protected readonly formGroupId = signal('');
  protected readonly formDueAt = signal('');
  protected readonly formTitle = signal('');
  protected readonly formDescription = signal('');
  protected readonly formErrors = signal<Partial<Record<FormField, string>>>({});
  protected readonly formMessage = signal('');
  protected readonly saving = signal(false);

  protected readonly listResource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId
        ? { accountId, scope: this.scope(), status: this.status(), typeId: this.typeFilter(), groupId: this.groupFilter() }
        : undefined;
    },
    stream: (params) => this.cases.list({
      scope: params.scope,
      status: params.status,
      typeId: params.typeId || undefined,
      groupId: params.groupId || undefined,
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
    this.cases.create({
      typeId: this.formTypeId(),
      groupId: this.formGroupId(),
      dueAt: due.toISOString(),
      title: this.formTitle().trim(),
      description: this.formDescription().trim(),
    }).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: (result) => {
        if (result.status === 'ready') {
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

  private clearError(field: FormField): void {
    if (!this.formErrors()[field]) return;
    const next = { ...this.formErrors() };
    delete next[field];
    this.formErrors.set(next);
  }
}

function pickFormErrors(errors: Readonly<Partial<Record<CaseField, string>>>): Partial<Record<FormField, string>> {
  const picked: Partial<Record<FormField, string>> = {};
  for (const field of ['typeId', 'groupId', 'dueAt', 'title', 'description'] as const) {
    if (errors[field]) picked[field] = errors[field];
  }
  return picked;
}
