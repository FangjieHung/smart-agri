import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { finalize } from 'rxjs';
import { fromDateTimeLocalValue, isDueInPast, toDateTimeLocalValue } from '../../core/domain/case-due-time';
import {
  CASE_COMMENT_REQUIRED_MESSAGE,
  CASE_DUE_IN_PAST_MESSAGE,
  CASE_DUE_REQUIRED_MESSAGE,
  CASE_GROUP_REQUIRED_MESSAGE,
  CASE_NOTE_MAX_LENGTH,
  CASE_REASON_REQUIRED_MESSAGE,
  CASE_REQUEST_INFO_NOTE_REQUIRED_MESSAGE,
  CASE_RESOLUTION_REQUIRED_MESSAGE,
  caseActionLabel,
  type CaseAction,
  type CaseDetailView,
} from '../../core/domain/case.model';
import type { CaseGroupView } from '../../core/domain/case-settings.model';
import { CasesRepository, type CaseActionInput, type CaseActionResult } from '../../core/repositories/cases.repository';

/** 每個需要表單的動作：文字欄位的標籤、是否必填與必填訊息、送出按鈕。受理與繼續處理按一下就送出。 */
interface ActionForm {
  readonly textLabel: string;
  readonly textField: 'note' | 'resolution' | 'reason';
  readonly requiredMessage: string | null;
  readonly submitLabel: string;
}

const FORMS: Readonly<Partial<Record<CaseAction, ActionForm>>> = {
  'request-info': { textLabel: '需要補充的資料', textField: 'note', requiredMessage: CASE_REQUEST_INFO_NOTE_REQUIRED_MESSAGE, submitLabel: '送出補件要求' },
  comment: { textLabel: '補充內容', textField: 'note', requiredMessage: CASE_COMMENT_REQUIRED_MESSAGE, submitLabel: '送出補充' },
  complete: { textLabel: '處理結果', textField: 'resolution', requiredMessage: CASE_RESOLUTION_REQUIRED_MESSAGE, submitLabel: '確認完成' },
  cancel: { textLabel: '取消原因', textField: 'reason', requiredMessage: CASE_REASON_REQUIRED_MESSAGE, submitLabel: '確認取消案件' },
  transfer: { textLabel: '說明（選填）', textField: 'note', requiredMessage: null, submitLabel: '確認轉組' },
  'set-due': { textLabel: '說明（選填）', textField: 'note', requiredMessage: null, submitLabel: '儲存新時限' },
};

/**
 * 案件詳情的動作區（issue #249；M7 計畫第 3 節 D、J）：只顯示後端 `allowedActions` 列出的動作；需要
 * 說明、處理結果或原因的動作用表單，轉組選承辦組，調整時限在送出前就提示「時限不能早於現在」。
 * 每個請求帶畫面上的 `eventCount`；`409` 時提示重新整理。只在 lazy 的案件頁使用。
 */
@Component({
  selector: 'app-case-actions',
  templateUrl: './case-actions.component.html',
  styleUrl: './case-actions.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CaseActionsComponent {
  private readonly cases = inject(CasesRepository);

  readonly detail = input.required<CaseDetailView>();
  /** 可轉入的承辦組（未封存）；目前的承辦組不會列出。 */
  readonly groups = input<readonly CaseGroupView[]>([]);
  /** 動作成功，帶回新的詳情。 */
  readonly changed = output<CaseDetailView>();
  /** `409` 後使用者按「重新整理」。 */
  readonly refresh = output<void>();

  protected readonly noteMaxLength = CASE_NOTE_MAX_LENGTH;
  protected readonly dueInPastMessage = CASE_DUE_IN_PAST_MESSAGE;
  protected readonly actionLabel = caseActionLabel;

  protected readonly active = signal<CaseAction | null>(null);
  protected readonly text = signal('');
  protected readonly groupId = signal('');
  protected readonly dueAt = signal('');
  protected readonly fieldError = signal<{ readonly field: 'text' | 'groupId' | 'dueAt'; readonly message: string } | null>(null);
  protected readonly message = signal('');
  protected readonly stale = signal(false);
  protected readonly saving = signal(false);

  protected readonly actions = computed(() => this.detail().allowedActions ?? []);
  protected readonly form = computed(() => {
    const action = this.active();
    return action ? FORMS[action] ?? null : null;
  });
  protected readonly textRequired = computed(() => {
    const form = this.form();
    if (!form?.requiredMessage) return false;
    return this.active() !== 'cancel' || this.detail().cancelReasonRequired;
  });
  protected readonly targetGroups = computed(() => {
    const current = this.detail().case.group.id;
    return this.groups().filter((group) => group.id !== current);
  });
  protected readonly dueInPast = computed(() => {
    const due = fromDateTimeLocalValue(this.dueAt());
    return this.active() === 'set-due' && due !== null && isDueInPast(due, new Date());
  });

  protected choose(action: CaseAction): void {
    this.message.set('');
    this.stale.set(false);
    this.fieldError.set(null);
    if (!FORMS[action]) {
      this.active.set(null);
      this.send(action, {});
      return;
    }
    this.active.set(action);
    this.text.set('');
    this.groupId.set(action === 'transfer' ? this.targetGroups()[0]?.id ?? '' : '');
    this.dueAt.set(action === 'set-due' ? toDateTimeLocalValue(new Date(this.detail().case.dueAt)) : '');
  }

  protected close(): void {
    this.active.set(null);
    this.fieldError.set(null);
  }

  protected setText(value: string): void {
    this.text.set(value);
    if (this.fieldError()?.field === 'text') this.fieldError.set(null);
  }

  protected setGroup(value: string): void {
    this.groupId.set(value);
    if (this.fieldError()?.field === 'groupId') this.fieldError.set(null);
  }

  protected setDue(value: string): void {
    this.dueAt.set(value);
    if (this.fieldError()?.field === 'dueAt') this.fieldError.set(null);
  }

  protected submit(): void {
    const action = this.active();
    const form = this.form();
    if (!action || !form || this.saving()) return;
    const text = this.text().trim();
    if (this.textRequired() && text.length === 0) {
      this.fail('text', form.requiredMessage ?? '');
      return;
    }
    const fields: Partial<Omit<CaseActionInput, 'eventCount'>> = text ? { [form.textField]: text } : {};
    if (action === 'transfer') {
      if (!this.groupId()) {
        this.fail('groupId', CASE_GROUP_REQUIRED_MESSAGE);
        return;
      }
      this.send(action, { ...fields, groupId: this.groupId() });
      return;
    }
    if (action === 'set-due') {
      const due = fromDateTimeLocalValue(this.dueAt());
      if (due === null) {
        this.fail('dueAt', CASE_DUE_REQUIRED_MESSAGE);
        return;
      }
      if (isDueInPast(due, new Date())) {
        this.fail('dueAt', CASE_DUE_IN_PAST_MESSAGE);
        return;
      }
      this.send(action, { ...fields, dueAt: due.toISOString() });
      return;
    }
    this.send(action, fields);
  }

  private send(action: CaseAction, fields: Partial<Omit<CaseActionInput, 'eventCount'>>): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.cases.act(this.detail().case.id, action, { eventCount: this.detail().case.eventCount, ...fields })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: (result) => this.handle(action, result),
        error: () => this.message.set('目前無法送出，請稍後再試。'),
      });
  }

  private handle(action: CaseAction, result: CaseActionResult): void {
    if (result.status === 'ready') {
      this.active.set(null);
      this.message.set(`已送出：${caseActionLabel(action)}。`);
      this.changed.emit(result.data);
    } else if (result.status === 'changed') {
      this.stale.set(true);
      this.message.set(result.message);
    } else if (result.status === 'validation-failed') {
      const errors = result.fieldErrors;
      if (errors.groupId) this.fail('groupId', errors.groupId);
      else if (errors.dueAt) this.fail('dueAt', errors.dueAt);
      else this.fail('text', errors.note ?? errors.resolution ?? errors.reason ?? result.message);
    } else if (result.status === 'permission-denied') {
      this.message.set(result.message);
    }
  }

  protected reload(): void {
    this.stale.set(false);
    this.message.set('');
    this.active.set(null);
    this.refresh.emit();
  }

  private fail(field: 'text' | 'groupId' | 'dueAt', message: string): void {
    this.fieldError.set({ field, message });
    this.message.set(message);
  }
}
