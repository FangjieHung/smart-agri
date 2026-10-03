import { take, type Observable } from 'rxjs';

/**
 * 測試用：mock repository 的 Observable 方法都是 `defer(() => of(...))`，訂閱當下就同步送出結果。
 * 取出那個值；沒有同步送出（例如誤用在 HTTP 上）就直接失敗，不讓測試靜默通過。
 */
export function syncValue<T>(source: Observable<T>): T {
  let emitted = false;
  let value: T | undefined;
  source.pipe(take(1)).subscribe((next) => {
    emitted = true;
    value = next;
  });
  if (!emitted) throw new Error('expected the observable to emit synchronously');
  return value as T;
}
