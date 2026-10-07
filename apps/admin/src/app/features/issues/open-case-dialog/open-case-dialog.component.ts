import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal, type OnInit } from '@angular/core';
import { finalize } from 'rxjs';
import type { AssistantIssueOpenedCaseView, OpenCaseField } from '../../../core/domain/assistant-issue.model';
import {
  dueAtFromHours,
  formatDueHours,
  fromDateTimeLocalValue,
  isDueInPast,
  toDateTimeLocalValue,
} from '../../../core/domain/case-due-time';
import {
  CASE_DESCRIPTION_MAX_LENGTH,
  CASE_DUE_IN_PAST_MESSAGE,
  CASE_DUE_REQUIRED_MESSAGE,
  CASE_GROUP_REQUIRED_MESSAGE,
  CASE_TITLE_MAX_LENGTH,
  CASE_TITLE_REQUIRED_MESSAGE,
  CASE_TYPE_REQUIRED_MESSAGE,
} from '../../../core/domain/case.model';
import type { CaseTypeView } from '../../../core/domain/case-settings.model';
import { AssistantIssuesRepository } from '../../../core/repositories/assistant-issues.repository';
import { CaseSettingsRepository } from '../../../core/repositories/case-settings.repository';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog/confirm-dialog.component';

/** 409 已另開過：畫面帶著既有案件的 id，讓使用者直接打開它。 */
export interface OpenCaseConflict {
  readonly message: string;
  readonly caseId?: string;
}

/** 對話框的說明：送出後處理事項會以「非助理問題」結案。 */
export const OPEN_CASE_DETAIL = '這不是助理答錯，而是需要有人去做的事：建立案件後，這個處理事項會以「非助理問題」結案，兩邊互相連結。';

/**
 * 處理事項的「另開案件」對話框（issue #252；M7 計畫第 3 節 G）：預先帶入處理事項的標題與問題副本（可以修改），
 * 類型、承辦組與時限比照「建立案件」（選類型 → 帶入預設承辦組與「現在＋預設時限」，時限早於現在時送出前就提示）。
 * 只在 lazy 的處理事項頁使用；每次開啟都是新的元件，欄位不會延續到下一次。
 */
@Component({
  selector: 'app-open-case-dialog',
  imports: [ConfirmDialogComponent],
  templateUrl: './open-case-dialog.component.html',
  styleUrl: './open-case-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpenCaseDialogComponent implements OnInit {
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly settings = inject(CaseSettingsRepository);

  readonly issueId = input.required<string>();
  readonly issueTitle = input.required<string>();
  readonly question = input<string | null>(null);

  readonly cancelled = output<void>();
  readonly opened = output<AssistantIssueOpenedCaseView>();
  /** `409`：已另開過（帶 `caseId`）或處理事項剛被改過；由頁面重新整理並顯示訊息。 */
  readonly conflicted = output<OpenCaseConflict>();

  protected readonly detail = OPEN_CASE_DETAIL;
  protected readonly titleMaxLength = CASE_TITLE_MAX_LENGTH;
  protected readonly descriptionMaxLength = CASE_DESCRIPTION_MAX_LENGTH;
  protected readonly dueInPastMessage = CASE_DUE_IN_PAST_MESSAGE;
  protected readonly formatDueHours = formatDueHours;

  protected readonly typeId = signal('');
  protected readonly groupId = signal('');
  protected readonly dueAt = signal('');
  protected readonly title = signal('');
  protected readonly description = signal('');
  protected readonly errors = signal<Partial<Record<OpenCaseField, string>>>({});
  protected readonly message = signal('');
  protected readonly saving = signal(false);

  private readonly typesResource = repositoryResource({ params: () => true, stream: () => this.settings.listCaseTypes() });
  private readonly groupsResource = repositoryResource({ params: () => true, stream: () => this.settings.listCaseGroups() });
  protected readonly types = computed(() => {
    const view = this.typesResource.view();
    return view.status === 'ready' ? view.data.types : [];
  });
  protected readonly groups = computed(() => {
    const view = this.groupsResource.view();
    return view.status === 'ready' ? view.data.groups : [];
  });
  protected readonly selectedType = computed<CaseTypeView | undefined>(() => this.types().find((type) => type.id === this.typeId()));
  protected readonly dueInPast = computed(() => {
    const due = fromDateTimeLocalValue(this.dueAt());
    return due !== null && isDueInPast(due, new Date());
  });

  /** 開啟時帶入處理事項的標題（截到案件標題上限）與問題副本（截到說明上限），都可以修改。 */
  ngOnInit(): void {
    this.title.set(this.issueTitle().trim().slice(0, CASE_TITLE_MAX_LENGTH));
    this.description.set((this.question() ?? '').trim().slice(0, CASE_DESCRIPTION_MAX_LENGTH));
  }

  /** 選類型 → 帶入預設承辦組與「現在＋預設處理時限」，兩者都可以再修改（比照建立案件）。 */
  protected chooseType(typeId: string): void {
    this.typeId.set(typeId);
    const type = this.types().find((candidate) => candidate.id === typeId);
    if (type) {
      if (this.groups().some((group) => group.id === type.defaultGroup.id)) this.groupId.set(type.defaultGroup.id);
      this.dueAt.set(toDateTimeLocalValue(dueAtFromHours(new Date(), type.defaultDueHours)));
    }
    this.clearError('typeId');
  }

  protected setField(field: OpenCaseField, value: string): void {
    const setters: Record<OpenCaseField, (next: string) => void> = {
      typeId: (next) => this.chooseType(next),
      groupId: (next) => this.groupId.set(next),
      dueAt: (next) => this.dueAt.set(next),
      title: (next) => this.title.set(next),
      description: (next) => this.description.set(next),
    };
    setters[field](value);
    this.clearError(field);
    this.message.set('');
  }

  protected cancel(): void {
    if (!this.saving()) this.cancelled.emit();
  }

  protected submit(): void {
    if (this.saving()) return;
    const errors: Partial<Record<OpenCaseField, string>> = {};
    const due = fromDateTimeLocalValue(this.dueAt());
    if (!this.typeId()) errors.typeId = CASE_TYPE_REQUIRED_MESSAGE;
    if (!this.groupId()) errors.groupId = CASE_GROUP_REQUIRED_MESSAGE;
    if (due === null) errors.dueAt = CASE_DUE_REQUIRED_MESSAGE;
    else if (isDueInPast(due, new Date())) errors.dueAt = CASE_DUE_IN_PAST_MESSAGE;
    if (this.title().trim().length === 0) errors.title = CASE_TITLE_REQUIRED_MESSAGE;
    this.errors.set(errors);
    if (Object.keys(errors).length > 0 || due === null) {
      this.message.set(Object.values(errors)[0] ?? '');
      return;
    }
    this.saving.set(true);
    this.message.set('');
    this.issues.openCase(
      this.issueId(),
      {
        typeId: this.typeId(),
        groupId: this.groupId(),
        dueAt: due.toISOString(),
        title: this.title().trim(),
        description: this.description().trim(),
      },
      { activeTypeIds: this.types().map((type) => type.id), groupIds: this.groups().map((group) => group.id) },
    ).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: (result) => {
        if (result.status === 'ready') {
          this.opened.emit(result.data);
        } else if (result.status === 'validation-failed') {
          this.errors.set({ ...result.fieldErrors });
          this.message.set(result.message);
        } else if (result.status === 'conflict') {
          this.conflicted.emit({ message: result.message, caseId: result.caseId });
        } else {
          this.message.set(result.message);
        }
      },
      error: () => this.message.set('目前無法另開案件，請稍後再試。'),
    });
  }

  private clearError(field: OpenCaseField): void {
    if (!this.errors()[field]) return;
    const next = { ...this.errors() };
    delete next[field];
    this.errors.set(next);
  }
}
