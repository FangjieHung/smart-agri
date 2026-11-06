import { provideZonelessChangeDetection, type Type } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { API_PREFIX, OrderingApi, TABLE_HEADER, TOKEN_HEADER } from '../api/ordering-api';
import type { Order, ShopInfo, TableView } from '../api/types';
import { App } from '../app';
import { EMPTY_CART, saveCart, type StoredCart } from '../domain/cart';
import { menu } from '../domain/menu.testing';
import { saveTableToken, storedTableToken } from '../domain/table';
import { ShopStore } from '../state/shop-store';
import CheckoutPage from './checkout-page';
import MenuPage from './menu-page';
import StatusPage from './status-page';

// 內用掃碼（#402）的顧客網頁：桌號網址、桌號 token 只在 sessionStorage 與 X-Ordering-Table 標頭、
// 桌號模式的菜單／結帳／訂單頁，以及沒有新欄位的舊回應。

const SLUG = 'wangji-noodles';
const TABLE = 'Tb1e_Tok3n-0123456789abcdefghijklmnopqrstuv';
const STATUS = 'kq3Vd8u6m0yJxT2bA9cR4eLw7sZpN1fH5gUiOoE_-Qa';

const open = {
  isOpen: true,
  currentPeriodOpens: '2026-11-07T03:00:00+00:00',
  currentPeriodCloses: '2026-11-07T06:00:00+00:00',
  nextOpening: null,
};

function shopInfo(patch: Partial<ShopInfo> = {}): ShopInfo {
  return {
    slug: SLUG,
    name: '王記麵店',
    phone: '06-123-4567',
    address: null,
    notices: { reminder: null, checkout: null },
    services: {
      takeoutScheduled: true,
      takeoutAsap: true,
      delivery: false,
      deliveryAsap: false,
      dineIn: true,
    },
    paused: false,
    timeZone: 'Asia/Taipei',
    now: '2026-11-07T04:00:00+00:00',
    open,
    asap: { minMinutes: 15, maxMinutes: 30 },
    slotRules: { slotMinutes: 15, daysAhead: 1, minLeadMinutes: 30 },
    showOrderComment: true,
    showTableware: true,
    menuVersion: 1,
    orderingHours: [],
    limits: {
      maxLines: 30,
      maxQuantity: 99,
      nameMaxLength: 40,
      lineCommentMaxLength: 100,
      orderCommentMaxLength: 200,
      maxTotal: 1_000_000,
    },
    delivery: null,
    dineIn: { note: null, autoAccept: false, open, canOrder: true, orderingHours: [] },
    ...patch,
  };
}

const tableView = (patch: Partial<TableView> = {}): TableView => ({
  name: 'A1',
  canOrder: true,
  session: null,
  ...patch,
});

function dineInOrder(patch: Partial<Order> = {}): Order {
  return {
    number: 3,
    displayNumber: '#003',
    status: 'pending',
    serviceType: 'dine-in',
    businessDate: '2026-11-07',
    pickupAt: '2026-11-07T04:00:00+00:00',
    estimate: null,
    createdAt: '2026-11-07T04:00:00+00:00',
    cancelledAt: null,
    lines: [
      {
        itemId: 'wonton-soup',
        itemName: '鮮肉餛飩湯',
        itemPrice: 70,
        quantity: 2,
        unitPrice: 70,
        lineTotal: 140,
        options: [],
        comment: null,
      },
    ],
    subtotal: 140,
    total: 140,
    menuVersion: 1,
    customerName: '',
    phoneLastDigits: '',
    orderComment: null,
    tableware: null,
    canCancel: true,
    promisedAt: null,
    declineReason: null,
    declineNote: null,
    cancelledBy: null,
    cancelReason: null,
    cancelNote: null,
    deliveryFee: 0,
    delivery: null,
    table: { name: 'A1' },
    autoAccepted: false,
    ...patch,
  };
}

interface Call {
  readonly url: string;
  readonly method: string;
  readonly headers: Record<string, string>;
  readonly body: Record<string, unknown> | null;
}

type Answer = { status: number; body: unknown };

/** 假的公開點餐 API：依「方法 路徑」回應，記下每一次呼叫。 */
function setup(routes: Record<string, () => Answer>) {
  const calls: Call[] = [];
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const api = TestBed.inject(OrderingApi);
  api.fetcher = async (url, init) => {
    const method = init.method ?? 'GET';
    calls.push({
      url,
      method,
      headers: init.headers as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : null,
    });
    const path = url.slice(`${API_PREFIX}${SLUG}`.length);
    const answer = routes[`${method} ${path}`]?.() ?? {
      status: 404,
      body: { status: 404, reason: 'not-mocked', message: '' },
    };
    return new Response(JSON.stringify(answer.body), {
      status: answer.status,
      headers: { 'Content-Type': 'application/json' },
    });
  };
  return { calls, store: TestBed.inject(ShopStore) };
}

const ok =
  (body: unknown, status = 200) =>
  () => ({ status, body });
const notFound =
  (reason: string, message = '') =>
  () => ({
    status: 404,
    body: { status: 404, reason, message },
  });

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 12; i++) {
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }
}

function render<T>(component: Type<T>) {
  const fixture = TestBed.createComponent(component);
  const root = fixture.nativeElement as HTMLElement;
  const text = (id: string) => root.querySelector(`[data-testid="${id}"]`)?.textContent?.trim();
  return { fixture, root, text };
}

/** 直接在店家裡、桌號模式（不經過網址）。 */
function tableMode(store: ShopStore, shop = shopInfo()): void {
  store.slug.set(SLUG);
  store.shop.set(shop);
  store.shopState.set('ready');
  store.tableToken.set(TABLE);
}

const wontonCart = (patch: Partial<StoredCart> = {}): StoredCart => ({
  ...EMPTY_CART,
  menuVersion: 1,
  lines: [
    {
      key: 'w',
      itemId: 'wonton-soup',
      name: '鮮肉餛飩湯',
      quantity: 2,
      selections: [],
      comment: null,
    },
  ],
  ...patch,
});

/** 沒有任何呼叫把桌號或狀態 token 放進網址。 */
function expectNoTokenInUrls(calls: readonly Call[]): void {
  for (const call of calls) {
    expect(call.url).not.toContain(TABLE);
    expect(call.url).not.toContain(STATUS);
    expect(call.url).not.toContain('?');
  }
}

beforeEach(() => {
  sessionStorage.clear();
  localStorage.clear();
});

describe('Dine-in: the table QR code URL (#402)', () => {
  it('keeps the token in sessionStorage, replaces the address with the menu and shows the table', async () => {
    history.replaceState(null, '', `/order/${SLUG}/table#${TABLE}`);
    const session = {
      openedAt: '2026-11-07T03:40:00+00:00',
      orderCount: 2,
      total: 540,
      orders: [
        {
          displayNumber: '#001',
          status: 'accepted' as const,
          total: 270,
          createdAt: '2026-11-07T03:40:00+00:00',
          items: [{ name: '紅燒牛肉麵', quantity: 1 }],
        },
        {
          displayNumber: '#002',
          status: 'pending' as const,
          total: 270,
          createdAt: '2026-11-07T03:50:00+00:00',
          items: [{ name: '紅燒牛肉麵', quantity: 1 }],
        },
      ],
    };
    const { calls, store } = setup({
      'GET ': ok(shopInfo()),
      'GET /menu': ok(menu(1)),
      'GET /table': ok(tableView({ session })),
    });
    const { fixture, root, text } = render(App);
    fixture.detectChanges();
    await settle(fixture);

    expect(location.pathname).toBe(`/order/${SLUG}`);
    expect(location.hash).toBe('');
    expect(storedTableToken(SLUG)).toBe(TABLE);
    expect(localStorage.length === 0 || !JSON.stringify({ ...localStorage }).includes(TABLE)).toBe(
      true,
    );
    expect(store.isTable()).toBe(true);
    expect(text('table-name')).toBe('內用・桌號 A1');
    expect(text('table-session')).toContain('這桌已點 2 筆，共 NT$540');
    expect(text('table-session')).toContain('#002');
    expect(root.querySelector('dialog[open]')).toBeNull();
    expect(root.querySelector('[data-testid="table-closed"]')).toBeNull();

    const table = calls.find((call) => call.url.endsWith('/table'));
    expect(table?.headers[TABLE_HEADER]).toBe(TABLE);
    expect(calls.some((call) => call.url.endsWith('/slots'))).toBe(false);
    expectNoTokenInUrls(calls);
    fixture.destroy();
  });

  it('a malformed token shows 「這個桌號的 QR code 已失效，請洽店員」 without asking the server', async () => {
    saveTableToken(SLUG, TABLE);
    history.replaceState(null, '', `/order/${SLUG}/table#not-a-token`);
    const { calls, store } = setup({ 'GET ': ok(shopInfo()) });
    const { fixture, text } = render(App);
    fixture.detectChanges();
    await settle(fixture);
    expect(text('table-invalid')).toContain('這個桌號的 QR code 已失效，請洽店員');
    expect(storedTableToken(SLUG)).toBeNull();
    expect(store.isTable()).toBe(false);
    expect(calls.map((call) => call.url)).toEqual([`${API_PREFIX}${SLUG}`]);
    fixture.destroy();
  });

  it('a token the shop no longer has (404 table-not-found) is forgotten and explained', async () => {
    saveTableToken(SLUG, TABLE);
    history.replaceState(null, '', `/order/${SLUG}`);
    const { store } = setup({
      'GET ': ok(shopInfo()),
      'GET /menu': ok(menu(1)),
      'GET /table': notFound('table-not-found', '這個桌號的 QR code 已失效，請洽店員。'),
    });
    const { fixture, root, text } = render(App);
    fixture.detectChanges();
    await settle(fixture);
    expect(text('table-invalid')).toContain('這個桌號的 QR code 已失效，請洽店員');
    expect(storedTableToken(SLUG)).toBeNull();
    expect(store.isTable()).toBe(false);

    // 〔看菜單〕改成一般外帶點餐。
    root.querySelector<HTMLButtonElement>('[data-testid="table-invalid"] button')?.click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="table-invalid"]')).toBeNull();
    expect(location.pathname).toBe(`/order/${SLUG}`);
    fixture.destroy();
  });

  it('the plain shop URL in a new tab (empty sessionStorage) is takeout again', async () => {
    history.replaceState(null, '', `/order/${SLUG}`);
    const { calls, store } = setup({ 'GET ': ok(shopInfo()) });
    await store.load(SLUG);
    expect(store.isTable()).toBe(false);
    expect(store.service()).toBeNull();
    expect(calls.some((call) => TABLE_HEADER in (call.headers ?? {}))).toBe(false);
  });
});

describe('Dine-in: table mode in the store (#402)', () => {
  it('needs no time: the service is dine-in now, and slots are never asked for', async () => {
    const { calls, store } = setup({ 'GET /table': ok(tableView()) });
    store.cart.set({ ...EMPTY_CART, service: { type: 'takeout-asap', date: null, slot: null } });
    tableMode(store);
    await store.loadService();
    expect(store.service()).toEqual({ type: 'dine-in', date: null, slot: null });
    expect([store.serviceReady(), store.canOrder(), store.isDelivery()]).toEqual([
      true,
      true,
      false,
    ]);
    expect(store.pickupAt()?.toISOString()).toBe('2026-11-07T04:00:00.000Z');
    expect(store.tableClosed()).toBeNull();
    expect(store.dineInPayment()).toBe('用餐後至櫃台結帳');
    expect(calls.map((call) => call.url)).toEqual([`${API_PREFIX}${SLUG}/table`]);

    // 送出後清空購物車：存著的外帶選擇不被內用蓋掉。
    store.clearCart();
    expect(store.cart().service).toEqual({ type: 'takeout-asap', date: null, slot: null });
  });

  it('paused, closed, or a table the server says cannot order now: no checkout', () => {
    const { store } = setup({});
    tableMode(store, shopInfo({ paused: true }));
    expect(store.tableClosed()).toBe('店家暫停接受線上點餐，請洽店員。');
    store.shop.set(
      shopInfo({
        dineIn: {
          note: '用餐後請至櫃台結帳。',
          autoAccept: true,
          open,
          canOrder: false,
          orderingHours: [],
        },
      }),
    );
    expect(store.canOrder()).toBe(false);
    expect(store.tableClosed()).toBe('現在不是內用點餐的時間，請洽店員。');
    expect(store.dineInPayment()).toBe('用餐後請至櫃台結帳');
    store.shop.set(shopInfo());
    store.table.set(tableView({ canOrder: false }));
    expect(store.canOrder()).toBe(false);
  });

  it('an old recorded shop answer without dineIn or services.dineIn: table mode cannot order, takeout is unchanged', () => {
    const { store } = setup({});
    const old = shopInfo();
    delete (old as Partial<ShopInfo>).dineIn;
    delete (old.services as Partial<ShopInfo['services']>).dineIn;
    store.shop.set(old);
    store.slots.set({ now: old.now, dates: [] } as never);
    expect(store.canOrder()).toBe(true);
    tableMode(store, old);
    expect(store.canOrder()).toBe(false);
    expect(store.dineInPayment()).toBe('用餐後至櫃台結帳');
  });
});

describe('Dine-in: menu page (#402)', () => {
  it('outside the dine-in hours it says so and nothing can be ordered', async () => {
    const { store } = setup({
      'GET /menu': ok(menu(1)),
      'GET /table': ok(tableView({ canOrder: false })),
    });
    tableMode(
      store,
      shopInfo({
        dineIn: { note: null, autoAccept: false, open, canOrder: false, orderingHours: [] },
      }),
    );
    const { fixture, root, text } = render(MenuPage);
    fixture.detectChanges();
    await settle(fixture);
    expect(text('table-closed')).toContain('現在不是內用點餐的時間，請洽店員');
    expect(text('table-name')).toBe('內用・桌號 A1');
    expect(root.querySelector('button.item')).toBeNull();
    fixture.destroy();
  });
});

describe('Dine-in: checkout (#402)', () => {
  function checkout(submit: () => Answer) {
    const server = setup({
      'GET /menu': ok(menu(1)),
      'GET /table': ok(tableView()),
      'POST /orders': submit,
    });
    tableMode(server.store);
    const cart = wontonCart({ orderComment: '小孩的餐先上' });
    saveCart(SLUG, cart);
    server.store.cart.set(cart);
    history.replaceState(null, '', `/order/${SLUG}/checkout`);
    return { ...server, ...render(CheckoutPage) };
  }

  async function send(fixture: ComponentFixture<unknown>, root: HTMLElement): Promise<void> {
    root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit'));
    await settle(fixture);
  }

  it('asks only an optional 稱呼 and sends a dine-in order with the table header', async () => {
    const { calls, fixture, root } = checkout(
      ok({ token: STATUS, order: dineInOrder({ customerName: '王先生' }) }, 201),
    );
    fixture.detectChanges();
    await settle(fixture);
    expect(root.querySelector('#checkout-phone')).toBeNull();
    expect(root.querySelector('#checkout-email')).toBeNull();
    expect(root.querySelector('input[name="tableware"]')).toBeNull();
    expect(root.querySelector('label[for="checkout-name"]')?.textContent).toContain('稱呼');
    expect(root.querySelector('label[for="checkout-name"]')?.textContent).toContain('選填');
    expect(root.querySelector('.payment')?.textContent).toContain('用餐後至櫃台結帳');
    expect(root.querySelector('.privacy')?.textContent).toContain('稱呼與備註');
    expect(root.querySelector('.privacy')?.textContent).toContain('90 天');

    const name = root.querySelector<HTMLInputElement>('#checkout-name');
    if (!name) throw new Error('沒有稱呼欄位');
    name.value = '王先生';
    name.dispatchEvent(new Event('input'));
    await send(fixture, root);

    const post = calls.find((call) => call.method === 'POST');
    expect(post?.url).toBe(`${API_PREFIX}${SLUG}/orders`);
    expect(post?.headers[TABLE_HEADER]).toBe(TABLE);
    expect(post?.headers[TOKEN_HEADER]).toBeUndefined();
    expect(post?.body).toMatchObject({
      serviceType: 'dine-in',
      pickupDate: null,
      pickupSlot: null,
      menuVersion: 1,
      customer: { name: '王先生', phone: null, email: null },
      orderComment: '小孩的餐先上',
      tableware: null,
      deliveryZoneId: null,
      deliveryAddress: null,
      deliveryNote: null,
    });
    expect(post?.body?.['lines']).toEqual([
      { itemId: 'wonton-soup', quantity: 2, selections: [], comment: null },
    ]);
    expect(typeof post?.body?.['submissionKey']).toBe('string');
    expect(JSON.stringify(post?.body)).not.toContain(TABLE);
    expectNoTokenInUrls(calls);
    expect(`${location.pathname}${location.hash}`).toBe(`/order/${SLUG}/o#${STATUS}`);
    fixture.destroy();
  });

  it("shows the shop's dineIn.note as the payment, not the takeout checkout notice", async () => {
    const { store, fixture, root } = checkout(ok({ token: STATUS, order: dineInOrder() }, 201));
    store.shop.set(
      shopInfo({
        notices: { reminder: null, checkout: '取餐時付款，可用現金或 LINE Pay。' },
        dineIn: {
          note: '用餐後請至櫃台結帳，可用現金或 LINE Pay。',
          autoAccept: false,
          open,
          canOrder: true,
          orderingHours: [],
        },
      }),
    );
    fixture.detectChanges();
    await settle(fixture);
    expect(root.querySelector('.payment')?.textContent?.trim()).toBe(
      '用餐後請至櫃台結帳，可用現金或 LINE Pay',
    );
    expect(root.querySelector('.notice')).toBeNull();
    expect(root.textContent).not.toContain('取餐時付款');
    fixture.destroy();
  });

  it('without a 稱呼 the name is null', async () => {
    const { calls, fixture, root } = checkout(ok({ token: STATUS, order: dineInOrder() }, 201));
    fixture.detectChanges();
    await settle(fixture);
    await send(fixture, root);
    expect(calls.find((call) => call.method === 'POST')?.body?.['customer']).toEqual({
      name: null,
      phone: null,
      email: null,
    });
    fixture.destroy();
  });

  it('outside the dine-in hours the server message is shown and no time sheet opens', async () => {
    const { fixture, root, text } = checkout(() => ({
      status: 409,
      body: {
        status: 409,
        reason: 'service-unavailable',
        message: '現在不是內用點餐的時間，請洽店員。',
      },
    }));
    fixture.detectChanges();
    await settle(fixture);
    await send(fixture, root);
    expect(text('submit-failure')).toBe('現在不是內用點餐的時間，請洽店員。');
    expect(root.querySelector('dialog[open]')).toBeNull();
    fixture.destroy();
  });

  it('the table limit (429 too-many-pending) shows the server message', async () => {
    const message = '這桌已經有 3 筆等待店家確認的訂單，請等店家確認後再加點，或洽店員。';
    const { fixture, root, text } = checkout(() => ({
      status: 429,
      body: { status: 429, reason: 'too-many-pending', message },
    }));
    fixture.detectChanges();
    await settle(fixture);
    await send(fixture, root);
    expect(text('submit-failure')).toBe(message);
    fixture.destroy();
  });

  it('a QR code that became invalid forgets the table', async () => {
    const { fixture, root, store } = checkout(
      notFound('table-not-found', '這個桌號的 QR code 已失效，請洽店員。'),
    );
    saveTableToken(SLUG, TABLE);
    fixture.detectChanges();
    await settle(fixture);
    await send(fixture, root);
    expect(store.tableInvalid()).toBe(true);
    expect(store.isTable()).toBe(false);
    expect(storedTableToken(SLUG)).toBeNull();
    fixture.destroy();
  });
});

describe('Dine-in: order status page (#402)', () => {
  function statusPage(order: Order, withTable: boolean) {
    history.replaceState(null, '', `/order/${SLUG}/o#${STATUS}`);
    const server = setup({ 'GET /orders/current': ok(order) });
    server.store.slug.set(SLUG);
    server.store.shop.set(shopInfo());
    if (withTable) server.store.tableToken.set(TABLE);
    return { ...server, ...render(StatusPage) };
  }

  it('shows the table instead of a pickup time, and 〔繼續加點〕 while the tab knows the table', async () => {
    const { calls, fixture, root, text } = statusPage(
      dineInOrder({ status: 'accepted', autoAccepted: true, canCancel: false }),
      true,
    );
    fixture.detectChanges();
    await settle(fixture);
    expect(text('order-status')).toBe('已自動接單，餐點準備中');
    expect(text('order-table')).toBe('內用・桌號 A1');
    expect(root.querySelector('[data-testid="order-pickup"]')).toBeNull();
    expect(text('order-payment')).toBe('用餐後至櫃台結帳');
    expect(root.textContent).not.toContain('取餐');
    expect(root.textContent).not.toContain('訂購人');
    expect(root.querySelector('[aria-current="step"]')?.textContent).toContain('準備中');
    const more = root.querySelector<HTMLAnchorElement>('[data-testid="order-more"]');
    expect(more?.textContent?.trim()).toBe('繼續加點');
    expect(more?.getAttribute('href')).toBe(`/order/${SLUG}`);
    expect(calls[0].headers[TOKEN_HEADER]).toBe(STATUS);
    expect(calls[0].headers[TABLE_HEADER]).toBeUndefined();
    expectNoTokenInUrls(calls);
    fixture.destroy();
  });

  it('ready and completed (〔已結帳〕) words; no 〔繼續加點〕 once the tab no longer knows the table', async () => {
    const ready = statusPage(
      dineInOrder({ status: 'ready', canCancel: false, customerName: '王先生' }),
      false,
    );
    ready.fixture.detectChanges();
    await settle(ready.fixture);
    expect(ready.text('order-status')).toBe('已出餐');
    expect(ready.root.textContent).toContain('稱呼');
    expect(ready.root.textContent).toContain('王先生');
    expect(ready.root.querySelector('[data-testid="order-more"]')).toBeNull();
    expect(ready.root.textContent).toContain('回到菜單');
    ready.fixture.destroy();
  });

  it('completed: 已結帳，謝謝光臨', async () => {
    const done = statusPage(dineInOrder({ status: 'completed', canCancel: false }), true);
    done.fixture.detectChanges();
    await settle(done.fixture);
    expect(done.text('order-status')).toBe('已結帳，謝謝光臨');
    done.fixture.destroy();
  });

  it('a pending dine-in order can be cancelled as before', async () => {
    const { fixture, root } = statusPage(dineInOrder(), true);
    fixture.detectChanges();
    await settle(fixture);
    expect(root.querySelector('[data-testid="cancel-order"]')).not.toBeNull();
    fixture.destroy();
  });

  it('an answer recorded before #402 (no table, no autoAccepted) still works', async () => {
    const old = dineInOrder({ status: 'accepted', canCancel: false }) as Partial<Order>;
    delete old.table;
    delete old.autoAccepted;
    const { fixture, text } = statusPage(old as Order, false);
    fixture.detectChanges();
    await settle(fixture);
    expect(text('order-status')).toBe('店家已接單，餐點準備中');
    expect(text('order-table')).toBe('內用');
    fixture.destroy();

    TestBed.resetTestingModule();
    const takeout = dineInOrder({
      serviceType: 'takeout-asap',
      status: 'accepted',
    }) as Partial<Order>;
    delete takeout.table;
    delete takeout.autoAccepted;
    const page = statusPage(takeout as Order, false);
    page.fixture.detectChanges();
    await settle(page.fixture);
    expect(page.text('order-status')).toBe('店家已接單，正在準備');
    expect(page.root.querySelector('[data-testid="order-table"]')).toBeNull();
    expect(page.text('order-pickup')).toContain('外帶');
    page.fixture.destroy();
  });
});
