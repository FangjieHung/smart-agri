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
  CreateKnowledgeBaseInput,
  KnowledgeBaseDetailView,
  KnowledgeBaseSummaryView,
  KnowledgeDocumentView,
  KnowledgeSharingView,
} from '../domain/knowledge-base.model';
import {
  isRepositoryPermissionDeniedReason,
  type CreateKnowledgeBaseResult,
  type DeleteKnowledgeResult,
  type KnowledgeValidationFailedView,
  type PermissionDeniedRepositoryView,
  type RepositoryPermissionDeniedReason,
  type RepositoryView,
  type RetryKnowledgeDocumentResult,
  type UpdateKnowledgeSharingResult,
  type UpdateMemberPermissionsResult,
} from './demo-repository';
import type { DemoSeed } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import {
  KNOWLEDGE_PERMISSION_DENIED_MESSAGE,
  MockDemoRepository,
  TEAM_PERMISSION_DENIED_MESSAGE,
  type AccountPermissionOverrides,
  type MockDemoRepositoryOptions,
} from './mock-demo-repository';
import { createScopedStorage, type StorageIdentity } from './scoped-storage';

type TeamResponse = components['schemas']['TeamResponse'];
type UpdateMemberPermissionsRequest = components['schemas']['UpdateMemberPermissionsRequest'];
type ApiKnowledgeBaseSummary = components['schemas']['KnowledgeBaseSummaryView'];
type ApiKnowledgeBaseDetail = components['schemas']['KnowledgeBaseDetailView'];
type ApiKnowledgeDocument = components['schemas']['KnowledgeDocumentView'];
type ApiKnowledgeSharing = components['schemas']['KnowledgeSharingView'];
type CreateKnowledgeBaseRequest = components['schemas']['CreateKnowledgeBaseRequest'];
type UpdateKnowledgeSharingRequest = components['schemas']['UpdateKnowledgeSharingRequest'];

export const API_TEAM_PATH = '/api/v1/team';

export function apiMemberPermissionsPath(memberId: string): string {
  return `${API_TEAM_PATH}/members/${encodeURIComponent(memberId)}/permissions`;
}

export const API_KNOWLEDGE_BASES_PATH = '/api/v1/knowledge-bases';

export function apiKnowledgeBasePath(knowledgeBaseId: string): string {
  return `${API_KNOWLEDGE_BASES_PATH}/${encodeURIComponent(knowledgeBaseId)}`;
}

export function apiKnowledgeSharingPath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/sharing`;
}

export function apiKnowledgeDocumentPath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/documents/${encodeURIComponent(documentId)}`;
}

export function apiKnowledgeRetryPath(knowledgeBaseId: string, documentId: string, versionId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/retry`;
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
  readonly errors?: Readonly<Record<string, unknown>>;
}

/**
 * 某個功能區的 403 預設值：body 的 `reason` 不在前端的 union 裡（例如後端保底的
 * `forbidden`）時用這一組，訊息則優先用 body 的 `message`。
 */
interface PermissionDeniedFallback {
  readonly reason: RepositoryPermissionDeniedReason;
  readonly message: string;
}

const TEAM_DENIED: PermissionDeniedFallback = { reason: 'team', message: TEAM_PERMISSION_DENIED_MESSAGE };
const KNOWLEDGE_DENIED: PermissionDeniedFallback = {
  reason: 'knowledge-base',
  message: KNOWLEDGE_PERMISSION_DENIED_MESSAGE,
};

/**
 * `/me` 中 mock 需要的部分；API 模式由 `HttpSessionBackend.restore()` 提供。
 * `accountId` 是後端的真實帳號 GUID，用來判斷團隊列表裡「哪一列是我自己」；
 * `demoAccountId` 只給仍在 mock 的功能區（`viewerOverride`）沿用現有的權限判斷。
 */
export interface ApiViewerPermissions {
  readonly accountId: string;
  readonly demoAccountId: AccountId;
  readonly permissions: readonly AccountPermission[];
  /**
   * `/me` 的真實組織 GUID，與 `accountId` 一起為 API 模式的 mock storage 鍵值加前綴
   * （見 `scoped-storage.ts`）。選填是為了不強迫既有只關心權限的測試假身分也要補上；
   * 正式的 `HttpSessionBackend.restore()` 一定會填（`ApiIdentity` 的同名欄位）。
   */
  readonly organizationId?: string;
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
function viewerOverride(
  viewer: ApiViewerPermissions | null,
  fromTeam: OwnPermissionsFromTeam,
): AccountPermissionOverrides {
  if (viewer === null) return {};
  // 團隊 API 比啟動時的 `/me` 新：登入者在團隊頁改了自己的權限時，mock 區域立即生效。
  // 以真實帳號 GUID 比對，同一分頁換帳號登入後不會套用到別人身上。
  const permissions =
    fromTeam.accountId === viewer.accountId && fromTeam.permissions !== null
      ? fromTeam.permissions
      : viewer.permissions;
  return { [viewer.demoAccountId]: permissions };
}

/** 最近一次團隊回應中「登入者自己」那一列的權限。 */
interface OwnPermissionsFromTeam {
  accountId: string | null;
  permissions: readonly AccountPermission[] | null;
}

/**
 * API 模式的 repository：已接上 API 的方法走 HTTP，其餘方法沿用 `MockDemoRepository`
 * （以繼承取得，所以 mock 的所有方法不用逐一轉接）。只由 `provideApiMode()` 建立，
 * mock 建置不含這個檔案。
 *
 * 目前接上 API 的：`getTeam`、`updateMemberPermissions`（M1；M2 起改用真實帳號 GUID
 * 當列表的 key、編輯目標與更新目標，不再換回同角色的 Demo 身分——見 issue #36）；
 * 知識庫的清單、詳情、建立、刪除、重新處理與分享（M2 Slice 11，issue #45）。
 *
 * 每個方法的形狀都一樣（後續功能區照做）：`http.<verb>` → `map` 成前端 view →
 * `catchError` 把可恢復的狀態碼（403／404／409／422）轉成結果，其餘錯誤原樣拋出，
 * 由畫面顯示「目前無法載入」。
 */
export class HybridDemoRepository extends MockDemoRepository {
  private readonly http: HttpClient;
  private readonly viewerPermissions: () => ApiViewerPermissions | null;
  private readonly ownFromTeam: OwnPermissionsFromTeam;

  constructor(seed: DemoSeed, options: MockDemoRepositoryOptions, deps: HybridDemoRepositoryDeps) {
    const ownFromTeam: OwnPermissionsFromTeam = { accountId: null, permissions: null };
    // 以真實組織／帳號 GUID 為 mock storage 的鍵值加前綴：兩個組織、或同組織中同角色的
    // 兩個帳號，共用同一瀏覽器時不會讀到彼此的草稿、對話等模擬資料（見 scoped-storage.ts）。
    // `deps.viewerPermissions` 在每次呼叫時才讀取，所以即使 repository 在登入前就已由
    // DI 建立，storage 也一定看到「目前」的身分，不是建構當下（那時通常還沒登入）。
    const storage = createScopedStorage(
      options.storage ?? createMemoryStorage(),
      (): StorageIdentity | null => {
        const viewer = deps.viewerPermissions();
        return viewer?.organizationId !== undefined
          ? { organizationId: viewer.organizationId, accountId: viewer.accountId }
          : null;
      },
    );
    super(seed, {
      ...options,
      storage,
      accountsSource: () => viewerOverride(deps.viewerPermissions(), ownFromTeam),
    });
    this.http = deps.http;
    this.viewerPermissions = deps.viewerPermissions;
    this.ownFromTeam = ownFromTeam;
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
      data: toTeamView(response, this.rememberOwnPermissions(response)),
    };
  }

  /** 記下團隊回應中登入者自己的權限（見 `viewerOverride`），回傳登入者的帳號 GUID。 */
  private rememberOwnPermissions(response: TeamResponse): string | null {
    const viewerAccountId = this.viewerPermissions()?.accountId ?? null;
    const own = response.members.find((member) => member.id === viewerAccountId);
    if (own !== undefined) {
      this.ownFromTeam.accountId = own.id;
      this.ownFromTeam.permissions = normalizeMemberPermissions(own.permissions);
    }
    return viewerAccountId;
  }

  // ---------- 知識庫 ----------

  override listKnowledgeBaseSummaries(): Observable<RepositoryView<readonly KnowledgeBaseSummaryView[]>> {
    return this.http.get<ApiKnowledgeBaseSummary[]>(API_KNOWLEDGE_BASES_PATH).pipe(
      map((response): RepositoryView<readonly KnowledgeBaseSummaryView[]> => ({
        status: 'ready',
        data: response.map((summary) => this.fromApiSummary(summary)),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED)),
    );
  }

  override getKnowledgeBaseDetail(knowledgeBaseId: string): Observable<RepositoryView<KnowledgeBaseDetailView>> {
    return this.http.get<ApiKnowledgeBaseDetail>(apiKnowledgeBasePath(knowledgeBaseId)).pipe(
      map((response): RepositoryView<KnowledgeBaseDetailView> => ({
        status: 'ready',
        data: this.fromApiDetail(response),
      })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override createKnowledgeBase(input: CreateKnowledgeBaseInput): Observable<CreateKnowledgeBaseResult> {
    const body: CreateKnowledgeBaseRequest = { name: input.name, purpose: input.purpose };
    return this.http.post<ApiKnowledgeBaseSummary>(API_KNOWLEDGE_BASES_PATH, body).pipe(
      map((response): CreateKnowledgeBaseResult => ({ status: 'ready', data: this.fromApiSummary(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422)
          ? of(knowledgeValidationFailed(error))
          : this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED),
      ),
    );
  }

  override deleteKnowledgeBase(knowledgeBaseId: string): Observable<DeleteKnowledgeResult> {
    return this.http.delete<void>(apiKnowledgeBasePath(knowledgeBaseId)).pipe(
      map((): DeleteKnowledgeResult => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override deleteKnowledgeDocument(knowledgeBaseId: string, documentId: string): Observable<DeleteKnowledgeResult> {
    return this.http.delete<void>(apiKnowledgeDocumentPath(knowledgeBaseId, documentId)).pipe(
      map((): DeleteKnowledgeResult => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override retryKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
  ): Observable<RetryKnowledgeDocumentResult> {
    return this.http
      .post<ApiKnowledgeDocument>(apiKnowledgeRetryPath(knowledgeBaseId, documentId, versionId), null)
      .pipe(
        map((response): RetryKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
        catchError((error: unknown) =>
          isHttpError(error, 409)
            ? of(knowledgeValidationFailed(error))
            : this.knowledgeDeniedOrThrow(error),
        ),
      );
  }

  override updateKnowledgeSharing(
    knowledgeBaseId: string,
    sharing: KnowledgeSharingView,
  ): Observable<UpdateKnowledgeSharingResult> {
    const body: UpdateKnowledgeSharingRequest = {
      scope: sharing.scope,
      sharedWithAccountIds: [...sharing.sharedWithAccountIds],
      allowOriginalDownload: sharing.allowOriginalDownload,
    };
    return this.http.put<ApiKnowledgeSharing>(apiKnowledgeSharingPath(knowledgeBaseId), body).pipe(
      map((response): UpdateKnowledgeSharingResult => ({ status: 'ready', data: toKnowledgeSharing(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  /**
   * 後端沒有「已連接助理」（助理在 M3 之前仍是前端 mock 資料），由 mock 的助理補上：
   * 連接到這個 id 的、目前 Demo 身分自己的助理。
   */
  private connectedAssistantsOf(knowledgeBaseId: string) {
    const viewer = this.viewer();
    return viewer === null ? [] : this.connectedKnowledgeAssistants(viewer, knowledgeBaseId);
  }

  private fromApiSummary(summary: ApiKnowledgeBaseSummary): KnowledgeBaseSummaryView {
    return {
      id: summary.id,
      name: summary.name,
      purpose: summary.purpose,
      documentCount: summary.documentCount,
      faqCount: summary.faqCount,
      statusCounts: { ...summary.statusCounts },
      sharingScope: summary.sharingScope,
      connectedAssistantNames: this.connectedAssistantsOf(summary.id).map((assistant) => assistant.name),
      updatedAt: summary.updatedAt,
      viewerCanManage: summary.viewerCanManage,
    };
  }

  private fromApiDetail(detail: ApiKnowledgeBaseDetail): KnowledgeBaseDetailView {
    return {
      summary: this.fromApiSummary(detail.summary),
      documents: detail.documents.map(toKnowledgeDocument),
      connectedAssistants: this.connectedAssistantsOf(detail.summary.id),
      sharing: toKnowledgeSharing(detail.sharing),
      shareTargets: detail.shareTargets.map(({ id, displayName }) => ({ id, displayName })),
    };
  }

  /**
   * 以 id 指定知識庫的端點：`403 knowledge-base`（不存在、別人的、別的組織的都一樣），
   * 以及 `404`——路由只接受 GUID（`{id:guid}`），網址裡不是 GUID 的 id（例如 mock 的
   * `knowledge-product-guide`）根本不會進到端點。兩者對畫面都是「無法查看這個知識庫」。
   */
  private knowledgeDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied(KNOWLEDGE_DENIED));
    return this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED);
  }

  /**
   * 403 轉成 permission-denied；其他錯誤（5xx、連線中斷）交給畫面的錯誤狀態。
   *
   * `reason` 照 body 實際的值對應（例如 `knowledge-base`、`password-change-required`），
   * 不再全部折成同一個；body 的值不在前端 union 裡時（後端保底的 `forbidden`），才用
   * `fallback` 的 reason。訊息優先用 body 的 `message`——後端與 mock 的訊息逐字相同。
   *
   * 團隊端點的 403 只有兩種原因：呼叫者沒有權限，或成員不存在／不屬於這個組織——
   * 後端刻意讓兩者回傳一模一樣的內容（`ApiErrors.NotFound` 就是 `Forbidden`），
   * 知識庫也一樣，所以這裡不必另外分辨「找不到」。
   */
  private permissionDeniedOrThrow(
    error: unknown,
    fallback: PermissionDeniedFallback = TEAM_DENIED,
  ): Observable<PermissionDeniedRepositoryView> {
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
    const reason = isRepositoryPermissionDeniedReason(body.reason) ? body.reason : fallback.reason;
    return of({ status: 'permission-denied', reason, message: message ?? fallback.message });
  }
}

function isHttpError(error: unknown, status: number): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && error.status === status;
}

function permissionDenied(fallback: PermissionDeniedFallback): PermissionDeniedRepositoryView {
  return { status: 'permission-denied', reason: fallback.reason, message: fallback.message };
}

/** `422`（逐欄錯誤）與 `409`（`reason` + `message`）的共同點：一句給人看的 `message`。 */
function knowledgeValidationFailed(error: HttpErrorResponse): KnowledgeValidationFailedView {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次變更沒有儲存，請再試一次。',
  };
}

function bodyMessage(error: HttpErrorResponse): string | null {
  const body = (error.error ?? {}) as ValidationFailedBody;
  if (typeof body.message === 'string') return body.message;
  const firstField = Object.values(body.errors ?? {})[0];
  return Array.isArray(firstField) && typeof firstField[0] === 'string' ? firstField[0] : null;
}

function toKnowledgeDocument(document: ApiKnowledgeDocument): KnowledgeDocumentView {
  return {
    id: document.id,
    kind: document.kind,
    name: document.name,
    status: document.status,
    issue: document.issue,
    updatedAt: document.updatedAt,
    latestVersionId: document.latestVersionId,
  };
}

function toKnowledgeSharing(sharing: ApiKnowledgeSharing): KnowledgeSharingView {
  return {
    scope: sharing.scope,
    sharedWithAccountIds: [...sharing.sharedWithAccountIds],
    allowOriginalDownload: sharing.allowOriginalDownload,
  };
}

/** 422：移除自己的 `manage-assistants` 或送出不認得的權限值；後端的訊息與 mock 相同。 */
function validationFailed(error: HttpErrorResponse): UpdateMemberPermissionsResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次變更沒有儲存，請再試一次。',
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
