import type { Injector, WritableSignal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HandoffsRepository } from '../../core/repositories/handoffs.repository';
import { DEMO_ACCOUNT_BY_ROLE } from '../../core/session/api-mode/demo-identity-bridge';
import { DemoSessionService } from '../../core/session/demo-session.service';

let latestRequest = 0;

/**
 * 側欄「轉真人」旁的待處理數字（issue #355）：與「案件」的逾期數字同一個做法——shell 換頁時以動態 `import()`
 * 呼叫（首頁的 chunk 轉出它，`WorkspaceLayoutComponent.refreshNavCounts`），`HandoffsRepository` 與它的
 * HTTP 呼叫不進初始 bundle。
 *
 * - 外部客戶與沒有身分時是 0，**不發**請求（外部客戶不會是助理的擁有者）；
 * - 沒有權限是 0；讀不到（網路錯誤等）沿用上一次的數字；
 * - 較早送出、較晚回來的結果不會蓋掉較新的。
 */
export async function refreshHandoffPendingCount(injector: Injector, count: WritableSignal<number>): Promise<void> {
  const request = ++latestRequest;
  let next: number | null = null;
  try {
    const accountId = injector.get(DemoSessionService).activeAccountId();
    if (accountId === null || accountId === DEMO_ACCOUNT_BY_ROLE['external-customer']) {
      next = 0;
    } else {
      const view = await firstValueFrom(injector.get(HandoffsRepository).attention());
      if (view.status === 'ready') next = view.data.pendingCount;
      else if (view.status === 'permission-denied') next = 0;
    }
  } catch {
    // 讀不到（網路錯誤，或 shell 已被銷毀）：沿用上一次的數字。
  }
  if (request === latestRequest && next !== null) count.set(next);
}
