import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import type { AccountId, AccountRole } from '../domain/account.model';
import { CASE_DUE_MAX_HOURS, CASE_DUE_MIN_HOURS, CASE_DUE_RANGE_MESSAGE } from '../domain/case-due-time';
import type {
  CaseGroupAccountView,
  CaseGroupListView,
  CaseGroupMemberChangeView,
  CaseGroupView,
  CaseTypeInput,
  CaseTypeListView,
  CaseTypeView,
} from '../domain/case-settings.model';
import { DemoSessionService } from '../session/demo-session.service';
import { DEMO_SEED } from './demo-seed';
import type {
  OrganizationSettingsConflictView,
  OrganizationSettingsValidationFailedView,
  PermissionDeniedRepositoryView,
  RepositoryView,
} from './demo-repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_CASE_GROUPS_PATH = '/api/v1/case-groups';

export function apiCaseGroupPath(groupId: string): string {
  return `${API_CASE_GROUPS_PATH}/${encodeURIComponent(groupId)}`;
}

export const API_CASE_TYPES_PATH = '/api/v1/case-types';

export function apiCaseTypePath(typeId: string): string {
  return `${API_CASE_TYPES_PATH}/${encodeURIComponent(typeId)}`;
}

/** 與後端 `ForbiddenReason.OrganizationSettings` 相同：不是管理者、不存在、別的組織，一律這句。 */
export const CASE_SETTINGS_ADMIN_DENIED_MESSAGE = '只有管理者可以變更組織設定。';
/** 與後端 `ForbiddenReason.CaseFeature` 相同：外部客戶，以及看不到、不存在或別的組織的案件（issue #248），一律這句。 */
export const CASE_FEATURE_DENIED_MESSAGE = '你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。';
export const CASE_GROUP_NAME_REQUIRED_MESSAGE = '請輸入承辦組名稱。';
export const CASE_GROUP_NAME_MAX_LENGTH = 40;
export const CASE_GROUP_NAME_TOO_LONG_MESSAGE = `承辦組名稱請在 ${CASE_GROUP_NAME_MAX_LENGTH} 個字以內。`;
export const CASE_GROUP_NAME_TAKEN_MESSAGE = '已經有同名的承辦組，請換一個名稱。';
export const CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE = '成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。';
export const CASE_GROUP_MEMBERS_CONFLICT_MESSAGE = '承辦組成員剛被其他人更新，請重新整理後再試一次。';

/** 與後端 `CaseTypeRules`／`CaseTypeEndpoints` 相同的上限與訊息（issue #247）。 */
export const CASE_TYPE_NAME_MAX_LENGTH = 40;
export const CASE_TYPE_DESCRIPTION_MAX_LENGTH = 500;
export const CASE_TYPE_NAME_REQUIRED_MESSAGE = '請輸入案件類型名稱。';
export const CASE_TYPE_NAME_TOO_LONG_MESSAGE = `案件類型名稱請在 ${CASE_TYPE_NAME_MAX_LENGTH} 個字以內。`;
export const CASE_TYPE_DESCRIPTION_TOO_LONG_MESSAGE = `說明請在 ${CASE_TYPE_DESCRIPTION_MAX_LENGTH} 個字以內。`;
export const CASE_TYPE_GROUP_REQUIRED_MESSAGE = '請選擇預設承辦組。';
export const CASE_TYPE_NAME_TAKEN_MESSAGE = '已經有同名的案件類型，請換一個名稱。';
export const CASE_TYPE_GROUP_ARCHIVED_MESSAGE = '這個承辦組已封存，請選擇其他承辦組。';
export const CASE_TYPE_GROUP_NOT_FOUND_MESSAGE = '找不到這個承辦組，請重新選擇。';

/** 與後端 `CaseGroupEndpoints.InUse` 相同：承辦組是啟用中類型的預設承辦組時不能封存。 */
export function caseGroupInUseMessage(groupName: string, typeNames: readonly string[]): string {
  return `「${groupName}」是啟用中的案件類型${typeNames.map((name) => `「${name}」`).join('、')}的預設承辦組。`
    + '請先替這些類型換一個承辦組，或停用它們，再封存。';
}

/** 案件類型表單的欄位（與 API 的欄位名稱相同）。 */
export type CaseTypeField = 'name' | 'description' | 'defaultGroupId' | 'defaultDueHours';
const CASE_TYPE_FIELDS: readonly CaseTypeField[] = ['name', 'description', 'defaultGroupId', 'defaultDueHours'];

/** `422`：每個欄位的第一則錯誤；`message` 是後端的摘要（第一個錯誤）。什麼都沒有寫入。 */
export interface CaseTypeValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
  readonly fieldErrors: Readonly<Partial<Record<CaseTypeField, string>>>;
}

/** 建立、修改案件類型的結果。 */
export type CaseTypeResult = RepositoryView<CaseTypeView> | CaseTypeValidationFailedView;

/** 建立、改名：名稱空白、太長或重複是 validation-failed（什麼都沒有寫入）。 */
export type CaseGroupNameResult = RepositoryView<CaseGroupView> | OrganizationSettingsValidationFailedView;

/** 封存承辦組：是啟用中案件類型的預設承辦組時是 validation-failed（`422 case-group-in-use`）。 */
export type CaseGroupArchiveResult = RepositoryView<CaseGroupView> | OrganizationSettingsValidationFailedView;

/** 整份取代成員：含外部客戶或不認得的帳號是 validation-failed；同時有人修改是 conflict。 */
export type CaseGroupMembersResult =
  | RepositoryView<CaseGroupView>
  | OrganizationSettingsValidationFailedView
  | OrganizationSettingsConflictView;

const ADMIN_DENIED: PermissionDeniedRepositoryView = {
  status: 'permission-denied',
  reason: 'organization-settings',
  message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
};

const CASE_DENIED: PermissionDeniedRepositoryView = {
  status: 'permission-denied',
  reason: 'case',
  message: CASE_FEATURE_DENIED_MESSAGE,
};

interface MockGroup {
  readonly id: string;
  readonly name: string;
  readonly archivedAt: string | null;
  readonly members: readonly AccountId[];
  readonly createdAt: string;
  readonly updatedAt: string;
}

interface MockChange {
  readonly id: string;
  readonly groupId: string;
  readonly accountId: AccountId;
  readonly added: boolean;
  readonly changedBy: AccountId;
  readonly changedAt: string;
}

/** mock 的範例承辦組（只放在這個檔案，不加進 `demo-seed.ts`：後端有測試會讀它）。 */
const MOCK_SEED_AT = '2026-10-01T01:00:00.000Z';
const MOCK_GROUPS: readonly MockGroup[] = [
  {
    id: 'case-group-equipment', name: '設備組', archivedAt: null,
    members: ['account-internal-employee'], createdAt: MOCK_SEED_AT, updatedAt: MOCK_SEED_AT,
  },
  {
    id: 'case-group-purchasing', name: '採購組', archivedAt: null,
    members: ['account-smb-admin'], createdAt: '2026-10-01T01:05:00.000Z', updatedAt: '2026-10-01T01:05:00.000Z',
  },
  {
    id: 'case-group-old-warehouse', name: '舊倉儲組', archivedAt: '2026-10-02T03:00:00.000Z',
    members: [], createdAt: '2026-09-20T02:00:00.000Z', updatedAt: '2026-10-02T03:00:00.000Z',
  },
];
const MOCK_CHANGES: readonly MockChange[] = [
  {
    id: 'case-group-change-1', groupId: 'case-group-equipment', accountId: 'account-internal-employee',
    added: true, changedBy: 'account-smb-admin', changedAt: MOCK_SEED_AT,
  },
  {
    id: 'case-group-change-2', groupId: 'case-group-purchasing', accountId: 'account-smb-admin',
    added: true, changedBy: 'account-smb-admin', changedAt: '2026-10-01T01:05:00.000Z',
  },
];

interface MockType {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly defaultGroupId: string;
  readonly defaultDueHours: number;
  readonly isActive: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** mock 的範例案件類型：一個啟用中（設備組）、一個停用（預設承辦組已封存）。 */
const MOCK_TYPES: readonly MockType[] = [
  {
    id: 'case-type-equipment-repair', name: '設備故障報修',
    description: '冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。',
    defaultGroupId: 'case-group-equipment', defaultDueHours: 72, isActive: true,
    createdAt: '2026-10-01T01:10:00.000Z', updatedAt: '2026-10-01T01:10:00.000Z',
  },
  {
    id: 'case-type-stocktake', name: '倉儲盤點差異',
    description: '盤點數量與系統不符，需要追查。',
    defaultGroupId: 'case-group-old-warehouse', defaultDueHours: 36, isActive: false,
    createdAt: '2026-09-20T02:10:00.000Z', updatedAt: '2026-10-02T02:50:00.000Z',
  },
];

const REMOVED_ACCOUNT_NAME = '已停用的帳號';

/**
 * 承辦組與案件類型設定的單一資料入口（issue #246、#247）。API 模式讀寫 `/api/v1/case-groups`、
 * `/api/v1/case-types`；純 Demo 模式只保存在這次工作階段，檢查與後端相同：管理者以角色
 * （`smb-admin`）判斷，成員只能是內部帳號，名稱 1–40 字且不重複；類型的說明 0–500 字、
 * 處理時限 1–2,160 小時，預設承辦組不能是封存中的組。
 */
@Injectable({ providedIn: 'root' })
export class CaseSettingsRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private mockGroups: MockGroup[] = MOCK_GROUPS.map((group) => ({ ...group }));
  private mockChanges: MockChange[] = [...MOCK_CHANGES];
  private mockTypes: MockType[] = MOCK_TYPES.map((type) => ({ ...type }));

  /** 內部帳號的清單；`includeArchived` 只有管理者有效（設定頁），其他人永遠只看到可選的組。 */
  listCaseGroups(options: { readonly includeArchived?: boolean } = {}): Observable<RepositoryView<CaseGroupListView>> {
    if (this.apiMode) {
      const params = options.includeArchived ? new HttpParams().set('includeArchived', 'true') : undefined;
      return this.client().get<CaseGroupListView>(API_CASE_GROUPS_PATH, { params }).pipe(
        map((data): RepositoryView<CaseGroupListView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<CaseGroupListView>(error, CASE_DENIED)),
      );
    }
    return defer(() => {
      const role = this.viewerRole();
      if (role === null || role === 'external-customer') return of(CASE_DENIED);
      const isManager = role === 'smb-admin';
      const groups = this.mockGroups
        .filter((group) => (isManager && options.includeArchived) || group.archivedAt === null)
        .map((group) => this.mockView(group));
      const candidates = isManager
        ? DEMO_SEED.accounts
          .filter((account) => account.role !== 'external-customer')
          .map((account) => ({ id: account.id, displayName: account.displayName, role: account.role }))
        : [];
      return of<RepositoryView<CaseGroupListView>>({ status: 'ready', data: { groups, canManage: isManager, candidates } });
    });
  }

  createCaseGroup(name: string): Observable<CaseGroupNameResult> {
    if (this.apiMode) {
      return this.client().post<CaseGroupView>(API_CASE_GROUPS_PATH, { name }).pipe(
        map((data): CaseGroupNameResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.writeError<CaseGroupNameResult>(error)),
      );
    }
    return defer(() => {
      if (!this.mockIsManager()) return of(ADMIN_DENIED);
      const checked = this.mockCheckName(name, null);
      if (typeof checked !== 'string') return of(checked);
      const now = new Date().toISOString();
      const group: MockGroup = { id: crypto.randomUUID(), name: checked, archivedAt: null, members: [], createdAt: now, updatedAt: now };
      this.mockGroups = [...this.mockGroups, group];
      return of<CaseGroupNameResult>({ status: 'ready', data: this.mockView(group) });
    });
  }

  renameCaseGroup(groupId: string, name: string): Observable<CaseGroupNameResult> {
    if (this.apiMode) {
      return this.client().put<CaseGroupView>(apiCaseGroupPath(groupId), { name }).pipe(
        map((data): CaseGroupNameResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.writeError<CaseGroupNameResult>(error)),
      );
    }
    return defer(() => {
      const current = this.mockFind(groupId);
      if (!this.mockIsManager() || !current) return of(ADMIN_DENIED);
      const checked = this.mockCheckName(name, groupId);
      if (typeof checked !== 'string') return of(checked);
      const next = checked === current.name ? current : { ...current, name: checked, updatedAt: new Date().toISOString() };
      this.mockReplace(next);
      return of<CaseGroupNameResult>({ status: 'ready', data: this.mockView(next) });
    });
  }

  /**
   * 封存（`archive: true`）或取消封存；已經是那個狀態時什麼都不寫。是啟用中案件類型的預設承辦組時
   * 不能封存（validation-failed，訊息列出那些類型）。
   */
  setCaseGroupArchived(groupId: string, archive: boolean): Observable<CaseGroupArchiveResult> {
    if (this.apiMode) {
      return this.client().post<CaseGroupView>(`${apiCaseGroupPath(groupId)}:${archive ? 'archive' : 'unarchive'}`, {}).pipe(
        map((data): CaseGroupArchiveResult => ({ status: 'ready', data })),
        catchError((error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 422) {
            return of<CaseGroupArchiveResult>({ status: 'validation-failed', message: messageOf(error) ?? '目前無法封存這個承辦組。' });
          }
          return this.denied<CaseGroupView>(error, ADMIN_DENIED);
        }),
      );
    }
    return defer(() => {
      const current = this.mockFind(groupId);
      if (!this.mockIsManager() || !current) return of(ADMIN_DENIED);
      const usedBy = this.mockTypes.filter((type) => type.isActive && type.defaultGroupId === groupId).map((type) => type.name);
      if (archive && current.archivedAt === null && usedBy.length > 0) {
        return of<CaseGroupArchiveResult>({ status: 'validation-failed', message: caseGroupInUseMessage(current.name, usedBy) });
      }
      const now = new Date().toISOString();
      const next = (current.archivedAt !== null) === archive
        ? current
        : { ...current, archivedAt: archive ? now : null, updatedAt: now };
      this.mockReplace(next);
      return of<CaseGroupArchiveResult>({ status: 'ready', data: this.mockView(next) });
    });
  }

  /** 整份取代成員名單；每個增減在歷史各記一筆。 */
  updateCaseGroupMembers(groupId: string, accountIds: readonly string[]): Observable<CaseGroupMembersResult> {
    if (this.apiMode) {
      return this.client().put<CaseGroupView>(`${apiCaseGroupPath(groupId)}/members`, { accountIds }).pipe(
        map((data): CaseGroupMembersResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.writeError<CaseGroupMembersResult>(error)),
      );
    }
    return defer(() => {
      const current = this.mockFind(groupId);
      const viewer = this.session.activeAccountId();
      if (!this.mockIsManager() || !current || viewer === null) return of(ADMIN_DENIED);
      const requested = [...new Set(accountIds)];
      const eligible = requested.every((id) => {
        const role = DEMO_SEED.accounts.find((account) => account.id === id)?.role;
        return role === 'smb-admin' || role === 'internal-employee';
      });
      if (!eligible) {
        return of<CaseGroupMembersResult>({ status: 'validation-failed', message: CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE });
      }
      const now = new Date().toISOString();
      const removed = current.members.filter((id) => !requested.includes(id));
      const added = requested.filter((id) => !current.members.includes(id));
      if (removed.length + added.length === 0) {
        return of<CaseGroupMembersResult>({ status: 'ready', data: this.mockView(current) });
      }
      const change = (accountId: AccountId, wasAdded: boolean): MockChange => ({
        id: crypto.randomUUID(), groupId, accountId, added: wasAdded, changedBy: viewer, changedAt: now,
      });
      this.mockChanges = [
        ...this.mockChanges,
        ...removed.map((id) => change(id, false)),
        ...added.map((id) => change(id, true)),
      ];
      const next = { ...current, members: [...current.members.filter((id) => requested.includes(id)), ...added] };
      this.mockReplace(next);
      return of<CaseGroupMembersResult>({ status: 'ready', data: this.mockView(next) });
    });
  }

  /** 成員異動歷史，最新的在前（管理者）。 */
  listCaseGroupMemberChanges(groupId: string): Observable<RepositoryView<readonly CaseGroupMemberChangeView[]>> {
    if (this.apiMode) {
      return this.client().get<CaseGroupMemberChangeView[]>(`${apiCaseGroupPath(groupId)}/member-changes`).pipe(
        map((data): RepositoryView<readonly CaseGroupMemberChangeView[]> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<readonly CaseGroupMemberChangeView[]>(error, ADMIN_DENIED)),
      );
    }
    return defer(() => {
      if (!this.mockIsManager() || !this.mockFind(groupId)) return of(ADMIN_DENIED);
      const data = this.mockChanges
        .filter((change) => change.groupId === groupId)
        .map((change, index) => ({ change, index }))
        .sort((a, b) => b.change.changedAt.localeCompare(a.change.changedAt) || b.index - a.index)
        .map(({ change }): CaseGroupMemberChangeView => ({
          id: change.id,
          account: this.mockAccount(change.accountId),
          added: change.added,
          changedBy: this.mockAccount(change.changedBy),
          changedAt: change.changedAt,
        }));
      return of<RepositoryView<readonly CaseGroupMemberChangeView[]>>({ status: 'ready', data });
    });
  }

  /**
   * 案件類型（issue #247）：內部帳號都讀得到啟用中的類型；`includeInactive` 只有管理者有效（設定頁），
   * 其他人永遠只看到可以用來建立案件的類型。M7-3 建立案件時用預設清單帶入承辦組與時限。
   */
  listCaseTypes(options: { readonly includeInactive?: boolean } = {}): Observable<RepositoryView<CaseTypeListView>> {
    if (this.apiMode) {
      const params = options.includeInactive ? new HttpParams().set('includeInactive', 'true') : undefined;
      return this.client().get<CaseTypeListView>(API_CASE_TYPES_PATH, { params }).pipe(
        map((data): RepositoryView<CaseTypeListView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<CaseTypeListView>(error, CASE_DENIED)),
      );
    }
    return defer(() => {
      const role = this.viewerRole();
      if (role === null || role === 'external-customer') return of(CASE_DENIED);
      const isManager = role === 'smb-admin';
      const types = this.mockTypes
        .filter((type) => (isManager && options.includeInactive) || type.isActive)
        .map((type) => this.mockTypeView(type));
      return of<RepositoryView<CaseTypeListView>>({ status: 'ready', data: { types, canManage: isManager } });
    });
  }

  /** 建立案件類型（管理者）：欄位錯誤、名稱重複、承辦組封存中都是 validation-failed。 */
  createCaseType(input: CaseTypeInput): Observable<CaseTypeResult> {
    if (this.apiMode) {
      return this.client().post<CaseTypeView>(API_CASE_TYPES_PATH, input).pipe(
        map((data): CaseTypeResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.caseTypeError(error)),
      );
    }
    return defer(() => {
      if (!this.mockIsManager()) return of(ADMIN_DENIED);
      const checked = this.mockCheckType(input, null);
      if ('status' in checked) return of(checked);
      const now = new Date().toISOString();
      const type: MockType = { id: crypto.randomUUID(), ...checked, createdAt: now, updatedAt: now };
      this.mockTypes = [...this.mockTypes, type];
      return of<CaseTypeResult>({ status: 'ready', data: this.mockTypeView(type) });
    });
  }

  /** 修改案件類型（管理者），每次送出完整內容；停用、重新啟用也是這個。沒有變更時什麼都不寫。 */
  updateCaseType(typeId: string, input: CaseTypeInput): Observable<CaseTypeResult> {
    if (this.apiMode) {
      return this.client().put<CaseTypeView>(apiCaseTypePath(typeId), input).pipe(
        map((data): CaseTypeResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.caseTypeError(error)),
      );
    }
    return defer(() => {
      const current = this.mockTypes.find((type) => type.id === typeId);
      if (!this.mockIsManager() || !current) return of(ADMIN_DENIED);
      const checked = this.mockCheckType(input, current);
      if ('status' in checked) return of(checked);
      const unchanged = (Object.keys(checked) as (keyof typeof checked)[]).every((key) => checked[key] === current[key]);
      const next: MockType = unchanged ? current : { ...current, ...checked, updatedAt: new Date().toISOString() };
      this.mockTypes = this.mockTypes.map((type) => (type.id === typeId ? next : type));
      return of<CaseTypeResult>({ status: 'ready', data: this.mockTypeView(next) });
    });
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  /** `403`：後端帶的原因（`case`、`organization-settings`）照用；其他狀態照常拋出。 */
  private denied<T>(error: unknown, fallback: PermissionDeniedRepositoryView): Observable<RepositoryView<T>> {
    if (!(error instanceof HttpErrorResponse) || error.status !== 403) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const reason = body['reason'];
    const message = typeof body['message'] === 'string' ? body['message'] : fallback.message;
    if (reason === 'case' || reason === 'organization-settings' || reason === 'password-change-required') {
      return of({ status: 'permission-denied', reason, message });
    }
    return of(fallback);
  }

  /** `422` → validation-failed、`409` → conflict、`403` → permission-denied。 */
  private writeError<T extends CaseGroupNameResult | CaseGroupMembersResult>(error: unknown): Observable<T> {
    if (error instanceof HttpErrorResponse && (error.status === 409 || error.status === 422)) {
      const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
      const message = typeof body['message'] === 'string' ? body['message'] : null;
      return of((error.status === 409
        ? { status: 'conflict', message: message ?? CASE_GROUP_MEMBERS_CONFLICT_MESSAGE }
        : { status: 'validation-failed', message: message ?? CASE_GROUP_NAME_REQUIRED_MESSAGE }) as T);
    }
    return this.denied<CaseGroupView>(error, ADMIN_DENIED) as Observable<T>;
  }

  /** `422` → validation-failed（每個欄位一則）；其他照 `denied` 處理。 */
  private caseTypeError(error: unknown): Observable<CaseTypeResult> {
    if (error instanceof HttpErrorResponse && error.status === 422) {
      const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
      const errors = body['errors'] && typeof body['errors'] === 'object' ? body['errors'] as Record<string, unknown> : {};
      const fieldErrors: Partial<Record<CaseTypeField, string>> = {};
      for (const field of CASE_TYPE_FIELDS) {
        const messages = errors[field];
        if (Array.isArray(messages) && typeof messages[0] === 'string') fieldErrors[field] = messages[0];
      }
      return of<CaseTypeResult>({
        status: 'validation-failed',
        message: messageOf(error) ?? Object.values(fieldErrors)[0] ?? CASE_TYPE_NAME_REQUIRED_MESSAGE,
        fieldErrors,
      });
    }
    return this.denied<CaseTypeView>(error, ADMIN_DENIED);
  }

  /**
   * 與後端相同的檢查：先檢查每個欄位（一次列出全部），再檢查名稱重複與承辦組。承辦組封存中只允許
   * 「停用的類型沿用原本的承辦組」。成功時回傳修剪後的欄位。
   */
  private mockCheckType(
    input: CaseTypeInput,
    current: MockType | null,
  ): Omit<MockType, 'id' | 'createdAt' | 'updatedAt'> | CaseTypeValidationFailedView {
    const name = input.name.trim();
    const description = input.description.trim();
    const fieldErrors: Partial<Record<CaseTypeField, string>> = {};
    if (name.length === 0) fieldErrors.name = CASE_TYPE_NAME_REQUIRED_MESSAGE;
    else if (name.length > CASE_TYPE_NAME_MAX_LENGTH) fieldErrors.name = CASE_TYPE_NAME_TOO_LONG_MESSAGE;
    if (description.length > CASE_TYPE_DESCRIPTION_MAX_LENGTH) fieldErrors.description = CASE_TYPE_DESCRIPTION_TOO_LONG_MESSAGE;
    if (!input.defaultGroupId) fieldErrors.defaultGroupId = CASE_TYPE_GROUP_REQUIRED_MESSAGE;
    if (!Number.isInteger(input.defaultDueHours) || input.defaultDueHours < CASE_DUE_MIN_HOURS || input.defaultDueHours > CASE_DUE_MAX_HOURS) {
      fieldErrors.defaultDueHours = CASE_DUE_RANGE_MESSAGE;
    }
    const failed = (errors: Partial<Record<CaseTypeField, string>>): CaseTypeValidationFailedView =>
      ({ status: 'validation-failed', message: Object.values(errors)[0] ?? '', fieldErrors: errors });
    if (Object.keys(fieldErrors).length > 0) return failed(fieldErrors);
    if (this.mockTypes.some((type) => type.name === name && type.id !== current?.id)) {
      return failed({ name: CASE_TYPE_NAME_TAKEN_MESSAGE });
    }
    const group = this.mockFind(input.defaultGroupId);
    if (!group) return failed({ defaultGroupId: CASE_TYPE_GROUP_NOT_FOUND_MESSAGE });
    if (group.archivedAt !== null && (input.isActive || group.id !== current?.defaultGroupId)) {
      return failed({ defaultGroupId: CASE_TYPE_GROUP_ARCHIVED_MESSAGE });
    }
    return { name, description, defaultGroupId: group.id, defaultDueHours: input.defaultDueHours, isActive: input.isActive };
  }

  private mockTypeView(type: MockType): CaseTypeView {
    const group = this.mockFind(type.defaultGroupId);
    return {
      id: type.id,
      name: type.name,
      description: type.description,
      defaultGroup: { id: type.defaultGroupId, name: group?.name ?? '', archived: group?.archivedAt != null },
      defaultDueHours: type.defaultDueHours,
      isActive: type.isActive,
      createdAt: type.createdAt,
      updatedAt: type.updatedAt,
    };
  }

  private viewerRole(): AccountRole | null {
    const viewer = this.session.activeAccountId();
    return DEMO_SEED.accounts.find((account) => account.id === viewer)?.role ?? null;
  }

  private mockIsManager(): boolean {
    return this.viewerRole() === 'smb-admin';
  }

  private mockFind(groupId: string): MockGroup | undefined {
    return this.mockGroups.find((group) => group.id === groupId);
  }

  private mockReplace(group: MockGroup): void {
    this.mockGroups = this.mockGroups.map((candidate) => (candidate.id === group.id ? group : candidate));
  }

  /** 與後端相同：先修剪，空白、超過 40 字、與其他組同名各自失敗；成功時回傳修剪後的名稱。 */
  private mockCheckName(name: string, exceptId: string | null): string | OrganizationSettingsValidationFailedView {
    const trimmed = name.trim();
    const failed = (message: string): OrganizationSettingsValidationFailedView => ({ status: 'validation-failed', message });
    if (trimmed.length === 0) return failed(CASE_GROUP_NAME_REQUIRED_MESSAGE);
    if (trimmed.length > CASE_GROUP_NAME_MAX_LENGTH) return failed(CASE_GROUP_NAME_TOO_LONG_MESSAGE);
    if (this.mockGroups.some((group) => group.name === trimmed && group.id !== exceptId)) {
      return failed(CASE_GROUP_NAME_TAKEN_MESSAGE);
    }
    return trimmed;
  }

  private mockAccount(accountId: AccountId): CaseGroupAccountView {
    return {
      id: accountId,
      displayName: DEMO_SEED.accounts.find((account) => account.id === accountId)?.displayName ?? REMOVED_ACCOUNT_NAME,
    };
  }

  private mockView(group: MockGroup): CaseGroupView {
    return {
      id: group.id,
      name: group.name,
      archived: group.archivedAt !== null,
      archivedAt: group.archivedAt,
      members: group.members.map((id) => this.mockAccount(id)),
      createdAt: group.createdAt,
      updatedAt: group.updatedAt,
    };
  }
}

function messageOf(error: HttpErrorResponse): string | null {
  const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
  return typeof body['message'] === 'string' ? body['message'] : null;
}
