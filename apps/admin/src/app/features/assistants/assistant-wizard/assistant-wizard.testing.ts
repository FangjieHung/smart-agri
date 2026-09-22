import { signal, type Provider } from '@angular/core';
import type { AccountId } from '../../../core/domain/account.model';
import type { DemoKeyValueStorage } from '../../../core/repositories/demo-repository';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 單元測試用：以記憶體儲存的 mock repository 與固定帳號組裝精靈依賴。 */
export function provideWizardTesting(
  options: {
    readonly storage?: DemoKeyValueStorage;
    readonly accountId?: AccountId;
  } = {},
): { readonly providers: Provider[]; readonly repository: MockDemoRepository } {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: options.storage ?? createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
  const activeAccountId = signal<AccountId | null>(
    options.accountId ?? 'account-smb-admin',
  );

  return {
    repository,
    providers: [
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId } },
    ],
  };
}
