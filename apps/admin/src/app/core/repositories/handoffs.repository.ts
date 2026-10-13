import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import {
  HANDOFF_DENIED_MESSAGE,
  NOTIFICATION_EMAIL_INVALID_MESSAGE,
  normalizeNotificationEmail,
  type NotificationSettingsView,
  type UpdateNotificationSettingsRequest,
  type VisitorHandoffAttentionView,
  type VisitorHandoffChannel,
  type VisitorHandoffStatusFilter,
  type VisitorHandoffView,
} from '../domain/handoff.model';
import { DemoSessionService } from '../session/demo-session.service';
import type { PermissionDeniedRepositoryView, RepositoryView } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_HANDOFFS_PATH = '/api/v1/handoffs';

export const API_HANDOFF_ATTENTION_PATH = `${API_HANDOFFS_PATH}/attention`;

export const API_NOTIFICATION_SETTINGS_PATH = '/api/v1/me/notification-settings';

export function apiHandoffHandlePath(handoffId: string): string {
  return `${API_HANDOFFS_PATH}/${encodeURIComponent(handoffId)}:handle`;
}

/** `422`：只有 `email` 這一個欄位會錯；什麼都沒有寫入。 */
export interface NotificationSettingsInvalidView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type UpdateNotificationSettingsResult = RepositoryView<NotificationSettingsView> | NotificationSettingsInvalidView;

/** 看不到、不存在、別的組織：後端一律是同一個 `403 handoff`。 */
const HANDOFF_DENIED: PermissionDeniedRepositoryView = { status: 'permission-denied', reason: 'handoff', message: HANDOFF_DENIED_MESSAGE };

interface MockHandoff {
  readonly id: string;
  readonly assistantId: string;
  readonly channel: VisitorHandoffChannel;
  readonly createdAt: string;
  handledAt: string | null;
  handledBy: AccountId | null;
}

/**
 * mock 的範例（只放在這個檔案，不加進 `demo-seed.ts`：後端有測試會讀它）：客服助理（擁有者：管理者）在 LINE
 * 與官網各有一則待處理、官網一則已處理。
 */
const MOCK_SEEDS: readonly MockHandoff[] = [
  { id: 'handoff-line-pending', assistantId: 'assistant-customer-service', channel: 'line', createdAt: '2026-10-13T02:10:00.000Z', handledAt: null, handledBy: null },
  { id: 'handoff-website-pending', assistantId: 'assistant-customer-service', channel: 'website', createdAt: '2026-10-12T09:30:00.000Z', handledAt: null, handledBy: null },
  {
    id: 'handoff-website-handled', assistantId: 'assistant-customer-service', channel: 'website', createdAt: '2026-10-11T03:00:00.000Z',
    handledAt: '2026-10-11T04:20:00.000Z', handledBy: 'account-smb-admin',
  },
];

const REMOVED_ACCOUNT_NAME = '已停用的帳號';

/**
 * 轉真人通知與自己的通知設定的單一資料入口（issue #355）。API 模式讀寫 `/api/v1/handoffs` 與
 * `/api/v1/me/notification-settings`；純 Demo 模式只保存在這次工作階段，規則與後端相同：只有助理的擁有者
 * 看得到、能標記，標記兩次保留第一次。只會在 lazy 的程式注入（`/app/handoffs`、設定頁，以及側欄以動態
 * `import()` 載入的 `handoff-attention.loader.ts`），不進初始 bundle。
 */
@Injectable({ providedIn: 'root' })
export class HandoffsRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private mockHandoffs: MockHandoff[] = MOCK_SEEDS.map((seed) => ({ ...seed }));
  private readonly mockSettings = new Map<AccountId, { email: string | null; handoffEmails: boolean }>();

  /** 我的助理的轉真人，新的在前；預設只列待處理。 */
  list(status: VisitorHandoffStatusFilter = 'pending'): Observable<RepositoryView<readonly VisitorHandoffView[]>> {
    if (this.apiMode) {
      return this.client().get<VisitorHandoffView[]>(API_HANDOFFS_PATH, { params: new HttpParams().set('status', status) }).pipe(
        map((data): RepositoryView<readonly VisitorHandoffView[]> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<readonly VisitorHandoffView[]>(error)),
      );
    }
    return defer(() => {
      const viewer = this.session.activeAccountId();
      const rows = this.mockOwned(viewer)
        .filter((item) => status === 'all' || (status === 'pending' ? item.handledAt === null : item.handledAt !== null))
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
        .map((item) => this.mockView(item));
      return of<RepositoryView<readonly VisitorHandoffView[]>>({ status: 'ready', data: rows });
    });
  }

  /** 側欄數字：我的助理待處理的轉真人。 */
  attention(): Observable<RepositoryView<VisitorHandoffAttentionView>> {
    if (this.apiMode) {
      return this.client().get<VisitorHandoffAttentionView>(API_HANDOFF_ATTENTION_PATH).pipe(
        map((data): RepositoryView<VisitorHandoffAttentionView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<VisitorHandoffAttentionView>(error)),
      );
    }
    return defer(() => of<RepositoryView<VisitorHandoffAttentionView>>({
      status: 'ready',
      data: { pendingCount: this.mockOwned(this.session.activeAccountId()).filter((item) => item.handledAt === null).length },
    }));
  }

  /** 標記已處理；已處理過的照樣成功，保留第一次標記的人與時間。看不到是 `403 handoff`。 */
  markHandled(handoffId: string): Observable<RepositoryView<VisitorHandoffView>> {
    if (this.apiMode) {
      return this.client().post<VisitorHandoffView>(apiHandoffHandlePath(handoffId), {}).pipe(
        map((data): RepositoryView<VisitorHandoffView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<VisitorHandoffView>(error)),
      );
    }
    return defer(() => {
      const viewer = this.session.activeAccountId();
      const item = this.mockOwned(viewer).find((candidate) => candidate.id === handoffId);
      if (!item || viewer === null) return of(HANDOFF_DENIED);
      if (item.handledAt === null) {
        item.handledAt = new Date().toISOString();
        item.handledBy = viewer;
      }
      return of<RepositoryView<VisitorHandoffView>>({ status: 'ready', data: this.mockView(item) });
    });
  }

  /** 自己的通知設定；沒有設定過是「沒有地址、Email 開著」。 */
  notificationSettings(): Observable<RepositoryView<NotificationSettingsView>> {
    if (this.apiMode) {
      return this.client().get<NotificationSettingsView>(API_NOTIFICATION_SETTINGS_PATH).pipe(
        map((data): RepositoryView<NotificationSettingsView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<NotificationSettingsView>(error)),
      );
    }
    return defer(() => {
      const viewer = this.session.activeAccountId();
      if (viewer === null) return of(HANDOFF_DENIED);
      return of<RepositoryView<NotificationSettingsView>>({ status: 'ready', data: this.mockSettingsView(viewer) });
    });
  }

  /** 儲存自己的通知設定：地址不合格是 validation-failed，什麼都沒有寫入；空白清除地址。 */
  updateNotificationSettings(request: UpdateNotificationSettingsRequest): Observable<UpdateNotificationSettingsResult> {
    if (this.apiMode) {
      return this.client().put<NotificationSettingsView>(API_NOTIFICATION_SETTINGS_PATH, request).pipe(
        map((data): UpdateNotificationSettingsResult => ({ status: 'ready', data })),
        catchError((error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 422) return of(invalid(error));
          return this.denied<NotificationSettingsView>(error);
        }),
      );
    }
    return defer(() => {
      const viewer = this.session.activeAccountId();
      if (viewer === null) return of(HANDOFF_DENIED);
      const normalized = normalizeNotificationEmail(request.email);
      if (!normalized.valid) {
        return of<UpdateNotificationSettingsResult>({ status: 'validation-failed', message: NOTIFICATION_EMAIL_INVALID_MESSAGE });
      }
      this.mockSettings.set(viewer, { email: normalized.email, handoffEmails: request.handoffEmails });
      return of<UpdateNotificationSettingsResult>({ status: 'ready', data: this.mockSettingsView(viewer) });
    });
  }

  /** 與後端相同：只有助理的擁有者看得到（mock 的助理與擁有者來自 `DEMO_SEED`）。 */
  private mockOwned(viewer: AccountId | null): MockHandoff[] {
    if (viewer === null) return [];
    const owned = new Set<string>(DEMO_SEED.assistants.filter((assistant) => assistant.ownerAccountId === viewer).map((assistant) => assistant.id));
    return this.mockHandoffs.filter((item) => owned.has(item.assistantId));
  }

  private mockView(item: MockHandoff): VisitorHandoffView {
    const assistant = DEMO_SEED.assistants.find((candidate) => candidate.id === item.assistantId);
    const handler = item.handledBy === null ? null : DEMO_SEED.accounts.find((account) => account.id === item.handledBy);
    return {
      id: item.id,
      assistantId: item.assistantId,
      assistantName: assistant?.name ?? '',
      channel: item.channel,
      createdAt: item.createdAt,
      handledAt: item.handledAt,
      handledByAccountId: item.handledBy,
      handledByName: item.handledBy === null ? null : handler?.displayName ?? REMOVED_ACCOUNT_NAME,
    };
  }

  /** Demo 模式假設部署已設定寄信伺服器，讓設定畫面完整呈現（實際上不會寄出任何信）。 */
  private mockSettingsView(viewer: AccountId): NotificationSettingsView {
    const saved = this.mockSettings.get(viewer);
    return { email: saved?.email ?? null, handoffEmails: saved?.handoffEmails ?? true, emailDeliveryConfigured: true };
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  /** `403`：後端帶的原因（`handoff`、`password-change-required`）照用；其他狀態照常拋出。 */
  private denied<T>(error: unknown): Observable<RepositoryView<T>> {
    if (!(error instanceof HttpErrorResponse) || error.status !== 403) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const message = typeof body['message'] === 'string' ? body['message'] : HANDOFF_DENIED.message;
    if (body['reason'] === 'password-change-required') return of({ status: 'permission-denied', reason: 'password-change-required', message });
    return of({ ...HANDOFF_DENIED, message });
  }
}

function invalid(error: HttpErrorResponse): NotificationSettingsInvalidView {
  const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
  const errors = body['errors'] && typeof body['errors'] === 'object' ? body['errors'] as Record<string, unknown> : {};
  const field = Array.isArray(errors['email']) ? errors['email'][0] : undefined;
  const message = typeof field === 'string' ? field : typeof body['message'] === 'string' ? body['message'] : NOTIFICATION_EMAIL_INVALID_MESSAGE;
  return { status: 'validation-failed', message };
}
