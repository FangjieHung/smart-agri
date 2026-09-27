import { signal, type Provider } from '@angular/core';
import type { AccountId } from '../../core/domain/account.model';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';

/** 單元測試用：以記憶體儲存的 mock repository 與固定帳號組裝知識庫畫面依賴。 */
export function provideKnowledgeTesting(
  accountId: AccountId = 'account-smb-admin',
  /** 需要模擬時間經過（處理進度）時傳入；預設固定在 2026-09-22T02:00Z。 */
  now: () => Date = () => new Date('2026-09-22T02:00:00.000Z'),
): { readonly providers: Provider[]; readonly repository: MockDemoRepository } {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now,
    viewer: () => accountId,
  });

  return {
    repository,
    providers: [
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId: signal(accountId) } },
    ],
  };
}
