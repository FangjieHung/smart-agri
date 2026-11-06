import type { Menu, MenuItem, SelectionRequest, ServiceType } from '../api/types';
import { describe, fromSelections, issues, unitPrice, type Choices } from './selection';

/** 購物車的一行：送出時的形狀（不含價格），加上前端自己的識別碼。 */
export interface CartLine {
  readonly key: string;
  readonly itemId: string;
  readonly quantity: number;
  readonly selections: SelectionRequest[] | null;
  readonly comment: string | null;
  /** 加入時的品名：菜單改版、品項下架時用來告訴顧客移除了什麼（不送出）。 */
  readonly name?: string;
}

/** 顧客選的取餐方式與時間。 */
export interface ServiceChoice {
  readonly type: ServiceType;
  /** 預約才有：`GET slots` 的 `date`。 */
  readonly date: string | null;
  /** 預約才有：`GET slots` 的 `value`。 */
  readonly slot: string | null;
}

/** 存在瀏覽器裡的購物車（每家店一份）。 */
export interface StoredCart {
  readonly menuVersion: number;
  readonly lines: readonly CartLine[];
  readonly orderComment: string;
  readonly tableware: boolean | null;
  readonly service: ServiceChoice | null;
  /** 菜單改版後還沒按「知道了」的說明（重新整理後仍顯示）。 */
  readonly notice?: string | null;
}

export const EMPTY_CART: StoredCart = {
  menuVersion: 0,
  lines: [],
  orderComment: '',
  tableware: null,
  service: null,
};

const PREFIX = 'smart-agri-shop';
export const cartKey = (slug: string) => `${PREFIX}:cart:${slug}`;

/** localStorage 在私密瀏覽、被封鎖時會丟例外；失敗就當成沒有存。 */
export function readJson<T>(key: string): T | null {
  try {
    const raw = globalThis.localStorage?.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

export function writeJson(key: string, value: unknown): void {
  try {
    if (value === null) {
      globalThis.localStorage?.removeItem(key);
    } else {
      globalThis.localStorage?.setItem(key, JSON.stringify(value));
    }
  } catch {
    // 存不了就只放在記憶體裡（重新整理後消失）。
  }
}

export function loadCart(slug: string): StoredCart {
  const stored = readJson<Partial<StoredCart>>(cartKey(slug));
  if (!stored || !Array.isArray(stored.lines)) return EMPTY_CART;
  return {
    menuVersion: typeof stored.menuVersion === 'number' ? stored.menuVersion : 0,
    lines: stored.lines.filter(isLine),
    orderComment: typeof stored.orderComment === 'string' ? stored.orderComment : '',
    tableware: typeof stored.tableware === 'boolean' ? stored.tableware : null,
    service: stored.service ?? null,
    notice: typeof stored.notice === 'string' ? stored.notice : null,
  };
}

export function saveCart(slug: string, cart: StoredCart): void {
  writeJson(cartKey(slug), cart);
}

function isLine(value: unknown): value is CartLine {
  const line = value as CartLine;
  return (
    !!line &&
    typeof line.key === 'string' &&
    typeof line.itemId === 'string' &&
    Number.isInteger(line.quantity) &&
    (line.selections === null || Array.isArray(line.selections))
  );
}

export function findItem(menu: Menu, itemId: string): MenuItem | undefined {
  for (const category of menu.categories) {
    const item = category.items.find((candidate) => candidate.id === itemId);
    if (item) return item;
  }
  return undefined;
}

/** 一行購物車對照菜單的結果。 */
export interface PricedCartLine {
  readonly line: CartLine;
  readonly item: MenuItem;
  readonly choices: Choices;
  readonly unitPrice: number;
  readonly lineTotal: number;
  readonly summary: string;
}

/** 對照菜單算價；品項不存在、選項不合菜單時是 `null`。 */
export function priceLine(menu: Menu, line: CartLine): PricedCartLine | null {
  const item = findItem(menu, line.itemId);
  if (!item) return null;
  const choices = fromSelections(item.optionGroups, line.selections);
  if (choices === null || issues(item.optionGroups, choices).length > 0) return null;
  if (!Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > 99) return null;
  const unit = unitPrice(item, choices);
  return {
    line,
    item,
    choices,
    unitPrice: unit,
    lineTotal: unit * line.quantity,
    summary: describe(item.optionGroups, choices),
  };
}

/** 菜單改版後重新檢查購物車：留下仍然有效的行，列出被移除的品名（找不到品項時用「已下架的品項」）。 */
export function revalidate(cart: StoredCart, menu: Menu): { cart: StoredCart; removed: string[] } {
  const kept: CartLine[] = [];
  const removed: string[] = [];
  for (const line of cart.lines) {
    if (priceLine(menu, line)) {
      kept.push(line);
    } else {
      removed.push(line.name ?? findItem(menu, line.itemId)?.name ?? '已下架的品項');
    }
  }
  return { cart: { ...cart, lines: kept, menuVersion: menu.version }, removed };
}

/** 兩行是不是同一個東西（同品項、同選項、同備註）：再加一次時合併份數。 */
export function sameLine(
  a: Pick<CartLine, 'itemId' | 'selections' | 'comment'>,
  b: Pick<CartLine, 'itemId' | 'selections' | 'comment'>,
): boolean {
  return (
    a.itemId === b.itemId &&
    (a.comment ?? '') === (b.comment ?? '') &&
    JSON.stringify(a.selections) === JSON.stringify(b.selections)
  );
}

let counter = 0;
export function newLineKey(): string {
  counter += 1;
  return `${Date.now().toString(36)}-${counter.toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
}
