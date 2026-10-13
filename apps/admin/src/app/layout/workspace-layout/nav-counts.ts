/**
 * 側欄數字（案件逾期、轉真人待處理）平常只在換頁時更新（M7 決定 R）。頁面改變了數字（例如把轉真人標記為
 * 已處理，issue #355）時，在 `document` 上送出這個事件，外框就立刻重新讀取。
 */
export const NAV_COUNTS_CHANGED_EVENT = 'smartagri:nav-counts-changed';
