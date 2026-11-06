import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import {
  countsInTableTotal,
  isDelivery,
  isDineIn,
  listOf,
  ORDERING_ASAP_MINUTES,
  shopClock,
  sortOrders,
  staffNoteProblem,
  type OrderingAsapBand,
  type OrderingCancelReason,
  type OrderingDeclineReason,
  type OrderingItemAvailability,
  type OrderingItemView,
  type OrderingListView,
  type OrderingMenuView,
  type OrderingOrderAction,
  type OrderingOrdersView,
  type OrderingOrderView,
  type OrderingSummaryView,
  type OrderingTablesView,
  type OrderingTableSessionView,
} from '../domain/ordering.model';
import { ApiSessionService } from '../session/api-session.service';
import { DemoSessionService } from '../session/demo-session.service';
import type { PermissionDeniedRepositoryView, RepositoryView } from './demo-repository';
import {
  createMockOrderingState,
  MOCK_ORDERING_SHOP,
  MOCK_ORDERING_TIME_ZONE,
  mockActions,
  mockReopensAt,
  type MockOrderingState,
  type MockTableSession,
} from './ordering.mock';
import { API_DEMO_REPOSITORY_FACTORY, DEMO_REPOSITORY } from './tokens';

export const API_ORDERING_PATH = '/api/v1/ordering';
export const API_ORDERING_SUMMARY_PATH = `${API_ORDERING_PATH}/summary`;
export const API_ORDERING_ORDERS_PATH = `${API_ORDERING_PATH}/orders`;
export const API_ORDERING_PAUSE_PATH = `${API_ORDERING_PATH}/pause`;
export const API_ORDERING_ASAP_BAND_PATH = `${API_ORDERING_PATH}/asap-band`;
export const API_ORDERING_ITEMS_PATH = `${API_ORDERING_PATH}/items`;
export const API_ORDERING_TABLES_PATH = `${API_ORDERING_PATH}/tables`;

export function apiOrderingCloseTablePath(sessionId: string): string {
  return `${API_ORDERING_TABLES_PATH}/sessions/${encodeURIComponent(sessionId)}/close`;
}

export function apiOrderingActionPath(orderId: string, action: OrderingOrderAction): string {
  return `${API_ORDERING_ORDERS_PATH}/${encodeURIComponent(orderId)}/${action}`;
}

export function apiOrderingItemStatePath(itemId: string): string {
  return `${API_ORDERING_ITEMS_PATH}/${encodeURIComponent(itemId)}/state`;
}

/** `404 ordering-not-configured`：這個組織還沒匯入點餐設定與菜單（每個端點都一樣）。 */
export interface OrderingNotConfiguredView {
  readonly status: 'not-configured';
  readonly message: string;
}

/** `409`：訂單已經不是畫面上的版本（`order-changed`），或這個狀態不能這樣處理（`action-not-allowed`）；附上目前的訂單。 */
export interface OrderingConflictResult {
  readonly status: 'conflict';
  readonly reason: string;
  readonly message: string;
  readonly order: OrderingOrderView;
}

/** `422`：`{ message, errors: { 欄位: [訊息] } }`；什麼都沒有寫入。 */
export interface OrderingInvalidResult {
  readonly status: 'validation-failed';
  readonly message: string;
  readonly errors: Readonly<Record<string, readonly string[]>>;
}

/** `404 item-not-found`：目前的菜單沒有這個品項（菜單剛換過）。 */
export interface OrderingItemNotFoundResult {
  readonly status: 'item-not-found';
  readonly message: string;
}

/**
 * 〔已結帳〕的 `409`（#402）：`table-has-pending`（還有待確認的訂單）或 `session-changed`（有新的加點、或別人已經結帳）；
 * 附上這桌目前的樣子（`closedAt` 有值＝已經被結帳了）。
 */
export interface OrderingTableConflictResult {
  readonly status: 'table-conflict';
  readonly reason: string;
  readonly message: string;
  readonly session: OrderingTableSessionView;
}

export type OrderingRead<T> = RepositoryView<T> | OrderingNotConfiguredView;
export type OrderingActionResult = OrderingRead<OrderingOrderView> | OrderingConflictResult | OrderingInvalidResult;
export type OrderingWriteResult<T> = OrderingRead<T> | OrderingInvalidResult | OrderingItemNotFoundResult;
export type OrderingCloseTableResult = OrderingRead<OrderingTableSessionView> | OrderingTableConflictResult | OrderingInvalidResult;

/**
 * 訂單動作要送的內容；每個動作都帶畫面上看到的 `version`（compare-and-set）。內用單（#402）接單時不帶 `promisedAt`
 * （伺服器對內用單的任何時間都回 422）。
 */
export type OrderingActionRequest =
  | { readonly action: 'accept'; readonly version: number; readonly promisedAt?: string | null }
  | { readonly action: 'decline'; readonly version: number; readonly reason: OrderingDeclineReason; readonly note: string | null }
  | { readonly action: 'ready' | 'complete'; readonly version: number }
  | { readonly action: 'cancel'; readonly version: number; readonly reason: OrderingCancelReason; readonly note: string | null };

export const ORDERING_DENIED_MESSAGE = '你沒有處理線上點餐的權限。請管理者到「團隊與設定」為你開啟「處理線上點餐」。';
export const ORDERING_NOT_CONFIGURED_MESSAGE = '這個組織還沒有設定線上點餐。';

const ORDERING_DENIED: PermissionDeniedRepositoryView = { status: 'permission-denied', reason: 'ordering', message: ORDERING_DENIED_MESSAGE };

/** 接單時間的範圍：與後端相同，容許 1 分鐘時鐘誤差、最多晚 24 小時。 */
const CLOCK_SKEW_MS = 60_000;
const MAX_DELAY_MS = 24 * 60 * 60_000;

/**
 * 線上點餐後台（issue #399）的單一資料入口。API 模式讀寫 `/api/v1/ordering`（要 `manage-orders`）；mock 模式是
 * `ordering.mock.ts` 的王記麵店，只存在這次工作階段，狀態機、版本檢查與 409 的形狀都照後端。只在 lazy 的程式
 * 注入（`/app/orders` 與側欄以動態 `import()` 載入的 `order-attention.loader.ts`），不進初始 bundle。
 */
@Injectable({ providedIn: 'root' })
export class OrderingRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly apiSession = inject(ApiSessionService);
  private readonly session = inject(DemoSessionService);
  private readonly demo = inject(DEMO_REPOSITORY);
  /** 第一次用到才建立，示範資料的時間才會相對於「打開這一頁」的時候。 */
  private mockState: MockOrderingState | null = null;

  /** 每 10 秒輪詢的計數與游標（§4.4）。 */
  summary(): Observable<OrderingRead<OrderingSummaryView>> {
    if (this.apiMode) return this.read<OrderingSummaryView>(API_ORDERING_SUMMARY_PATH);
    return this.mock((state, now) => this.mockSummary(state, now));
  }

  /** 一個清單：待確認、處理中（已接單與可取餐／已出發）或今日完成。 */
  orders(view: OrderingListView): Observable<OrderingRead<OrderingOrdersView>> {
    if (this.apiMode) return this.read<OrderingOrdersView>(API_ORDERING_ORDERS_PATH, new HttpParams().set('view', view));
    return this.mock((state, now) => ({
      now: new Date(now).toISOString(),
      view,
      orders: sortOrders(view, state.orders.filter((order) => listOf(order.status) === view)).map(copyOrder),
    }));
  }

  /** 接單、婉拒、可取餐、完成、取消；`409` 時附上目前的訂單。 */
  act(orderId: string, request: OrderingActionRequest): Observable<OrderingActionResult> {
    if (this.apiMode) {
      const { action, ...body } = request;
      return this.client().post<OrderingOrderView>(apiOrderingActionPath(orderId, action), body).pipe(
        map((data): OrderingActionResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.failed<OrderingOrderView>(error) as Observable<OrderingActionResult>),
      );
    }
    return defer(() => {
      const denied = this.mockDenied();
      if (denied) return of<OrderingActionResult>(denied);
      const state = this.state();
      const now = Date.now();
      const order = state.orders.find((candidate) => candidate.id === orderId);
      if (!order) return of<OrderingActionResult>(ORDERING_DENIED);
      if (order.version !== request.version) return of(conflict('order-changed', order));
      if (!order.actions.includes(request.action)) return of(conflict('action-not-allowed', order));
      const invalid = mockInvalid(order, request, now);
      if (invalid) return of<OrderingActionResult>(invalid);
      const at = new Date(now).toISOString();
      const by = this.mockMember();
      switch (request.action) {
        case 'accept':
          order.status = 'accepted';
          // 內用（#402）：時間就是送出的時候。
          order.promisedAt = request.promisedAt
            ?? (isDineIn(order.serviceType) ? order.pickupAt : new Date(Math.max(Date.parse(order.pickupAt), now)).toISOString());
          order.accepted = { at, by };
          break;
        case 'decline':
          order.status = 'declined';
          order.declined = { at, by, reason: request.reason, note: request.note?.trim() || null };
          break;
        case 'ready':
          order.status = 'ready';
          order.ready = { at, by };
          break;
        case 'complete':
          order.status = 'completed';
          order.completed = { at, by };
          break;
        case 'cancel':
          order.status = 'cancelled';
          order.cancelled = { at, by: 'shop', member: by, reason: request.reason, note: request.note?.trim() || null };
          break;
      }
      order.version += 1;
      order.statusChangedAt = at;
      order.actions = mockActions(order.status ?? 'pending');
      return of<OrderingActionResult>({ status: 'ready', data: copyOrder(order) });
    });
  }

  /** 開著的桌次（內用，#402）：每一桌還沒結帳的訂單，早開桌的在前。 */
  tables(): Observable<OrderingRead<OrderingTablesView>> {
    if (this.apiMode) return this.read<OrderingTablesView>(API_ORDERING_TABLES_PATH);
    return this.mock((state, now) => ({
      now: new Date(now).toISOString(),
      sessions: state.sessions
        .filter((session) => session.closedAt === null)
        .sort((a, b) => Date.parse(a.openedAt) - Date.parse(b.openedAt))
        .map((session) => mockSessionView(state, session)),
    }));
  }

  /**
   * 〔已結帳〕（#402）：帶畫面上看到的桌次 `version`；已接單與已出餐的訂單變成完成，桌次關閉。還有待確認的訂單、或版本不同時
   * 是 `409`，附上這桌目前的樣子。
   */
  closeTable(sessionId: string, version: number): Observable<OrderingCloseTableResult> {
    if (this.apiMode) {
      return this.client().post<OrderingTableSessionView>(apiOrderingCloseTablePath(sessionId), { version }).pipe(
        map((data): OrderingCloseTableResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.failed<OrderingTableSessionView>(error) as Observable<OrderingCloseTableResult>),
      );
    }
    return defer(() => {
      const denied = this.mockDenied();
      if (denied) return of<OrderingCloseTableResult>(denied);
      const state = this.state();
      const session = state.sessions.find((candidate) => candidate.id === sessionId);
      if (!session) return of<OrderingCloseTableResult>(ORDERING_DENIED);
      const orders = state.orders.filter((order) => order.table?.sessionId === session.id);
      if (session.closedAt !== null || session.version !== version) {
        return of(tableConflict('session-changed', mockSessionView(state, session)));
      }
      if (orders.some((order) => order.status === 'pending')) return of(tableConflict('table-has-pending', mockSessionView(state, session)));
      const at = new Date(Date.now()).toISOString();
      const by = this.mockMember();
      for (const order of orders.filter((candidate) => candidate.status === 'accepted' || candidate.status === 'ready')) {
        order.status = 'completed';
        order.completed = { at, by };
        order.version += 1;
        order.statusChangedAt = at;
        order.actions = mockActions('completed');
      }
      session.closedAt = at;
      session.closedBy = by;
      session.version += 1;
      return of<OrderingCloseTableResult>({ status: 'ready', data: mockSessionView(state, session) });
    });
  }

  /** 暫停或恢復接單；回傳新的 summary。 */
  setPaused(paused: boolean): Observable<OrderingWriteResult<OrderingSummaryView>> {
    if (this.apiMode) return this.write<OrderingSummaryView>(API_ORDERING_PAUSE_PATH, { paused });
    return this.mock((state, now) => {
      if (state.paused !== paused) {
        state.paused = paused;
        state.shopVersion += 1;
      }
      return this.mockSummary(state, now);
    });
  }

  /** 即時單準備時間四檔；回傳新的 summary。 */
  setAsapBand(band: OrderingAsapBand): Observable<OrderingWriteResult<OrderingSummaryView>> {
    if (this.apiMode) return this.write<OrderingSummaryView>(API_ORDERING_ASAP_BAND_PATH, { band });
    return this.mock((state, now) => {
      if (state.asapBand !== band) {
        state.asapBand = band;
        state.shopVersion += 1;
      }
      return this.mockSummary(state, now);
    });
  }

  /** 目前菜單的品項與供應狀態。 */
  items(): Observable<OrderingRead<OrderingMenuView>> {
    if (this.apiMode) return this.read<OrderingMenuView>(API_ORDERING_ITEMS_PATH);
    return this.mock((state, now) => ({
      now: new Date(now).toISOString(),
      menuVersion: MOCK_ORDERING_SHOP.menuVersion,
      categories: state.categories.map((category) => ({
        id: category.id,
        name: category.name,
        items: category.items.map((item) => ({ ...item })),
      })),
    }));
  }

  /** 供應中、今日售完（到下一個營業日第一段可點餐時間自動恢復）或停售。 */
  setItemState(itemId: string, state: OrderingItemAvailability): Observable<OrderingWriteResult<OrderingItemView>> {
    if (this.apiMode) return this.write<OrderingItemView>(apiOrderingItemStatePath(itemId), { state });
    return defer(() => {
      const denied = this.mockDenied();
      if (denied) return of(denied);
      const mock = this.state();
      const item = mock.categories.flatMap((category) => category.items).find((candidate) => candidate.id === itemId);
      if (!item) return of<OrderingWriteResult<OrderingItemView>>({ status: 'item-not-found', message: '目前的菜單沒有這個品項，請重新整理。' });
      if (item.availability !== state) {
        item.availability = state;
        item.resetsAt = state === 'sold-out-today' ? mockReopensAt(Date.now()) : null;
        mock.shopVersion += 1;
      }
      return of<OrderingWriteResult<OrderingItemView>>({ status: 'ready', data: { ...item } });
    });
  }

  private state(): MockOrderingState {
    return (this.mockState ??= createMockOrderingState(Date.now()));
  }

  private mock<T>(read: (state: MockOrderingState, now: number) => T): Observable<OrderingRead<T>> {
    return defer(() => {
      const denied = this.mockDenied();
      if (denied) return of(denied);
      return of<OrderingRead<T>>({ status: 'ready', data: read(this.state(), Date.now()) });
    });
  }

  /** 與後端相同：要登入而且有 `manage-orders`。 */
  private mockDenied(): PermissionDeniedRepositoryView | null {
    if (this.session.activeAccountId() === null) return ORDERING_DENIED;
    return this.apiSession.permissions().includes('manage-orders') ? null : ORDERING_DENIED;
  }

  private mockMember(): { id: string; displayName: string } | null {
    const accountId = this.session.activeAccountId();
    if (accountId === null) return null;
    const accounts = this.demo.listAccounts();
    const account = accounts.status === 'ready' ? accounts.data.find((candidate) => candidate.id === accountId) : undefined;
    return { id: accountId, displayName: account?.displayName ?? '已停用的帳號' };
  }

  private mockSummary(state: MockOrderingState, now: number): OrderingSummaryView {
    const latest = (values: readonly string[]): string | null =>
      values.length === 0 ? null : values.reduce((a, b) => (Date.parse(a) >= Date.parse(b) ? a : b));
    return {
      now: new Date(now).toISOString(),
      businessDate: shopClock(now, MOCK_ORDERING_TIME_ZONE).date,
      timeZone: MOCK_ORDERING_TIME_ZONE,
      shopName: MOCK_ORDERING_SHOP.shopName,
      slug: MOCK_ORDERING_SHOP.slug,
      pendingCount: state.orders.filter((order) => order.status === 'pending').length,
      activeCount: state.orders.filter((order) => listOf(order.status) === 'active').length,
      latestOrderAt: latest(state.orders.map((order) => order.createdAt)),
      // 桌次開桌與結帳也算（#402）。
      changedAt: latest([
        ...state.orders.map((order) => order.statusChangedAt),
        ...state.sessions.flatMap((session) => [session.openedAt, ...(session.closedAt ? [session.closedAt] : [])]),
      ]),
      paused: state.paused,
      asapBand: state.asapBand,
      asap: { ...ORDERING_ASAP_MINUTES[state.asapBand] },
      services: { takeoutScheduled: true, takeoutAsap: true, delivery: true, deliveryAsap: true, dineIn: true },
      menuVersion: MOCK_ORDERING_SHOP.menuVersion,
      shopVersion: state.shopVersion,
      openTableCount: state.sessions.filter((session) => session.closedAt === null).length,
    };
  }

  private read<T>(path: string, params?: HttpParams): Observable<OrderingRead<T>> {
    return this.client().get<T>(path, { params }).pipe(
      map((data): OrderingRead<T> => ({ status: 'ready', data })),
      // 讀取的端點不會回 409、422 與 item-not-found（§4.4）。
      catchError((error: unknown) => this.failed<T>(error) as Observable<OrderingRead<T>>),
    );
  }

  private write<T>(path: string, body: unknown): Observable<OrderingWriteResult<T>> {
    return this.client().put<T>(path, body).pipe(
      map((data): OrderingWriteResult<T> => ({ status: 'ready', data })),
      // 店家開關的端點不會回 409（§4.4）。
      catchError((error: unknown) => this.failed<T>(error) as Observable<OrderingWriteResult<T>>),
    );
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  /** 後台慣例的錯誤（§4.4）：403、404 兩種原因、409 附訂單、422 逐欄位；其他照常拋出。 */
  private failed<T>(error: unknown): Observable<
    OrderingRead<T> | OrderingConflictResult | OrderingTableConflictResult | OrderingInvalidResult | OrderingItemNotFoundResult
  > {
    if (!(error instanceof HttpErrorResponse)) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const message = typeof body['message'] === 'string' ? body['message'] : '';
    if (error.status === 403) {
      if (body['reason'] === 'password-change-required') return of({ status: 'permission-denied', reason: 'password-change-required', message });
      return of({ ...ORDERING_DENIED, message: message || ORDERING_DENIED_MESSAGE });
    }
    if (error.status === 404 && body['reason'] === 'ordering-not-configured') {
      return of({ status: 'not-configured', message: message || ORDERING_NOT_CONFIGURED_MESSAGE });
    }
    if (error.status === 404 && body['reason'] === 'item-not-found') return of({ status: 'item-not-found', message });
    if (error.status === 409 && body['session'] && typeof body['session'] === 'object') {
      return of({
        status: 'table-conflict',
        reason: typeof body['reason'] === 'string' ? body['reason'] : '',
        message,
        session: body['session'] as OrderingTableSessionView,
      });
    }
    if (error.status === 409 && body['order'] && typeof body['order'] === 'object') {
      return of({ status: 'conflict', reason: typeof body['reason'] === 'string' ? body['reason'] : '', message, order: body['order'] as OrderingOrderView });
    }
    if (error.status === 422) {
      const errors = body['errors'] && typeof body['errors'] === 'object' ? body['errors'] as Record<string, readonly string[]> : {};
      return of({ status: 'validation-failed', message, errors });
    }
    return throwError(() => error);
  }
}

function copyOrder(order: OrderingOrderView): OrderingOrderView {
  return { ...order, actions: [...order.actions] };
}

/** mock 的桌次：訂單舊的在前；合計不含婉拒、取消、逾時（同後端）。 */
function mockSessionView(state: MockOrderingState, session: MockTableSession): OrderingTableSessionView {
  const orders = state.orders
    .filter((order) => order.table?.sessionId === session.id)
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  return {
    id: session.id,
    version: session.version,
    tableId: session.tableId,
    tableName: session.tableName,
    openedAt: session.openedAt,
    total: orders.filter((order) => countsInTableTotal(order.status)).reduce((sum, order) => sum + order.total, 0),
    pendingCount: orders.filter((order) => order.status === 'pending').length,
    orders: orders.map(copyOrder),
    closedAt: session.closedAt,
    closedBy: session.closedBy,
  };
}

function tableConflict(reason: 'session-changed' | 'table-has-pending', session: OrderingTableSessionView): OrderingTableConflictResult {
  return {
    status: 'table-conflict',
    reason,
    message: reason === 'table-has-pending' ? '這桌還有待確認的訂單，請先接單或婉拒再結帳。' : '這桌剛剛有新的變動，請確認金額後再結帳。',
    session,
  };
}

function conflict(reason: 'order-changed' | 'action-not-allowed', order: OrderingOrderView): OrderingConflictResult {
  return {
    status: 'conflict',
    reason,
    message: reason === 'order-changed' ? '這筆訂單剛剛被更新了，請確認目前的狀態。' : '這筆訂單目前的狀態不能這樣處理。',
    order: copyOrder(order),
  };
}

/** 與後端相同的檢查：原因、說明、接單時間的範圍。 */
function mockInvalid(order: OrderingOrderView, request: OrderingActionRequest, now: number): OrderingInvalidResult | null {
  const invalid = (field: string, message: string): OrderingInvalidResult => ({ status: 'validation-failed', message, errors: { [field]: [message] } });
  if (request.action === 'decline' || request.action === 'cancel') {
    const problem = staffNoteProblem(request.note ?? '', request.reason === 'other');
    return problem ? invalid('note', problem) : null;
  }
  const promisedAt = request.action === 'accept' ? request.promisedAt ?? null : null;
  if (promisedAt !== null && isDineIn(order.serviceType)) return invalid('promisedAt', '內用訂單不需要指定時間。');
  if (promisedAt !== null) {
    const promised = Date.parse(promisedAt);
    const asap = order.serviceType === 'takeout-asap' || order.serviceType === 'delivery-asap';
    const what = isDelivery(order.serviceType) ? '預計送達時間' : '預計取餐時間';
    const earliest = asap ? now - CLOCK_SKEW_MS : Date.parse(order.pickupAt);
    const latest = Math.max(Date.parse(order.pickupAt), now) + MAX_DELAY_MS;
    if (Number.isNaN(promised) || promised < earliest) {
      return invalid('promisedAt', asap ? `${what}不能早於現在。` : `${what}不能早於顧客選的時間。`);
    }
    if (promised > latest) return invalid('promisedAt', `${what}最多延後 24 小時。`);
  }
  return null;
}
