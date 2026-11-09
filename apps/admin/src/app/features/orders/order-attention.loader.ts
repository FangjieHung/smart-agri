import type { Injector, WritableSignal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { OrderingRepository } from '../../core/repositories/ordering.repository';
import { ApiSessionService } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';

let latestRequest = 0;

/**
 * 側欄「點餐」旁的待確認數字（issue #399）：與「案件」「轉真人」同一個做法——shell 換頁時（以及頁面送出
 * `NAV_COUNTS_CHANGED_EVENT` 時）以動態 `import()` 呼叫（首頁的 chunk 轉出它），不另設計時器。「點餐」頁開著時
 * 每 10 秒讀 summary，數字變了就送出那個事件，所以店員在點餐頁上看到的徽章跟著輪詢更新。
 *
 * - 沒有身分或沒有 `manage-orders` 時是 0，**不發**請求（側欄也不顯示這一項）；
 * - 沒有權限或還沒設定線上點餐是 0；讀不到（網路錯誤等）沿用上一次的數字；
 * - 較早送出、較晚回來的結果不會蓋掉較新的。
 */
export async function refreshOrderPendingCount(injector: Injector, count: WritableSignal<number>): Promise<void> {
  const request = ++latestRequest;
  let next: number | null = null;
  try {
    const accountId = injector.get(DemoSessionService).activeAccountId();
    if (accountId === null || !injector.get(ApiSessionService).permissions().includes('manage-orders')) {
      next = 0;
    } else {
      const view = await firstValueFrom(injector.get(OrderingRepository).summary());
      if (view.status === 'ready') next = view.data.pendingCount;
      else if (view.status === 'permission-denied' || view.status === 'not-configured') next = 0;
    }
  } catch {
    // 讀不到（網路錯誤，或 shell 已被銷毀）：沿用上一次的數字。
  }
  if (request === latestRequest && next !== null) count.set(next);
}
