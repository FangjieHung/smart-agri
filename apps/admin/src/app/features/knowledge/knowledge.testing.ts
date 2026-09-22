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
): { readonly providers: Provider[]; readonly repository: MockDemoRepository } {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });

  return {
    repository,
    providers: [
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId: signal(accountId) } },
    ],
  };
}
