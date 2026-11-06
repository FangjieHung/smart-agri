/** 新台幣整數元：「NT$1,280」。 */
export function ntd(amount: number): string {
  return `NT$${Math.round(amount).toLocaleString('en-US')}`;
}

/** 選項加價：「+NT$20」，不加價時是空字串。 */
export function plusNtd(amount: number): string {
  return amount > 0 ? `+${ntd(amount)}` : '';
}

/** 電話的 `tel:` 連結（只留數字與 +）。 */
export function telHref(phone: string): string {
  return `tel:${phone.replace(/[^\d+]/g, '')}`;
}

/** 地址的 Google 地圖搜尋連結。 */
export function mapHref(address: string): string {
  return `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(address)}`;
}
