import { HttpErrorResponse, type HttpClient } from '@angular/common/http';
import { catchError, defer, map, of, switchMap, throwError, type Observable } from 'rxjs';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission, AccountRole } from '../domain/account.model';
import {
  ACCOUNT_PERMISSIONS,
  ACCOUNT_ROLE_DESCRIPTIONS,
  ACCOUNT_ROLE_LABELS,
  normalizeMemberPermissions,
  type TeamMemberView,
  type TeamView,
} from '../domain/team.model';
import { DEMO_ACCOUNT_BY_ROLE } from '../session/api-mode/demo-identity-bridge';
import type {
  PermissionDeniedRepositoryView,
  RepositoryView,
  UpdateMemberPermissionsResult,
} from './demo-repository';
import type { DemoSeed } from './demo-seed';
import {
  MockDemoRepository,
  TEAM_PERMISSION_DENIED_MESSAGE,
  type AccountPermissionOverrides,
  type MockDemoRepositoryOptions,
} from './mock-demo-repository';

type TeamResponse = components['schemas']['TeamResponse'];
type UpdateMemberPermissionsRequest = components['schemas']['UpdateMemberPermissionsRequest'];

export const API_TEAM_PATH = '/api/v1/team';

export function apiMemberPermissionsPath(memberId: string): string {
  return `${API_TEAM_PATH}/members/${encodeURIComponent(memberId)}/permissions`;
}

/** 設定新密碼前 API 回的 403 訊息；與 API 的 `ForbiddenReason.PasswordChangeRequired` 相同。 */
const PASSWORD_CHANGE_REQUIRED_MESSAGE = '請先設定新密碼，才能使用其他功能。';

/** OpenAPI 沒有描述錯誤本文（`content?: never`），依後端 `ApiErrors` 的形狀在這裡定義。 */
interface ForbiddenBody {
  readonly reason?: unknown;
  readonly message?: unknown;
}

interface ValidationFailedBody {
  readonly message?: unknown;
  readonly errors?: { readonly permissions?: unknown };
}

/** `/me` 中 mock 需要的部分；API 模式由 `HttpSessionBackend.restore()` 提供。 */
export interface ApiViewerPermissions {
  readonly demoAccountId: AccountId;
  readonly permissions: readonly AccountPermission[];
}

export interface HybridDemoRepositoryDeps {
  readonly http: HttpClient;
  /** 最近一次 `/me`（登入與每次啟動時更新）；沒有登入時為 null。 */
  readonly viewerPermissions: () => ApiViewerPermissions | null;
}

/** mock 呈現團隊的順序（seed 的順序就是角色順序）；API 依 id 排序，不能直接沿用。 */
const ROLE_ORDER = Object.keys(ACCOUNT_ROLE_LABELS) as AccountRole[];

/**
 * 仍在 mock 的功能區要用的權限（`MockDemoRepositoryOptions.accountsSource`）。
 *
 * API 只告訴我們「看得到的帳號」：
 * - 目前身分：永遠有 `/me`。
 * - 其他成員：只有可管理團隊的帳號在這個頁面生命週期內讀過團隊 API 後才知道；
 *   讀過之後以團隊 API 為準（比啟動時的 `/me` 新，也涵蓋改自己權限的情況）。
 * - 其餘帳號沿用 seed 的權限。mock 的檢查幾乎都只看目前身分，所以影響限於
 *   「別人的權限」出現在畫面上的地方（例如資料管理者候選人的說明）。
 * 重新整理頁面後團隊資料就不在了，回到只有 `/me` 的狀態。
 */
export class ApiAccountPermissions {
  private team: AccountPermissionOverrides = {};
  /** Demo 身分 id → API 帳號 id；PUT 的路徑要用 API 的 id。 */
  private readonly apiIds = new Map<AccountId, string>();

  constructor(private readonly viewerPermissions: () => ApiViewerPermissions | null) {}

  overrides(): AccountPermissionOverrides {
    const viewer = this.viewerPermissions();
    return viewer === null
      ? this.team
      : { [viewer.demoAccountId]: viewer.permissions, ...this.team };
  }

  apiIdOf(accountId: AccountId): string | undefined {
    return this.apiIds.get(accountId);
  }

  remember(response: TeamResponse): void {
    const team: Partial<Record<AccountId, readonly AccountPermission[]>> = {};
    for (const member of response.members) {
      const accountId = DEMO_ACCOUNT_BY_ROLE[member.role];
      team[accountId] = normalizeMemberPermissions(member.permissions);
      this.apiIds.set(accountId, member.id);
    }
    this.team = team;
  }
}

/**
 * API 模式的 repository：已接上 API 的方法走 HTTP，其餘方法沿用 `MockDemoRepository`
 * （以繼承取得，所以 mock 的所有方法不用逐一轉接）。只由 `provideApiMode()` 建立，
 * mock 建置不含這個檔案。
 *
 * 目前接上 API 的：`getTeam`、`updateMemberPermissions`（M1）。回應的成員 id 換回
 * 同角色的 Demo 身分 id（`DEMO_ACCOUNT_BY_ROLE`），畫面與 mock 區域的呼叫點都不用改。
 */
export class HybridDemoRepository extends MockDemoRepository {
  private readonly http: HttpClient;
  private readonly known: ApiAccountPermissions;

  constructor(seed: DemoSeed, options: MockDemoRepositoryOptions, deps: HybridDemoRepositoryDeps) {
    const known = new ApiAccountPermissions(deps.viewerPermissions);
    super(seed, { ...options, accountsSource: () => known.overrides() });
    this.http = deps.http;
    this.known = known;
  }

  override getTeam(): Observable<RepositoryView<TeamView>> {
    return this.http.get<TeamResponse>(API_TEAM_PATH).pipe(
      map((response) => this.teamLoaded(response)),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error)),
    );
  }

  override updateMemberPermissions(
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): Observable<UpdateMemberPermissionsResult> {
    return this.resolveApiId(memberAccountId).pipe(
      switchMap((apiId): Observable<UpdateMemberPermissionsResult> => {
        // 團隊裡沒有這個人：與 API 一樣，和「沒有權限」共用同一個結果。
        if (apiId === undefined) return of(teamPermissionDenied());
        const body: UpdateMemberPermissionsRequest = { permissions: [...permissions] };
        return this.http
          .put<TeamResponse>(apiMemberPermissionsPath(apiId), body)
          .pipe(map((response) => this.teamLoaded(response)));
      }),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(validationFailed(error)) : this.permissionDeniedOrThrow(error),
      ),
    );
  }

  /** 平常團隊面板已讀過一次；直接呼叫（或重新整理後）才需要先讀團隊取得 API 的 id。 */
  private resolveApiId(accountId: AccountId): Observable<string | undefined> {
    return defer(() => {
      const known = this.known.apiIdOf(accountId);
      if (known !== undefined) return of(known);
      return this.http.get<TeamResponse>(API_TEAM_PATH).pipe(
        map((response) => {
          this.known.remember(response);
          return this.known.apiIdOf(accountId);
        }),
      );
    });
  }

  private teamLoaded(response: TeamResponse): RepositoryView<TeamView> {
    this.known.remember(response);
    return { status: 'ready', data: toTeamView(response, this.viewer()) };
  }

  /** 403 轉成既有的 permission-denied；其他錯誤（5xx、連線中斷）交給畫面的錯誤狀態。 */
  private permissionDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (!isHttpError(error, 403)) return throwError(() => error);
    const body = (error.error ?? {}) as ForbiddenBody;
    const message = typeof body.message === 'string' ? body.message : null;
    if (body.reason === 'password-change-required') {
      return of({
        status: 'permission-denied',
        reason: 'password-change-required',
        message: message ?? PASSWORD_CHANGE_REQUIRED_MESSAGE,
      });
    }
    // 團隊端點只會回 `team`；其他值（例如後端的 `forbidden` 保底）也歸在這一區。
    return of({ ...teamPermissionDenied(), message: message ?? TEAM_PERMISSION_DENIED_MESSAGE });
  }
}

function isHttpError(error: unknown, status: number): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && error.status === status;
}

function teamPermissionDenied(): PermissionDeniedRepositoryView {
  return { status: 'permission-denied', reason: 'team', message: TEAM_PERMISSION_DENIED_MESSAGE };
}

/** 422：移除自己的 `manage-assistants` 或送出不認得的權限值；後端的訊息與 mock 相同。 */
function validationFailed(error: HttpErrorResponse): UpdateMemberPermissionsResult {
  const body = (error.error ?? {}) as ValidationFailedBody;
  const fieldErrors = body.errors?.permissions;
  const fieldMessage =
    Array.isArray(fieldErrors) && typeof fieldErrors[0] === 'string' ? fieldErrors[0] : null;
  return {
    status: 'validation-failed',
    message:
      typeof body.message === 'string'
        ? body.message
        : (fieldMessage ?? '這次變更沒有儲存，請再試一次。'),
  };
}

export function toTeamView(response: TeamResponse, viewer: AccountId | null): TeamView {
  const members = response.members
    .map((member): TeamMemberView => {
      const id = DEMO_ACCOUNT_BY_ROLE[member.role];
      return {
        id,
        displayName: member.displayName,
        role: member.role,
        roleLabel: ACCOUNT_ROLE_LABELS[member.role],
        roleDescription: ACCOUNT_ROLE_DESCRIPTIONS[member.role],
        permissions: normalizeMemberPermissions(member.permissions),
        isViewer: id === viewer,
        lockedPermissions: normalizeMemberPermissions(member.lockedPermissions),
      };
    })
    // `sort` 是穩定排序：同角色（橋接不支援的情況）維持 API 的順序。
    .sort((a, b) => ROLE_ORDER.indexOf(a.role) - ROLE_ORDER.indexOf(b.role));

  return { members, permissions: ACCOUNT_PERMISSIONS, savedAt: response.savedAt };
}
