import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import type { AccountId, AccountRole } from '../domain/account.model';
import type {
  CaseGroupAccountView,
  CaseGroupListView,
  CaseGroupMemberChangeView,
  CaseGroupView,
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

/** 與後端 `ForbiddenReason.OrganizationSettings` 相同：不是管理者、不存在、別的組織，一律這句。 */
export const CASE_SETTINGS_ADMIN_DENIED_MESSAGE = '只有管理者可以變更組織設定。';
/** 與後端 `ForbiddenReason.CaseFeature` 相同：外部客戶讀取承辦組。 */
export const CASE_FEATURE_DENIED_MESSAGE = '案件功能只開放組織內部帳號使用。';
export const CASE_GROUP_NAME_REQUIRED_MESSAGE = '請輸入承辦組名稱。';
export const CASE_GROUP_NAME_MAX_LENGTH = 40;
export const CASE_GROUP_NAME_TOO_LONG_MESSAGE = `承辦組名稱請在 ${CASE_GROUP_NAME_MAX_LENGTH} 個字以內。`;
export const CASE_GROUP_NAME_TAKEN_MESSAGE = '已經有同名的承辦組，請換一個名稱。';
export const CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE = '成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。';
export const CASE_GROUP_MEMBERS_CONFLICT_MESSAGE = '承辦組成員剛被其他人更新，請重新整理後再試一次。';

/** 建立、改名：名稱空白、太長或重複是 validation-failed（什麼都沒有寫入）。 */
export type CaseGroupNameResult = RepositoryView<CaseGroupView> | OrganizationSettingsValidationFailedView;

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

const REMOVED_ACCOUNT_NAME = '已停用的帳號';

/**
 * 承辦組（以及 M7-2 的案件類型）設定的單一資料入口（issue #246）。API 模式讀寫
 * `/api/v1/case-groups`；純 Demo 模式只保存在這次工作階段，檢查與後端相同：管理者以角色
 * （`smb-admin`）判斷，成員只能是內部帳號，名稱 1–40 字且不重複。
 */
@Injectable({ providedIn: 'root' })
export class CaseSettingsRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private mockGroups: MockGroup[] = MOCK_GROUPS.map((group) => ({ ...group }));
  private mockChanges: MockChange[] = [...MOCK_CHANGES];

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

  /** 封存（`archive: true`）或取消封存；已經是那個狀態時什麼都不寫。 */
  setCaseGroupArchived(groupId: string, archive: boolean): Observable<RepositoryView<CaseGroupView>> {
    if (this.apiMode) {
      return this.client().post<CaseGroupView>(`${apiCaseGroupPath(groupId)}:${archive ? 'archive' : 'unarchive'}`, {}).pipe(
        map((data): RepositoryView<CaseGroupView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<CaseGroupView>(error, ADMIN_DENIED)),
      );
    }
    return defer(() => {
      const current = this.mockFind(groupId);
      if (!this.mockIsManager() || !current) return of(ADMIN_DENIED);
      const now = new Date().toISOString();
      const next = (current.archivedAt !== null) === archive
        ? current
        : { ...current, archivedAt: archive ? now : null, updatedAt: now };
      this.mockReplace(next);
      return of<RepositoryView<CaseGroupView>>({ status: 'ready', data: this.mockView(next) });
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
