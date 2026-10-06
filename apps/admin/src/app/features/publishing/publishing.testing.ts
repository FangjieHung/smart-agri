import { signal, type EnvironmentProviders, type Provider } from '@angular/core';
import { provideRouter } from '@angular/router';
import type { AccountId } from '../../core/domain/account.model';
import type { AssistantPublishingView } from '../../core/domain/publishing.model';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';

/** 單元測試用：以記憶體儲存的 mock repository 與固定帳號組裝發布畫面依賴。 */
export function providePublishingTesting(accountId: AccountId = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    // 發布的非同步契約（issue #81）由 repository 的 `viewer` 推導目前帳號，要與假工作階段一致。
    viewer: () => activeAccountId(),
  });
  const providers: (Provider | EnvironmentProviders)[] = [
    provideRouter([]),
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];

  return { providers, repository };
}

/**
 * 透過 repository 取得助理發布設定，避免元件測試直接讀取 seed。mock 的 Observable 是
 * `defer(of)`，訂閱當下就同步送出，所以這裡可以同步取值。
 */
export function publishingOf(repository: MockDemoRepository, assistantId: string): AssistantPublishingView {
  let view: AssistantPublishingView | null = null;
  repository.getAssistantPublishing(assistantId).subscribe((result) => {
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
    view = result.data as AssistantPublishingView;
  });
  if (view === null) throw new Error('expected a synchronous mock result');
  return view;
}
