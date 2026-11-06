import type { MenuItem, OptionGroup, SelectionRequest } from '../api/types';

/**
 * 商品選項的選擇狀態與前端算價（設計文件 D6、§4.1 第 5 點），照伺服器 `MenuPricing` 的規則：
 * 每個群組（不論有沒有選）都要符合最少、最多；選項選 n 份＝n ×（選項加價＋它底下所選的加價）；
 * 只有可重複的群組能讓同一個選項超過 1 份；選了某個選項，它底下的群組才要檢查。
 *
 * 前端算出的價格只用來顯示，送出時不帶價格，伺服器會用目前的菜單重算。
 */

/** 選了一個選項：份數與它底下的選擇。 */
export interface Chosen {
  readonly optionId: string;
  readonly count: number;
  readonly sub: Choices;
}

/** 依群組 id 記錄選了哪些選項（不可變；每次操作回傳新物件）。 */
export type Choices = Readonly<Record<string, readonly Chosen[]>>;

/** 一份份數的上限（`MenuLimits.MaxCount`）。 */
export const MAX_COUNT = 99;

export const NO_CHOICES: Choices = {};

/** 群組要怎麼畫：單選圓鈕、核取方塊或份數加減。 */
export type GroupControl = 'radio' | 'checkbox' | 'stepper';

/**
 * 必選且只能選一個 → 圓鈕；可重複 → 每個選項一組加減；其他 → 核取方塊。
 * 選填的單選群組（最少 0、最多 1）也用核取方塊：圓鈕選了就無法取消，顧客會被迫選一個。
 */
export function controlOf(group: OptionGroup): GroupControl {
  if (group.repeatable) return 'stepper';
  return group.max === 1 && group.min >= 1 ? 'radio' : 'checkbox';
}

/** 這個群組目前選了幾份（份數加總）。 */
export function countIn(choices: Choices, groupId: string): number {
  return (choices[groupId] ?? []).reduce((sum, chosen) => sum + chosen.count, 0);
}

export function chosenOf(choices: Choices, groupId: string, optionId: string): Chosen | undefined {
  return (choices[groupId] ?? []).find((chosen) => chosen.optionId === optionId);
}

/**
 * 點一下選項：圓鈕＝換成這個（同一個就不變）；核取方塊＝勾或取消，單選群組勾新的會取代舊的，
 * 已達上限時勾不上去（回傳原物件）。可重複群組請用 {@link setCount}。
 */
export function toggle(group: OptionGroup, choices: Choices, optionId: string): Choices {
  const current = choices[group.id] ?? [];
  const existing = current.find((chosen) => chosen.optionId === optionId);
  if (controlOf(group) === 'radio') {
    if (existing) return choices;
    return { ...choices, [group.id]: [{ optionId, count: 1, sub: NO_CHOICES }] };
  }

  if (existing) {
    return withGroup(
      choices,
      group.id,
      current.filter((chosen) => chosen.optionId !== optionId),
    );
  }

  if (group.max === 1) {
    return { ...choices, [group.id]: [{ optionId, count: 1, sub: NO_CHOICES }] };
  }

  if (countIn(choices, group.id) >= group.max) return choices;
  return { ...choices, [group.id]: [...current, { optionId, count: 1, sub: NO_CHOICES }] };
}

/** 可重複群組的份數加減：0 表示取消；超過群組剩下的份數時夾到剛好達上限。 */
export function setCount(
  group: OptionGroup,
  choices: Choices,
  optionId: string,
  count: number,
): Choices {
  const current = choices[group.id] ?? [];
  const existing = current.find((chosen) => chosen.optionId === optionId);
  const others = countIn(choices, group.id) - (existing?.count ?? 0);
  const limit = Math.min(
    MAX_COUNT,
    group.repeatable ? group.max - others : Math.min(1, group.max - others),
  );
  const next = Math.max(0, Math.min(Math.trunc(count), limit));
  if (next === 0) {
    return withGroup(
      choices,
      group.id,
      current.filter((chosen) => chosen.optionId !== optionId),
    );
  }
  if (existing) {
    return {
      ...choices,
      [group.id]: current.map((chosen) =>
        chosen.optionId === optionId ? { ...chosen, count: next } : chosen,
      ),
    };
  }
  return { ...choices, [group.id]: [...current, { optionId, count: next, sub: NO_CHOICES }] };
}

/** 一個選項最多還能選幾份（份數加減的上限）。 */
export function maxCountFor(group: OptionGroup, choices: Choices, optionId: string): number {
  const own = chosenOf(choices, group.id, optionId)?.count ?? 0;
  const room = group.max - (countIn(choices, group.id) - own);
  return Math.max(0, Math.min(MAX_COUNT, group.repeatable ? room : Math.min(1, room)));
}

/** 換掉某個已選選項底下的選擇（第二層選單按「確定」）。 */
export function setSub(choices: Choices, groupId: string, optionId: string, sub: Choices): Choices {
  const current = choices[groupId] ?? [];
  if (!current.some((chosen) => chosen.optionId === optionId)) {
    return { ...choices, [groupId]: [...current, { optionId, count: 1, sub }] };
  }
  return {
    ...choices,
    [groupId]: current.map((chosen) =>
      chosen.optionId === optionId ? { ...chosen, sub } : chosen,
    ),
  };
}

/** 取消某個選項（第二層選單按返回、剛選上的選項要還原）。 */
export function removeOption(choices: Choices, groupId: string, optionId: string): Choices {
  const current = choices[groupId] ?? [];
  if (!current.some((chosen) => chosen.optionId === optionId)) return choices;
  return withGroup(
    choices,
    groupId,
    current.filter((chosen) => chosen.optionId !== optionId),
  );
}

function withGroup(choices: Choices, groupId: string, chosen: readonly Chosen[]): Choices {
  const next = { ...choices };
  if (chosen.length === 0) {
    delete next[groupId];
  } else {
    next[groupId] = chosen;
  }
  return next;
}

/** 群組沒達到要求的原因，`path` 同伺服器（`combo.set.side`）。 */
export interface GroupIssue {
  readonly code: 'too-few' | 'too-many' | 'not-repeatable' | 'unknown-option' | 'invalid-count';
  readonly path: string;
  readonly groupId: string;
}

/** 每一層、每個群組的問題（全部列出）；空陣列表示可以加入購物車。 */
export function issues(groups: readonly OptionGroup[], choices: Choices, path = ''): GroupIssue[] {
  const found: GroupIssue[] = [];
  for (const group of groups) {
    const groupPath = join(path, group.id);
    let count = 0;
    for (const chosen of choices[group.id] ?? []) {
      const option = group.options.find((candidate) => candidate.id === chosen.optionId);
      const optionPath = join(groupPath, chosen.optionId);
      if (!option) {
        found.push({ code: 'unknown-option', path: optionPath, groupId: group.id });
        continue;
      }
      if (!Number.isInteger(chosen.count) || chosen.count < 1 || chosen.count > MAX_COUNT) {
        found.push({ code: 'invalid-count', path: optionPath, groupId: group.id });
        continue;
      }
      if (chosen.count > 1 && !group.repeatable) {
        found.push({ code: 'not-repeatable', path: optionPath, groupId: group.id });
      }
      count += chosen.count;
      found.push(...issues(option.optionGroups, chosen.sub, optionPath));
    }
    if (count < group.min) {
      found.push({ code: 'too-few', path: groupPath, groupId: group.id });
    } else if (count > group.max) {
      found.push({ code: 'too-many', path: groupPath, groupId: group.id });
    }
  }
  return found;
}

/** 這一層有問題的群組 id（含它底下各層的問題，歸到這一層的群組）。 */
export function unmetGroupIds(groups: readonly OptionGroup[], choices: Choices): string[] {
  const ids = new Set(issues(groups, choices).map((issue) => issue.path.split('.')[0]));
  return groups.filter((group) => ids.has(group.id)).map((group) => group.id);
}

/** 選項在這些群組下加的錢（每份）。 */
export function extraPrice(groups: readonly OptionGroup[], choices: Choices): number {
  let extra = 0;
  for (const group of groups) {
    for (const chosen of choices[group.id] ?? []) {
      const option = group.options.find((candidate) => candidate.id === chosen.optionId);
      if (!option) continue;
      extra += chosen.count * (option.price + extraPrice(option.optionGroups, chosen.sub));
    }
  }
  return extra;
}

/** 一份商品含選項的單價。 */
export function unitPrice(
  item: Pick<MenuItem, 'price' | 'optionGroups'>,
  choices: Choices,
): number {
  return item.price + extraPrice(item.optionGroups, choices);
}

/** 依菜單的群組與選項順序，轉成送出訂單的 `selections`（沒有選擇時是 `null`）。 */
export function toSelections(
  groups: readonly OptionGroup[],
  choices: Choices,
): SelectionRequest[] | null {
  const list: SelectionRequest[] = [];
  for (const group of groups) {
    for (const option of group.options) {
      const chosen = chosenOf(choices, group.id, option.id);
      if (!chosen) continue;
      list.push({
        groupId: group.id,
        optionId: option.id,
        count: chosen.count,
        selections: toSelections(option.optionGroups, chosen.sub),
      });
    }
  }
  return list.length > 0 ? list : null;
}

/**
 * 從購物車存的 `selections` 還原成選擇狀態，並對照目前的菜單：找不到的群組或選項會讓整筆無效
 * （回傳 `null`），由呼叫端決定移除那一行。
 */
export function fromSelections(
  groups: readonly OptionGroup[],
  selections: readonly SelectionRequest[] | null | undefined,
): Choices | null {
  const result: Record<string, Chosen[]> = {};
  for (const selection of selections ?? []) {
    const group = groups.find((candidate) => candidate.id === selection.groupId);
    const option = group?.options.find((candidate) => candidate.id === selection.optionId);
    if (!group || !option || !selection.optionId) return null;
    if ((result[group.id] ?? []).some((chosen) => chosen.optionId === option.id)) return null;
    const sub = fromSelections(option.optionGroups, selection.selections);
    if (sub === null) return null;
    (result[group.id] ??= []).push({ optionId: option.id, count: selection.count, sub });
  }
  return result;
}

/** 選了什麼的簡短說明（購物車與確認畫面用）：「大碗、細麵、滷蛋 ×2、小菜飲料套餐（涼拌小黃瓜、紅茶）」。 */
export function describe(groups: readonly OptionGroup[], choices: Choices): string {
  const parts: string[] = [];
  for (const group of groups) {
    for (const option of group.options) {
      const chosen = chosenOf(choices, group.id, option.id);
      if (!chosen) continue;
      const inner = describe(option.optionGroups, chosen.sub);
      parts.push(
        `${option.name}${chosen.count > 1 ? ` ×${chosen.count}` : ''}${inner ? `（${inner}）` : ''}`,
      );
    }
  }
  return parts.join('、');
}

/** 群組的要求說明：「必選 1 個」「選填，最多 5 個」「必選 1–2 個」。 */
export function requirementText(group: OptionGroup): string {
  if (group.min === 0) return group.max === 1 ? '選填' : `選填，最多 ${group.max} 個`;
  if (group.min === group.max) return `必選 ${group.min} 個`;
  return `必選 ${group.min}–${group.max} 個`;
}

function join(path: string, id: string): string {
  return path ? `${path}.${id}` : id;
}
