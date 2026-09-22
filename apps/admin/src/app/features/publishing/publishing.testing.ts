import { signal, type Provider } from '@angular/core';
import type { AccountId } from '../../core/domain/account.model';
import type { AssistantPublishingView } from '../../core/domain/publishing.model';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';

/** 單元測試用：以記憶體儲存的 mock repository 與固定帳號組裝發布畫面依賴。 */
export function providePublishingTesting(accountId: AccountId = 'account-smb-admin') {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(accountId) } },
  ];

  return { providers, repository };
}

/** 透過 repository 取得助理發布設定，避免元件測試直接讀取 seed。 */
export function publishingOf(repository: MockDemoRepository, assistantId: string): AssistantPublishingView {
  const result = repository.getAssistantPublishing('account-smb-admin', assistantId);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}
