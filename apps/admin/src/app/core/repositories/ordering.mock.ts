import type {
  OrderingAsapBand,
  OrderingChosenOption,
  OrderingItemAvailability,
  OrderingOrderAction,
  OrderingOrderLine,
  OrderingOrderStatus,
  OrderingOrderView,
} from '../domain/ordering.model';
import { shopClock, shopInstant } from '../domain/ordering.model';

/**
 * 「點餐」頁的示範資料（issue #399，只給 mock 模式）：`deploy/ordering-shop.example.json` 的王記麵店。每個分頁至少
 * 一筆：待確認兩筆（預約、儘快）、處理中兩筆（已接單、可取餐）、今日完成兩筆（完成、婉拒）。時間都相對於建立
 * 這份資料的當下，所以任何時候打開都像「剛剛」發生。不放進 `demo-seed.ts`（後端有測試會讀它）。
 */
export const MOCK_ORDERING_TIME_ZONE = 'Asia/Taipei';
export const MOCK_ORDERING_SHOP = { shopName: '王記麵店', slug: 'wangji-noodles', menuVersion: 3 } as const;

export interface MockOrderingItem {
  readonly id: string;
  readonly name: string;
  readonly price: number;
  availability: OrderingItemAvailability;
  resetsAt: string | null;
}

export interface MockOrderingCategory {
  readonly id: string;
  readonly name: string;
  readonly items: MockOrderingItem[];
}

export interface MockOrderingState {
  orders: OrderingOrderView[];
  paused: boolean;
  asapBand: OrderingAsapBand;
  shopVersion: number;
  categories: MockOrderingCategory[];
}

const MINUTE = 60_000;

/** 每個狀態現在可以做的動作，順序同後端。 */
export function mockActions(status: OrderingOrderStatus): OrderingOrderAction[] {
  if (status === 'pending') return ['accept', 'decline'];
  if (status === 'accepted') return ['ready', 'complete', 'cancel'];
  if (status === 'ready') return ['complete', 'cancel'];
  return [];
}

function option(groupId: string, groupName: string, optionId: string, optionName: string, price = 0, count = 1, options: OrderingChosenOption[] = []): OrderingChosenOption {
  return { groupId, groupName, optionId, optionName, price, count, options };
}

function line(itemId: string, itemName: string, itemPrice: number, quantity: number, options: OrderingChosenOption[] = [], comment: string | null = null): OrderingOrderLine {
  const extra = (list: readonly OrderingChosenOption[]): number => list.reduce((sum, chosen) => sum + chosen.count * (chosen.price + extra(chosen.options)), 0);
  const unitPrice = itemPrice + extra(options);
  return { itemId, itemName, itemPrice, quantity, unitPrice, lineTotal: unitPrice * quantity, options, comment };
}

/** 下一個整半點之後再加 `slots` 個半小時（預約單的時段）。 */
function slotAfter(now: number, slots: number): number {
  const half = 30 * MINUTE;
  return Math.ceil(now / half) * half + slots * half;
}

function iso(at: number): string {
  return new Date(at).toISOString();
}

const ADMIN = { id: 'account-smb-admin', displayName: '安心商行管理者' };
const STAFF = { id: 'account-internal-employee', displayName: '安心商行客服同仁' };

export function createMockOrderingState(now: number): MockOrderingState {
  const businessDate = shopClock(now, MOCK_ORDERING_TIME_ZONE).date;
  const base = {
    businessDate, estimate: null, promisedAt: null, customerEmail: null, orderComment: null, tableware: null,
    menuVersion: MOCK_ORDERING_SHOP.menuVersion, accepted: null, ready: null, completed: null, declined: null, cancelled: null,
  } satisfies Partial<OrderingOrderView>;
  const order = (
    fields: Omit<OrderingOrderView, keyof typeof base | 'displayNumber' | 'subtotal' | 'total' | 'actions' | 'version' | 'customerPhoneDisplay' | 'status'>
      & Partial<OrderingOrderView> & { status: OrderingOrderStatus },
  ): OrderingOrderView => {
    const subtotal = fields.lines.reduce((sum, item) => sum + item.lineTotal, 0);
    const phone = fields.customerPhone;
    return {
      ...base,
      version: 1,
      ...fields,
      displayNumber: `#${String(fields.number).padStart(3, '0')}`,
      customerPhoneDisplay: `${phone.slice(0, 4)}-${phone.slice(4, 7)}-${phone.slice(7)}`,
      subtotal,
      total: subtotal,
      actions: mockActions(fields.status),
    };
  };

  const beefSet = line('beef-noodle', '紅燒牛肉麵', 150, 1, [
    option('size', '份量', 'large', '大碗', 20),
    option('noodle', '麵條', 'wide', '寬麵'),
    option('toppings', '加料', 'egg', '滷蛋', 15),
    option('combo', '升級套餐', 'set', '小菜飲料套餐', 60, 1, [
      option('side', '套餐小菜', 'cucumber', '涼拌小黃瓜'),
      option('drink', '套餐飲料', 'winter-melon', '冬瓜檸檬', 10),
    ]),
  ], '不要香菜，湯另外裝');
  const asapEstimate = { minMinutes: 15, maxMinutes: 30 };

  const orders: OrderingOrderView[] = [
    order({
      id: 'order-016', number: 16, status: 'pending', serviceType: 'takeout-scheduled', pickupAt: iso(slotAfter(now, 1)),
      customerName: '陳小姐', customerPhone: '0912345678', customerEmail: 'chen@example.com',
      lines: [beefSet, line('wonton-soup', '鮮肉餛飩湯', 70, 1)], orderComment: '到店門口會打電話', tableware: true,
      createdAt: iso(now - 2 * MINUTE), statusChangedAt: iso(now - 2 * MINUTE),
    }),
    order({
      id: 'order-015', number: 15, status: 'pending', serviceType: 'takeout-asap', pickupAt: iso(now - 6 * MINUTE + 30 * MINUTE), estimate: asapEstimate,
      customerName: '林先生', customerPhone: '0933222111',
      lines: [line('zhajiang', '炸醬麵', 80, 2, [option('spicy', '辣度', 'mild', '小辣')]), line('cucumber', '涼拌小黃瓜', 40, 1)], tableware: false,
      createdAt: iso(now - 6 * MINUTE), statusChangedAt: iso(now - 6 * MINUTE),
    }),
    order({
      id: 'order-013', number: 13, status: 'ready', version: 3, serviceType: 'takeout-asap', pickupAt: iso(now + 2 * MINUTE), estimate: asapEstimate,
      promisedAt: iso(now - MINUTE), customerName: '張先生', customerPhone: '0911222333',
      lines: [line('beef-noodle', '紅燒牛肉麵', 150, 1, [option('size', '份量', 'regular', '一般'), option('noodle', '麵條', 'thin', '細麵')])],
      createdAt: iso(now - 28 * MINUTE), statusChangedAt: iso(now - 3 * MINUTE),
      accepted: { at: iso(now - 26 * MINUTE), by: STAFF }, ready: { at: iso(now - 3 * MINUTE), by: STAFF },
    }),
    order({
      id: 'order-014', number: 14, status: 'accepted', version: 2, serviceType: 'takeout-scheduled', pickupAt: iso(slotAfter(now, 0)),
      promisedAt: iso(slotAfter(now, 0) + 10 * MINUTE), customerName: '王小姐', customerPhone: '0988777666', customerEmail: 'wang.hsiao@example.com',
      lines: [line('lunch-set', '午間乾麵套餐', 120, 2, [option('drink', '飲料', 'green-tea', '綠茶')])], tableware: true,
      createdAt: iso(now - 45 * MINUTE), statusChangedAt: iso(now - 40 * MINUTE), accepted: { at: iso(now - 40 * MINUTE), by: ADMIN },
    }),
    order({
      id: 'order-012', number: 12, status: 'completed', version: 4, serviceType: 'takeout-scheduled', pickupAt: iso(now - 50 * MINUTE),
      promisedAt: iso(now - 50 * MINUTE), customerName: '李小姐', customerPhone: '0922333444',
      lines: [line('zhajiang', '炸醬麵', 80, 1, [option('spicy', '辣度', 'none', '不辣')]), line('wonton-soup', '鮮肉餛飩湯', 70, 1)],
      createdAt: iso(now - 95 * MINUTE), statusChangedAt: iso(now - 48 * MINUTE),
      accepted: { at: iso(now - 90 * MINUTE), by: ADMIN }, ready: { at: iso(now - 55 * MINUTE), by: STAFF }, completed: { at: iso(now - 48 * MINUTE), by: STAFF },
    }),
    order({
      id: 'order-011', number: 11, status: 'declined', version: 2, serviceType: 'takeout-asap', pickupAt: iso(now - 70 * MINUTE), estimate: asapEstimate,
      customerName: '黃先生', customerPhone: '0955666777',
      lines: [line('braised-platter', '滷味拼盤', 90, 3)],
      createdAt: iso(now - 100 * MINUTE), statusChangedAt: iso(now - 98 * MINUTE),
      declined: { at: iso(now - 98 * MINUTE), by: ADMIN, reason: 'sold-out', note: '滷味今天只剩一份，抱歉！' },
    }),
  ];

  const reopens = mockReopensAt(now);
  const item = (id: string, name: string, price: number, availability: OrderingItemAvailability = 'available'): MockOrderingItem =>
    ({ id, name, price, availability, resetsAt: availability === 'sold-out-today' ? reopens : null });

  return {
    orders,
    paused: false,
    asapBand: '15-30',
    shopVersion: 1,
    categories: [
      { id: 'noodles', name: '麵食', items: [item('beef-noodle', '紅燒牛肉麵', 150), item('zhajiang', '炸醬麵', 80), item('wonton-soup', '鮮肉餛飩湯', 70)] },
      { id: 'sides', name: '小菜', items: [item('cucumber', '涼拌小黃瓜', 40), item('braised-platter', '滷味拼盤', 90, 'sold-out-today')] },
      { id: 'lunch-special', name: '平日午間限定', items: [item('lunch-set', '午間乾麵套餐', 120, 'unavailable')] },
    ],
  };
}

/** mock 的「今日售完」恢復時間：明天第一段可點餐時間 11:00（店家時區）。 */
export function mockReopensAt(now: number): string {
  const tomorrow = shopClock(now + 24 * 60 * MINUTE, MOCK_ORDERING_TIME_ZONE).date;
  return iso(shopInstant(tomorrow, '11:00', MOCK_ORDERING_TIME_ZONE));
}
