import { signal, type EnvironmentProviders, type Provider } from '@angular/core';
import { provideRouter } from '@angular/router';
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
): {
  readonly providers: (Provider | EnvironmentProviders)[];
  readonly repository: MockDemoRepository;
} {
  const activeAccountId = signal<AccountId | null>(
    options.accountId ?? 'account-smb-admin',
  );
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: options.storage ?? createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    // `listConnectableSources()` 是非同步契約，viewer 由 repository 的 `viewer` 選項推導，
    // 要與上面 `DemoSessionService` 的假身分一致，否則精靈的資料來源清單一律是空的。
    viewer: () => activeAccountId(),
  });

  return {
    repository,
    providers: [
      provideRouter([]),
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId } },
    ],
  };
}
