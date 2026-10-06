/**
 * 發布設定的品牌色（admin 的 `WEBSITE_BRAND_COLORS`，後端 `WebsiteBrandColor`）。
 * 每個顏色與白字的對比都 >= 4.5:1（spec 檢查）；不認得的值退回森林綠，載入器的啟動按鈕也用它。
 */
export const BRAND_COLORS = {
  forest: '#1f6f5c',
  ocean: '#1d5fa8',
  amber: '#a3520c',
  plum: '#6b3fa0',
} as const;

export type BrandColorId = keyof typeof BRAND_COLORS;

export const DEFAULT_BRAND_COLOR: BrandColorId = 'forest';

export function resolveBrandColor(value: string | null | undefined): BrandColorId {
  return typeof value === 'string' && Object.hasOwn(BRAND_COLORS, value) ? (value as BrandColorId) : DEFAULT_BRAND_COLOR;
}

/**
 * 只用 CSSOM（`style.setProperty`）設定，不寫 `style` 屬性，所以不受 `style-src` 擋 inline style 屬性的影響。
 * `--color-accent-soft` 等衍生值在 token 檔裡由 `--color-accent` 算出。
 */
export function applyBrandColor(root: HTMLElement, value: string | null | undefined): BrandColorId {
  const id = resolveBrandColor(value);
  root.style.setProperty('--color-accent', BRAND_COLORS[id]);
  root.dataset['brand'] = id;
  return id;
}
