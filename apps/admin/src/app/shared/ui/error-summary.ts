/**
 * 錯誤摘要的連結：直接把焦點移到出問題的欄位，
 * 不依賴瀏覽器對 fragment 連結不一致的焦點行為。
 */
export function focusErrorField(doc: Document, elementId: string, event: Event): void {
  event.preventDefault();
  const target = doc.getElementById(elementId);
  if (target === null) return;
  target.focus();
  target.scrollIntoView({ block: 'center' });
}
