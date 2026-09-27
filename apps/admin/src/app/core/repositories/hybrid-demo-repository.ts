import {
  HttpErrorResponse,
  HttpEventType,
  type HttpClient,
  type HttpProgressEvent,
  type HttpResponse,
} from '@angular/common/http';
import { catchError, filter, map, of, throwError, type Observable } from 'rxjs';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission, AccountRole } from '../domain/account.model';
import type { AssistantId } from '../domain/assistant.model';
import {
  ACCOUNT_PERMISSIONS,
  ACCOUNT_ROLE_DESCRIPTIONS,
  ACCOUNT_ROLE_LABELS,
  normalizeMemberPermissions,
  type TeamMemberView,
  type TeamView,
} from '../domain/team.model';
import {
  isKnowledgeUploadRejectionReason,
  type CreateKnowledgeBaseInput,
  type KnowledgeAccountRefView,
  type KnowledgeActivityAction,
  type KnowledgeActivityView,
  type KnowledgeBaseDetailView,
  type KnowledgeBaseSummaryView,
  type KnowledgeChunkView,
  type KnowledgeDocumentDetailView,
  type KnowledgeDocumentView,
  type KnowledgeExtractedUnitView,
  type KnowledgeSharingView,
  type KnowledgeUploadRejectionReason,
  type KnowledgeVersionPreviewView,
  type KnowledgeVersionView,
} from '../domain/knowledge-base.model';
import type { ConnectableSourceView } from '../domain/assistant-draft.model';
import type {
  AssistantChatView,
  ChatMessageView,
  ChatReplyView,
  ChatThreadListView,
  ChatThreadSummaryView,
  RecentConversationView,
} from '../domain/conversation.model';
import {
  isRepositoryPermissionDeniedReason,
  type ApproveKnowledgeVersionsResult,
  type CreateKnowledgeBaseResult,
  type CreateMemberInput,
  type CreateMemberResult,
  type DeleteKnowledgeResult,
  type DisableKnowledgeDocumentResult,
  type EnableKnowledgeDocumentResult,
  type KnowledgeUploadRejectedView,
  type KnowledgeValidationFailedView,
  type PermissionDeniedRepositoryView,
  type RenameChatThreadResult,
  type RepositoryPermissionDeniedReason,
  type RepositoryView,
  type RetryKnowledgeDocumentResult,
  type UpdateKnowledgeChunkExclusionResult,
  type UpdateKnowledgeSharingResult,
  type UpdateMemberPermissionsResult,
  type UploadKnowledgeDocumentEvent,
} from './demo-repository';
import type { DemoSeed } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import {
  connectableKnowledgeStatus,
  KNOWLEDGE_PERMISSION_DENIED_MESSAGE,
  MockDemoRepository,
  TEAM_PERMISSION_DENIED_MESSAGE,
  type AccountPermissionOverrides,
  type MockDemoRepositoryOptions,
} from './mock-demo-repository';
import { createScopedStorage, type StorageIdentity } from './scoped-storage';

type TeamResponse = components['schemas']['TeamResponse'];
type ApiTeamMember = components['schemas']['TeamMemberResponse'];
type UpdateMemberPermissionsRequest = components['schemas']['UpdateMemberPermissionsRequest'];
type CreateMemberRequest = components['schemas']['CreateMemberRequest'];
type CreateMemberResponse = components['schemas']['CreateMemberResponse'];
type ApiKnowledgeBaseSummary = components['schemas']['KnowledgeBaseSummaryView'];
type ApiKnowledgeBaseDetail = components['schemas']['KnowledgeBaseDetailView'];
type ApiKnowledgeDocument = components['schemas']['KnowledgeDocumentView'];
type ApiKnowledgeSharing = components['schemas']['KnowledgeSharingView'];
type CreateKnowledgeBaseRequest = components['schemas']['CreateKnowledgeBaseRequest'];
type UpdateKnowledgeSharingRequest = components['schemas']['UpdateKnowledgeSharingRequest'];
type ApiKnowledgeDocumentDetail = components['schemas']['KnowledgeDocumentDetailView'];
type ApiKnowledgeVersion = components['schemas']['KnowledgeVersionView'];
type ApiKnowledgeActivity = components['schemas']['KnowledgeActivityView'];
type ApiKnowledgeAccount = components['schemas']['KnowledgeAccountView'];
type ApiKnowledgeVersionPreview = components['schemas']['KnowledgeVersionPreviewView'];
type ApiKnowledgeExtractedUnit = components['schemas']['KnowledgeExtractedUnitView'];
type ApiKnowledgeChunk = components['schemas']['KnowledgeChunkView'];
type ApproveKnowledgeVersionsRequest = components['schemas']['ApproveKnowledgeVersionsRequest'];
type DisableKnowledgeDocumentRequest = components['schemas']['DisableKnowledgeDocumentRequest'];
type UpdateKnowledgeChunkExclusionRequest = components['schemas']['UpdateKnowledgeChunkExclusionRequest'];
type ApiChatThreadListView = components['schemas']['ChatThreadListView'];
type ApiChatThreadSummaryView = components['schemas']['ChatThreadSummaryView'];
type ApiAssistantChatView = components['schemas']['AssistantChatView'];
type ApiChatMessageView = components['schemas']['ChatMessageView'];
type ApiChatReplyView = components['schemas']['ChatReplyView'];
type ApiRecentConversationView = components['schemas']['RecentConversationView'];
type RenameChatThreadRequest = components['schemas']['RenameChatThreadRequest'];

export const API_TEAM_PATH = '/api/v1/team';

export const API_CREATE_MEMBER_PATH = `${API_TEAM_PATH}/members`;

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

export function apiKnowledgeDocumentsPath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/documents`;
}

export function apiKnowledgeDocumentVersionsPath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions`;
}

export function apiKnowledgeRetryPath(knowledgeBaseId: string, documentId: string, versionId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/retry`;
}

/** 文件詳情：與 `apiKnowledgeDocumentPath` 相同的網址，動詞是 `GET`（issue #47）。 */
export function apiKnowledgeDocumentDetailPath(knowledgeBaseId: string, documentId: string): string {
  return apiKnowledgeDocumentPath(knowledgeBaseId, documentId);
}

export function apiKnowledgeVersionPreviewPath(knowledgeBaseId: string, documentId: string, versionId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/preview`;
}

export function apiKnowledgeChunkExclusionPath(
  knowledgeBaseId: string,
  documentId: string,
  versionId: string,
  chunkId: string,
): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/chunks/${encodeURIComponent(chunkId)}/exclusion`;
}

export function apiKnowledgeApprovePath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/versions/approve`;
}

export function apiKnowledgeDisablePath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/disable`;
}

export function apiKnowledgeEnablePath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/enable`;
}

export function apiAssistantChatConversationsPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat/conversations`;
}

export function apiAssistantChatConversationPath(assistantId: string, threadId: string): string {
  return `${apiAssistantChatConversationsPath(assistantId)}/${encodeURIComponent(threadId)}`;
}

export function apiAssistantChatPath(assistantId: string, threadId?: string): string {
  const base = `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat`;
  return threadId === undefined ? base : `${base}?conversation=${encodeURIComponent(threadId)}`;
}

export const API_RECENT_CONVERSATIONS_PATH = '/api/v1/chat/recent-conversations';

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

/** 對話端點的 403 後端一定會帶 `assistant-use` 或 `chat-thread`；這是保底用的預設值。 */
const CHAT_DENIED: PermissionDeniedFallback = {
  reason: 'assistant-use',
  message: '你沒有使用這個助理的權限，或它已不存在。',
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
 * 知識庫的清單、詳情、建立、刪除、重新處理與分享（M2 Slice 11，issue #45）；助理精靈與
 * 設定頁的可連接來源清單（M2 Slice 15，issue #49）。
 *
 * 每個方法的形狀都一樣（後續功能區照做）：`http.<verb>` → `map` 成前端 view →
 * `catchError` 把可恢復的狀態碼（403／404／409／422）轉成結果，其餘錯誤原樣拋出，
 * 由畫面顯示「目前無法載入」。
 */
export class HybridDemoRepository extends MockDemoRepository {
  private readonly http: HttpClient;
  /**
   * 最近一次從 API 讀到、這個帳號可連接的知識庫。助理仍是 mock（#81 才換 API），
   * 同步的 `createAssistantFromDraft`／`setAssistantSourceConnection` 要用它驗證來源，
   * 否則真實 GUID 會被 mock 的種子清單當成「不可連接」而默默濾掉。精靈與設定頁的 store
   * 一建立就會呼叫 `listConnectableSources`，所以寫入前這份清單已經讀過。
   */
  private readonly apiConnectableKnowledge = new Map<AccountId, readonly ConnectableSourceView[]>();
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

  /**
   * 新增組織成員（issue #52，M2 Slice 18）：後端已經套用 `manage-assistants` 與同組織登入
   * 名稱唯一的規則，這裡只轉譯結果——`422` 轉成 validation-failed，`403` 沿用團隊的
   * permission-denied。一次性密碼原樣轉交給畫面，這個方法本身不記錄、不快取它。
   */
  override createMember(input: CreateMemberInput): Observable<CreateMemberResult> {
    const body: CreateMemberRequest = {
      loginName: input.loginName,
      displayName: input.displayName,
      role: input.role,
      permissions: [...input.permissions],
    };
    return this.http.post<CreateMemberResponse>(API_CREATE_MEMBER_PATH, body).pipe(
      map((response): CreateMemberResult => ({
        status: 'ready',
        data: {
          member: toTeamMemberView(response.member, this.viewerPermissions()?.accountId ?? null),
          oneTimePassword: response.oneTimePassword,
        },
      })),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(createMemberValidationFailed(error)) : this.permissionDeniedOrThrow(error),
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

  /**
   * 助理精靈與設定頁的可連接來源清單（M2 Slice 15，issue #49）：知識庫走 API、只列出
   * `viewerCanManage` 為真的（與 mock 只列自己擁有的一致），資料庫仍是 mock，
   * 由繼承的 `connectableDatabaseSources` 補上。沒有 `manage-assistants` 權限時
   * （或尚未登入）直接回傳空清單，不必打 API。
   */
  override listConnectableSources(): Observable<RepositoryView<readonly ConnectableSourceView[]>> {
    const viewer = this.viewer();
    if (viewer === null || !this.canManageAssistants(viewer)) {
      return of({ status: 'ready', data: [] });
    }

    return this.http.get<ApiKnowledgeBaseSummary[]>(API_KNOWLEDGE_BASES_PATH).pipe(
      map((response): RepositoryView<readonly ConnectableSourceView[]> => {
        const knowledgeBases = response
          .filter((summary) => summary.viewerCanManage)
          .map((summary): ConnectableSourceView => ({
            id: summary.id,
            type: 'knowledge-base',
            name: summary.name,
            summary: `${summary.documentCount} 份文件、${summary.faqCount} 則 FAQ`,
            permission: 'owner',
            status: connectableKnowledgeStatus(summary.statusCounts),
            updatedAt: summary.updatedAt,
          }));

        this.apiConnectableKnowledge.set(viewer, knowledgeBases);
        return {
          status: 'ready',
          data: [...knowledgeBases, ...this.connectableDatabaseSources(viewer)],
        };
      }),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED)),
    );
  }

  protected override connectableSources(viewerAccountId: AccountId): readonly ConnectableSourceView[] {
    if (!this.canManageAssistants(viewerAccountId)) return [];
    return [
      ...(this.apiConnectableKnowledge.get(viewerAccountId) ?? []),
      ...this.connectableDatabaseSources(viewerAccountId),
    ];
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

  // ---------- 版本確認、抽取預覽與緊急停用（issue #47，M2 Slice 13） ----------

  override getKnowledgeDocumentDetail(
    knowledgeBaseId: string,
    documentId: string,
  ): Observable<RepositoryView<KnowledgeDocumentDetailView>> {
    return this.http.get<ApiKnowledgeDocumentDetail>(apiKnowledgeDocumentDetailPath(knowledgeBaseId, documentId)).pipe(
      map((response): RepositoryView<KnowledgeDocumentDetailView> => ({
        status: 'ready',
        data: toKnowledgeDocumentDetail(response),
      })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override previewKnowledgeVersion(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
  ): Observable<RepositoryView<KnowledgeVersionPreviewView>> {
    return this.http
      .get<ApiKnowledgeVersionPreview>(apiKnowledgeVersionPreviewPath(knowledgeBaseId, documentId, versionId))
      .pipe(
        map((response): RepositoryView<KnowledgeVersionPreviewView> => ({
          status: 'ready',
          data: toKnowledgeVersionPreview(response),
        })),
        catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
      );
  }

  override updateKnowledgeChunkExclusion(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
    chunkId: string,
    excluded: boolean,
  ): Observable<UpdateKnowledgeChunkExclusionResult> {
    const body: UpdateKnowledgeChunkExclusionRequest = { excluded };
    return this.http
      .put<ApiKnowledgeChunk>(apiKnowledgeChunkExclusionPath(knowledgeBaseId, documentId, versionId, chunkId), body)
      .pipe(
        map((response): UpdateKnowledgeChunkExclusionResult => ({
          status: 'ready',
          data: toKnowledgeChunk(response),
        })),
        catchError((error: unknown) =>
          isHttpError(error, 422) ? of(knowledgeValidationFailed(error)) : this.knowledgeDeniedOrThrow(error),
        ),
      );
  }

  override approveKnowledgeVersions(
    knowledgeBaseId: string,
    versionIds: readonly string[],
    effectiveFrom?: string,
  ): Observable<ApproveKnowledgeVersionsResult> {
    const body: ApproveKnowledgeVersionsRequest = {
      versionIds: [...versionIds],
      ...(effectiveFrom !== undefined ? { effectiveFrom } : {}),
    };
    return this.http.post<ApiKnowledgeVersion[]>(apiKnowledgeApprovePath(knowledgeBaseId), body).pipe(
      map((response): ApproveKnowledgeVersionsResult => ({
        status: 'ready',
        data: response.map(toKnowledgeVersion),
      })),
      catchError((error: unknown) =>
        isHttpError(error, 422) || isHttpError(error, 409)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  override disableKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
    reason: string,
  ): Observable<DisableKnowledgeDocumentResult> {
    const body: DisableKnowledgeDocumentRequest = { reason };
    return this.http.post<ApiKnowledgeDocument>(apiKnowledgeDisablePath(knowledgeBaseId, documentId), body).pipe(
      map((response): DisableKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) || isHttpError(error, 409)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  override enableKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
  ): Observable<EnableKnowledgeDocumentResult> {
    return this.http.post<ApiKnowledgeDocument>(apiKnowledgeEnablePath(knowledgeBaseId, documentId), null).pipe(
      map((response): EnableKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 409) ? of(knowledgeValidationFailed(error)) : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  // ---------- 對話（issue #79：讀取與對話串管理走 API，送出訊息仍是 mock，見 #80） ----------

  override listChatThreads(assistantId: string): Observable<RepositoryView<ChatThreadListView>> {
    return this.http.get<ApiChatThreadListView>(apiAssistantChatConversationsPath(assistantId)).pipe(
      map((response): RepositoryView<ChatThreadListView> => ({
        status: 'ready',
        data: toChatThreadListView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  override createChatThread(assistantId: string): Observable<RepositoryView<AssistantChatView>> {
    return this.http.post<ApiAssistantChatView>(apiAssistantChatConversationsPath(assistantId), null).pipe(
      map((response): RepositoryView<AssistantChatView> => ({
        status: 'ready',
        data: toAssistantChatView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  override renameChatThread(
    assistantId: string,
    threadId: string,
    title: string,
  ): Observable<RenameChatThreadResult> {
    const body: RenameChatThreadRequest = { title };
    return this.http
      .patch<ApiChatThreadSummaryView>(apiAssistantChatConversationPath(assistantId, threadId), body)
      .pipe(
        map((response): RenameChatThreadResult => ({ status: 'ready', data: toChatThreadSummary(response) })),
        catchError((error: unknown) =>
          isHttpError(error, 422) ? of(chatValidationFailed(error)) : this.chatDeniedOrThrow(error),
        ),
      );
  }

  override deleteChatThread(
    assistantId: string,
    threadId: string,
  ): Observable<RepositoryView<ChatThreadListView>> {
    return this.http.delete<ApiChatThreadListView>(apiAssistantChatConversationPath(assistantId, threadId)).pipe(
      map((response): RepositoryView<ChatThreadListView> => ({
        status: 'ready',
        data: toChatThreadListView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  /** 側欄的最近對話：讀取失敗時當作沒有可顯示的，不讓整個側欄跟著顯示錯誤。 */
  override listRecentChatThreads(): Observable<RepositoryView<readonly RecentConversationView[]>> {
    return this.http.get<ApiRecentConversationView[]>(API_RECENT_CONVERSATIONS_PATH).pipe(
      map((response): RepositoryView<readonly RecentConversationView[]> => ({
        status: 'ready',
        data: response.map(toRecentConversationView),
      })),
      catchError(() => of({ status: 'ready' as const, data: [] })),
    );
  }

  override getAssistantChat(
    assistantId: string,
    threadId?: string,
  ): Observable<RepositoryView<AssistantChatView>> {
    return this.http.get<ApiAssistantChatView>(apiAssistantChatPath(assistantId, threadId)).pipe(
      map((response): RepositoryView<AssistantChatView> => ({
        status: 'ready',
        data: toAssistantChatView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  /** `403` 的 `reason` 一定是 `assistant-use` 或 `chat-thread`；`404` 視同對話不存在。 */
  private chatDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied({ reason: 'chat-thread', message: '找不到這段對話，或它不屬於你的帳號。' }));
    return this.permissionDeniedOrThrow(error, CHAT_DENIED);
  }

  /**
   * 上傳一個檔案成為新文件（issue #46）：`reportProgress` 讓 `HttpClient` 送出上傳進度事件
   * （`observe: 'events'`），只轉出上傳進度與最終回應，其餘事件類型（`Sent` 等）忽略。
   * `413`／`415`／`422` 轉成逐檔的 `rejected` 結果；其餘沿用知識庫的 403／404 轉換。
   */
  override uploadKnowledgeDocument(
    knowledgeBaseId: string,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return this.postKnowledgeUpload(apiKnowledgeDocumentsPath(knowledgeBaseId), file);
  }

  override uploadKnowledgeDocumentVersion(
    knowledgeBaseId: string,
    documentId: string,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return this.postKnowledgeUpload(apiKnowledgeDocumentVersionsPath(knowledgeBaseId, documentId), file);
  }

  private postKnowledgeUpload(path: string, file: File): Observable<UploadKnowledgeDocumentEvent> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<ApiKnowledgeDocument>(path, body, { reportProgress: true, observe: 'events' }).pipe(
      filter(
        (event): event is HttpProgressEvent | HttpResponse<ApiKnowledgeDocument> =>
          event.type === HttpEventType.UploadProgress || event.type === HttpEventType.Response,
      ),
      map((event): UploadKnowledgeDocumentEvent => {
        if (event.type === HttpEventType.Response) {
          return { status: 'ready', data: toKnowledgeDocument(event.body as ApiKnowledgeDocument) };
        }
        // event.type === HttpEventType.UploadProgress（篩選過，只剩這兩種）
        const percent = event.total ? Math.min(99, Math.round((event.loaded / event.total) * 100)) : 0;
        return { status: 'progress', percent };
      }),
      catchError((error: unknown) =>
        isHttpError(error, 413) || isHttpError(error, 415) || isHttpError(error, 422)
          ? of(uploadRejected(error))
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
      inEffectCount: summary.inEffectCount,
      awaitingApprovalCount: summary.awaitingApprovalCount,
      disabledCount: summary.disabledCount,
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

/** 413／415 沒有 `reason`（只有 422 的 body 有）時的保底原因。 */
function fallbackUploadReason(status: number): KnowledgeUploadRejectionReason {
  if (status === 413) return 'file-too-large';
  if (status === 415) return 'unsupported-file-type';
  return 'file-unreadable';
}

interface UploadRejectionBody {
  readonly reason?: unknown;
  readonly message?: unknown;
  readonly existingDocumentName?: unknown;
}

/** `413`／`415`／`422`：`reason` 照 body 實際的值對應，訊息與 `existingDocumentName` 原樣轉交。 */
function uploadRejected(error: HttpErrorResponse): KnowledgeUploadRejectedView {
  const body = (error.error ?? {}) as UploadRejectionBody;
  const reason = isKnowledgeUploadRejectionReason(body.reason) ? body.reason : fallbackUploadReason(error.status);
  const message = typeof body.message === 'string' ? body.message : '這個檔案沒有上傳成功，請再試一次。';
  const existingDocumentName = typeof body.existingDocumentName === 'string' ? body.existingDocumentName : undefined;
  return {
    status: 'rejected',
    reason,
    message,
    ...(existingDocumentName !== undefined ? { existingDocumentName } : {}),
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
    latestVersionNumber: document.latestVersionNumber,
    latestVersionState: document.latestVersionState,
    effectiveVersionNumber: document.effectiveVersionNumber,
    disabled: document.disabled,
    inEffect: document.inEffect,
  };
}

function toKnowledgeAccountRef(account: ApiKnowledgeAccount | null): KnowledgeAccountRefView | null {
  return account === null ? null : { id: account.id, displayName: account.displayName };
}

function toKnowledgeVersion(version: ApiKnowledgeVersion): KnowledgeVersionView {
  return {
    id: version.id,
    documentId: version.documentId,
    versionNumber: version.versionNumber,
    fileName: version.fileName,
    contentType: version.contentType,
    sizeBytes: version.sizeBytes,
    status: version.status,
    issue: version.issue,
    state: version.state,
    effectiveFrom: version.effectiveFrom,
    uploadedBy: toKnowledgeAccountRef(version.uploadedBy) ?? { id: version.id, displayName: '不明帳號' },
    uploadedAt: version.uploadedAt,
    approvedBy: toKnowledgeAccountRef(version.approvedBy),
    approvedAt: version.approvedAt,
    updatedAt: version.updatedAt,
  };
}

/**
 * 活動紀錄的 `action` 後端是跨知識庫事件的完整聯集（含分享、知識庫本身的建立／刪除），
 * 但這個端點（文件詳情）只會回傳與這份文件或其版本有關的子集；未知值一律當成
 * `document-uploaded`（畫面只用來挑圖示與文字，不影響任何判斷）。
 */
function toKnowledgeActivityAction(action: ApiKnowledgeActivity['action']): KnowledgeActivityAction {
  const known: readonly string[] = [
    'document-uploaded',
    'version-uploaded',
    'version-retried',
    'version-approved',
    'chunk-excluded',
    'chunk-included',
    'document-disabled',
    'document-enabled',
    'document-deleted',
  ];
  return (known.includes(action) ? action : 'document-uploaded') as KnowledgeActivityAction;
}

function toKnowledgeActivity(activity: ApiKnowledgeActivity): KnowledgeActivityView {
  return {
    id: activity.id,
    action: toKnowledgeActivityAction(activity.action),
    actor: toKnowledgeAccountRef(activity.actor),
    at: activity.at,
    versionId: activity.versionId,
    versionNumber: activity.versionNumber,
    reason: activity.reason,
  };
}

function toKnowledgeDocumentDetail(detail: ApiKnowledgeDocumentDetail): KnowledgeDocumentDetailView {
  return {
    document: toKnowledgeDocument(detail.document),
    createdAt: detail.createdAt,
    disabledAt: detail.disabledAt,
    disabledBy: toKnowledgeAccountRef(detail.disabledBy),
    disabledReason: detail.disabledReason,
    versions: detail.versions.map(toKnowledgeVersion),
    activities: detail.activities.map(toKnowledgeActivity),
  };
}

function toKnowledgeChunk(chunk: ApiKnowledgeChunk): KnowledgeChunkView {
  return { id: chunk.id, locationLabel: chunk.locationLabel, text: chunk.text, excluded: chunk.excluded };
}

function toKnowledgeExtractedUnit(unit: ApiKnowledgeExtractedUnit): KnowledgeExtractedUnitView {
  return {
    ordinal: unit.ordinal,
    locationKind: unit.locationKind,
    locationLabel: unit.locationLabel,
    readable: unit.readable,
    issueCode: unit.issueCode,
    text: unit.text,
    chunks: unit.chunks.map(toKnowledgeChunk),
  };
}

function toKnowledgeVersionPreview(preview: ApiKnowledgeVersionPreview): KnowledgeVersionPreviewView {
  return {
    documentId: preview.documentId,
    versionId: preview.versionId,
    versionNumber: preview.versionNumber,
    fileName: preview.fileName,
    status: preview.status,
    issue: preview.issue,
    units: preview.units.map(toKnowledgeExtractedUnit),
  };
}

function toKnowledgeSharing(sharing: ApiKnowledgeSharing): KnowledgeSharingView {
  return {
    scope: sharing.scope,
    sharedWithAccountIds: [...sharing.sharedWithAccountIds],
    allowOriginalDownload: sharing.allowOriginalDownload,
  };
}

/** `422`（標題空白或過長）只有一句 `message`。 */
function chatValidationFailed(error: HttpErrorResponse): RenameChatThreadResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次沒有儲存，請再試一次。',
  };
}

function toChatThreadSummary(summary: ApiChatThreadSummaryView): ChatThreadSummaryView {
  return {
    id: summary.id,
    title: summary.title,
    messageCount: summary.messageCount,
    updatedAt: summary.updatedAt,
  };
}

function toChatThreadListView(response: ApiChatThreadListView): ChatThreadListView {
  return {
    assistantId: response.assistantId as AssistantId,
    assistantName: response.assistantName,
    historyMode: response.historyMode as ChatThreadListView['historyMode'],
    threads: response.threads.map((thread) => ({
      id: thread.id,
      title: thread.title,
      messageCount: thread.messageCount,
      updatedAt: thread.updatedAt,
    })),
    historyNotice: response.historyNotice,
  };
}

/**
 * 後端的 `ChatReplyView` 是同一個扁平形狀（不適用的欄位省略），不是前端的
 * discriminated union（`docs/plans/2026-09-27-backend-milestone-3-in-platform-chat.md`
 * 第 3 節「與前端型別的差異」）；這裡依 `kind` 轉回前端的三種變體。`form-request` 與
 * `submission-receipt` 兩種目前後端還沒有，理論上不會出現。
 */
function toChatReply(reply: ApiChatReplyView): ChatReplyView {
  if (reply.kind === 'general-knowledge') {
    return { kind: 'general-knowledge', text: reply.text, notice: reply.notice ?? '' };
  }
  if (reply.kind === 'no-result') {
    return { kind: 'no-result', text: reply.text, nextSteps: [...reply.nextSteps] };
  }
  // 'company-data'，以及任何後端未來新增、前端還不認得的 kind 一律當成組織資料回覆。
  return {
    kind: 'company-data',
    text: reply.text,
    citations: reply.citations.map((citation) => ({
      id: citation.id,
      knowledgeBaseName: citation.knowledgeBaseName,
      documentName: citation.documentName,
      excerpt: citation.excerpt,
      updatedLabel: citation.updatedLabel,
    })),
    citationNotice: reply.notice ?? null,
  };
}

function toChatMessage(message: ApiChatMessageView): ChatMessageView {
  if (message.reply !== null) {
    return { id: message.id, author: 'assistant', reply: toChatReply(message.reply), createdAt: message.createdAt };
  }
  return { id: message.id, author: 'account', text: message.text ?? '', createdAt: message.createdAt };
}

function toAssistantChatView(response: ApiAssistantChatView): AssistantChatView {
  return {
    assistantId: response.assistantId as AssistantId,
    assistantName: response.assistantName,
    purpose: response.purpose,
    threadId: response.threadId,
    title: response.title,
    historyMode: response.historyMode as AssistantChatView['historyMode'],
    welcome: response.welcome,
    privacyNotice: response.privacyNotice,
    // 後端目前固定回傳 `[]`（PR #94）；id 一律當成不透明字串轉型，即使日後開放也不必再改。
    suggestedPrompts: response.suggestedPrompts.map((prompt) => ({
      id: prompt.id as AssistantChatView['suggestedPrompts'][number]['id'],
      text: prompt.text,
    })),
    messages: response.messages.map(toChatMessage),
  };
}

function toRecentConversationView(view: ApiRecentConversationView): RecentConversationView {
  return {
    assistantId: view.assistantId as AssistantId,
    assistantName: view.assistantName,
    threadId: view.threadId,
    title: view.title,
    messageCount: view.messageCount,
    updatedAt: view.updatedAt,
  };
}

/** 422：移除自己的 `manage-assistants` 或送出不認得的權限值；後端的訊息與 mock 相同。 */
function validationFailed(error: HttpErrorResponse): UpdateMemberPermissionsResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次變更沒有儲存，請再試一次。',
  };
}

/** 422：登入名稱重複、欄位長度不對或不認得的角色／權限值；後端的訊息直接顯示。 */
function createMemberValidationFailed(error: HttpErrorResponse): CreateMemberResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次沒有新增成員，請再試一次。',
  };
}

/** `viewerAccountId` 是目前登入者的真實帳號 GUID（來自 `/me`），不是 Demo 身分 id。 */
function toTeamMemberView(member: ApiTeamMember, viewerAccountId: string | null): TeamMemberView {
  return {
    id: member.id,
    displayName: member.displayName,
    role: member.role,
    roleLabel: ACCOUNT_ROLE_LABELS[member.role],
    roleDescription: ACCOUNT_ROLE_DESCRIPTIONS[member.role],
    permissions: normalizeMemberPermissions(member.permissions),
    isViewer: member.id === viewerAccountId,
    lockedPermissions: normalizeMemberPermissions(member.lockedPermissions),
  };
}

/** `viewerAccountId` 是目前登入者的真實帳號 GUID（來自 `/me`），不是 Demo 身分 id。 */
export function toTeamView(response: TeamResponse, viewerAccountId: string | null): TeamView {
  const members = response.members
    .map((member) => toTeamMemberView(member, viewerAccountId))
    // API 已依 id 排序；這裡改用 mock 慣用的角色順序呈現。`sort` 是穩定排序，
    // 同角色的多位成員（例如兩位 smb-internal）維持 API 回傳的順序。
    .sort((a, b) => ROLE_ORDER.indexOf(a.role) - ROLE_ORDER.indexOf(b.role));

  return { members, permissions: ACCOUNT_PERMISSIONS, savedAt: response.savedAt };
}
