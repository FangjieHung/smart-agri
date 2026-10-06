import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { AccountId } from '../../../../core/domain/account.model';
import {
  CASE_DUE_INTEGER_MESSAGE,
  CASE_DUE_MAX_HOURS,
  CASE_DUE_RANGE_MESSAGE,
  CASE_DUE_UNIT_LABELS,
  dueHoursFromInput,
  dueInputFromHours,
  formatDueHours,
  type CaseDueUnit,
} from '../../../../core/domain/case-due-time';
import type {
  CaseGroupListView,
  CaseTypeInput,
  CaseTypeListView,
  CaseTypeView,
} from '../../../../core/domain/case-settings.model';
import {
  CASE_TYPE_DESCRIPTION_MAX_LENGTH,
  CASE_TYPE_NAME_MAX_LENGTH,
  CaseSettingsRepository,
  type CaseTypeField,
  type CaseTypeResult,
} from '../../../../core/repositories/case-settings.repository';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { OrganizationSettingsChanges } from '../../organization-settings-changes.service';

/** 正在編輯的表單：新增，或某個既有類型。 */
type Editing = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly typeId: string };

interface Draft {
  readonly name: string;
  readonly description: string;
  readonly groupId: string;
  readonly dueValue: string;
  readonly dueUnit: CaseDueUnit;
  readonly isActive: boolean;
}

interface GroupOption {
  readonly id: string;
  readonly label: string;
}

const EMPTY_DRAFT: Draft = { name: '', description: '', groupId: '', dueValue: '3', dueUnit: 'days', isActive: true };

/**
 * 系統設定的「案件類型」（issue #247，M7 計畫第 3 節 B、J），放在「承辦組」之後。只有管理者看得到：
 * 其他內部帳號雖然讀得到啟用中的類型（建立案件時要選），但不在設定頁顯示；外部客戶是 `403 case`。
 *
 * - 每個類型有名稱（1–40 字、不重複）、說明（0–500 字，助理提議開案時參考）、預設承辦組
 *   （只能選未封存的組）、預設處理時限（可用「天」或「小時」輸入，以小時保存，1–2,160 小時）。
 * - 停用、不刪除：停用的類型不能用來建立新案件，既有案件不受影響。
 * - 欄位錯誤顯示在各自的欄位下方；伺服器的錯誤（名稱重複、承辦組已封存）也是。
 */
@Component({
  selector: 'app-case-types-panel',
  imports: [NgTemplateOutlet],
  templateUrl: './case-types-panel.component.html',
  styleUrl: './case-types-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CaseTypesPanelComponent {
  private readonly repository = inject(CaseSettingsRepository);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  /** 承辦組改名、封存後重新讀取（預設承辦組的名稱與可選項目）。 */
  private readonly settingsChanges = inject(OrganizationSettingsChanges, { optional: true });

  protected readonly nameMaxLength = CASE_TYPE_NAME_MAX_LENGTH;
  protected readonly descriptionMaxLength = CASE_TYPE_DESCRIPTION_MAX_LENGTH;
  protected readonly dueUnitLabels = CASE_DUE_UNIT_LABELS;
  protected readonly dueUnits: readonly CaseDueUnit[] = ['days', 'hours'];
  protected readonly formatDue = formatDueHours;

  private readonly typesResource = rxResource<RepositoryView<CaseTypeListView>, AccountId | undefined>({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listCaseTypes({ includeInactive: true }),
    defaultValue: { status: 'loading' },
  });

  /** 預設承辦組的選項：未封存的組（一般清單）。 */
  private readonly groupsResource = rxResource<RepositoryView<CaseGroupListView>, AccountId | undefined>({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listCaseGroups(),
    defaultValue: { status: 'loading' },
  });

  /** 只有管理者才顯示；讀取失敗、外部客戶、非管理者一律不顯示這個區塊。 */
  protected readonly view = computed(() => {
    if (!this.typesResource.hasValue()) return null;
    const result = this.typesResource.value();
    return result.status === 'ready' && result.data.canManage ? result.data : null;
  });

  protected readonly editing = signal<Editing | null>(null);
  protected readonly draft = signal<Draft>(EMPTY_DRAFT);
  protected readonly fieldErrors = signal<Partial<Record<CaseTypeField, string>>>({});
  protected readonly saving = signal(false);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  /** 表單的承辦組選項；編輯中的類型若沿用已封存的組，也列出它（標示已封存）。 */
  protected readonly groupOptions = computed<readonly GroupOption[]>(() => {
    const groups = this.groupsResource.hasValue() ? this.groupsResource.value() : null;
    const options: GroupOption[] = groups?.status === 'ready'
      ? groups.data.groups.map((group) => ({ id: group.id, label: group.name }))
      : [];
    const editing = this.editing();
    const current = editing?.mode === 'edit' ? this.view()?.types.find((type) => type.id === editing.typeId) : undefined;
    if (current && !options.some((option) => option.id === current.defaultGroup.id)) {
      options.push({ id: current.defaultGroup.id, label: `${current.defaultGroup.name}（已封存）` });
    }
    return options;
  });

  /** 以天輸入時顯示換算後的小時數，讓管理者確認。 */
  protected readonly dueHint = computed(() => {
    const draft = this.draft();
    const hours = dueHoursFromInput(draft.dueValue, draft.dueUnit);
    if (typeof hours !== 'number') return `最長 ${CASE_DUE_MAX_HOURS / 24} 天（${CASE_DUE_MAX_HOURS.toLocaleString('en-US')} 小時），以日曆時間計算。`;
    return draft.dueUnit === 'days'
      ? `共 ${hours} 小時，以日曆時間計算（含週末與假日）。`
      : `以日曆時間計算（含週末與假日）。`;
  });

  constructor() {
    effect(() => {
      const change = this.settingsChanges?.saved();
      if (change && change.by !== this) {
        untracked(() => {
          this.typesResource.reload();
          this.groupsResource.reload();
        });
      }
    });
  }

  protected isEditing(type: CaseTypeView): boolean {
    const editing = this.editing();
    return editing?.mode === 'edit' && editing.typeId === type.id;
  }

  protected isCreating(): boolean {
    return this.editing()?.mode === 'create';
  }

  protected startCreate(): void {
    const firstGroup = this.groupOptions()[0]?.id ?? '';
    this.open({ mode: 'create' }, { ...EMPTY_DRAFT, groupId: firstGroup });
  }

  protected startEdit(type: CaseTypeView): void {
    const due = dueInputFromHours(type.defaultDueHours);
    this.open({ mode: 'edit', typeId: type.id }, {
      name: type.name,
      description: type.description,
      groupId: type.defaultGroup.id,
      dueValue: due.value,
      dueUnit: due.unit,
      isActive: type.isActive,
    });
  }

  protected cancel(): void {
    this.editing.set(null);
    this.fieldErrors.set({});
  }

  protected onText(field: 'name' | 'description' | 'dueValue', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;
    this.draft.update((draft) => ({ ...draft, [field]: value }));
    this.clearError(field === 'dueValue' ? 'defaultDueHours' : field);
  }

  protected onGroup(event: Event): void {
    const groupId = (event.target as HTMLSelectElement).value;
    this.draft.update((draft) => ({ ...draft, groupId }));
    this.clearError('defaultGroupId');
  }

  protected onUnit(event: Event): void {
    const dueUnit = (event.target as HTMLSelectElement).value as CaseDueUnit;
    this.draft.update((draft) => ({ ...draft, dueUnit }));
    this.clearError('defaultDueHours');
  }

  protected onActive(event: Event): void {
    const isActive = (event.target as HTMLInputElement).checked;
    this.draft.update((draft) => ({ ...draft, isActive }));
  }

  protected save(event: Event): void {
    event.preventDefault();
    const editing = this.editing();
    if (this.saving() || editing === null) return;
    const draft = this.draft();
    const hours = dueHoursFromInput(draft.dueValue, draft.dueUnit);
    if (typeof hours !== 'number') {
      this.fieldErrors.set({ defaultDueHours: hours === 'integer' ? CASE_DUE_INTEGER_MESSAGE : CASE_DUE_RANGE_MESSAGE });
      return;
    }
    const input: CaseTypeInput = {
      name: draft.name,
      description: draft.description,
      defaultGroupId: draft.groupId,
      defaultDueHours: hours,
      isActive: draft.isActive,
    };
    this.submit(
      editing.mode === 'create' ? this.repository.createCaseType(input) : this.repository.updateCaseType(editing.typeId, input),
      (type) => editing.mode === 'create' ? `已建立案件類型「${type.name}」。` : `已儲存案件類型「${type.name}」。`,
    );
  }

  /** 停用或重新啟用：其他欄位照原樣送出。 */
  protected setActive(type: CaseTypeView, isActive: boolean): void {
    if (this.saving()) return;
    this.editing.set(null);
    this.submit(
      this.repository.updateCaseType(type.id, {
        name: type.name,
        description: type.description,
        defaultGroupId: type.defaultGroup.id,
        defaultDueHours: type.defaultDueHours,
        isActive,
      }),
      (saved) => isActive
        ? `已重新啟用「${saved.name}」，可以用來建立案件。`
        : `已停用「${saved.name}」。之後不能再用它建立案件，既有案件不受影響。`,
    );
  }

  private open(editing: Editing, draft: Draft): void {
    this.editing.set(editing);
    this.draft.set(draft);
    this.fieldErrors.set({});
    this.feedback.set('');
    this.error.set('');
  }

  private clearError(field: CaseTypeField): void {
    if (this.fieldErrors()[field] === undefined) return;
    this.fieldErrors.update((errors) => ({ ...errors, [field]: undefined }));
  }

  private submit(request: ReturnType<CaseSettingsRepository['createCaseType']>, done: (type: CaseTypeView) => string): void {
    this.saving.set(true);
    this.feedback.set('');
    this.error.set('');
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => this.saved(result, done),
      error: () => {
        this.saving.set(false);
        this.error.set('目前無法儲存案件類型，請稍後再試。這次沒有變更。');
      },
    });
  }

  private saved(result: CaseTypeResult, done: (type: CaseTypeView) => string): void {
    this.saving.set(false);
    if (result.status === 'ready') {
      this.editing.set(null);
      this.fieldErrors.set({});
      this.typesResource.reload();
      this.settingsChanges?.announce(this);
      this.feedback.set(done(result.data));
      return;
    }
    if (result.status === 'validation-failed') {
      // 表單開著時顯示在欄位下方；從清單直接停用／啟用時（例如承辦組已封存）顯示在區塊下方。
      if (this.editing() !== null && Object.keys(result.fieldErrors).length > 0) this.fieldErrors.set(result.fieldErrors);
      else this.error.set(result.message);
    } else if (result.status === 'permission-denied') {
      this.error.set(result.message);
    } else {
      this.error.set('目前無法儲存案件類型，請稍後再試。');
    }
  }
}
