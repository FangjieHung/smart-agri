import { HttpErrorResponse, type HttpClient } from '@angular/common/http';
import { catchError, map, of, throwError, type Observable } from 'rxjs';
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

/**
 * `/me` 中 mock 需要的部分；API 模式由 `HttpSessionBackend.restore()` 提供。
 * `accountId` 是後端的真實帳號 GUID，用來判斷團隊列表裡「哪一列是我自己」；
 * `demoAccountId` 只給仍在 mock 的功能區（`viewerOverride`）沿用現有的權限判斷。
 */
export interface ApiViewerPermissions {
  readonly accountId: string;
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
 * 只覆寫**目前登入者自己**的權限（來自 `/me`，鍵是同角色的 Demo 身分 id）。團隊 API
 * 回傳的其他成員不再經由角色換算成 Demo 身分——同組織有兩位同角色成員時，這個對應
 * 本來就無法一一還原，正是本票要修的問題（issue #36：改權限改到別人）。沒有被覆寫
 * 的 Demo 帳號沿用 seed 的權限；mock 的檢查幾乎都只看目前身分，所以影響限於
 * 「別人的權限」出現在畫面上的地方（例如資料管理者候選人的說明）。
 */
function viewerOverride(viewer: ApiViewerPermissions | null): AccountPermissionOverrides {
  return viewer === null ? {} : { [viewer.demoAccountId]: viewer.permissions };
}

/**
 * API 模式的 repository：已接上 API 的方法走 HTTP，其餘方法沿用 `MockDemoRepository`
 * （以繼承取得，所以 mock 的所有方法不用逐一轉接）。只由 `provideApiMode()` 建立，
 * mock 建置不含這個檔案。
 *
 * 目前接上 API 的：`getTeam`、`updateMemberPermissions`（M1；M2 起改用真實帳號 GUID
 * 當列表的 key、編輯目標與更新目標，不再換回同角色的 Demo 身分——見 issue #36）。
 */
export class HybridDemoRepository extends MockDemoRepository {
  private readonly http: HttpClient;
  private readonly viewerPermissions: () => ApiViewerPermissions | null;

  constructor(seed: DemoSeed, options: MockDemoRepositoryOptions, deps: HybridDemoRepositoryDeps) {
    super(seed, { ...options, accountsSource: () => viewerOverride(deps.viewerPermissions()) });
    this.http = deps.http;
    this.viewerPermissions = deps.viewerPermissions;
  }

  override getTeam(): Observable<RepositoryView<TeamView>> {
    return this.http.get<TeamResponse>(API_TEAM_PATH).pipe(
      map((response) => this.teamLoaded(response)),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error)),
    );
  }

  /** `memberAccountId` 是團隊列表給的真實帳號 GUID；直接 PUT 到該 id，不再需要先解析。 */
  override updateMemberPermissions(
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): Observable<UpdateMemberPermissionsResult> {
    const body: UpdateMemberPermissionsRequest = { permissions: [...permissions] };
    return this.http.put<TeamResponse>(apiMemberPermissionsPath(memberAccountId), body).pipe(
      map((response): UpdateMemberPermissionsResult => this.teamLoaded(response)),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(validationFailed(error)) : this.permissionDeniedOrThrow(error),
      ),
    );
  }

  private teamLoaded(response: TeamResponse): RepositoryView<TeamView> {
    return {
      status: 'ready',
      data: toTeamView(response, this.viewerPermissions()?.accountId ?? null),
    };
  }

  /**
   * 403 轉成既有的 permission-denied；其他錯誤（5xx、連線中斷）交給畫面的錯誤狀態。
   * 團隊端點的 403 只有兩種原因：呼叫者沒有權限，或成員不存在／不屬於這個組織——
   * 後端刻意讓兩者回傳一模一樣的內容（`ApiErrors.NotFound` 就是 `Forbidden`），
   * 這裡因此也共用同一個結果，不必另外分辨「找不到成員」。
   */
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

/** `viewerAccountId` 是目前登入者的真實帳號 GUID（來自 `/me`），不是 Demo 身分 id。 */
export function toTeamView(response: TeamResponse, viewerAccountId: string | null): TeamView {
  const members = response.members
    .map((member): TeamMemberView => ({
      id: member.id,
      displayName: member.displayName,
      role: member.role,
      roleLabel: ACCOUNT_ROLE_LABELS[member.role],
      roleDescription: ACCOUNT_ROLE_DESCRIPTIONS[member.role],
      permissions: normalizeMemberPermissions(member.permissions),
      isViewer: member.id === viewerAccountId,
      lockedPermissions: normalizeMemberPermissions(member.lockedPermissions),
    }))
    // API 已依 id 排序；這裡改用 mock 慣用的角色順序呈現。`sort` 是穩定排序，
    // 同角色的多位成員（例如兩位 smb-internal）維持 API 回傳的順序。
    .sort((a, b) => ROLE_ORDER.indexOf(a.role) - ROLE_ORDER.indexOf(b.role));

  return { members, permissions: ACCOUNT_PERMISSIONS, savedAt: response.savedAt };
}
