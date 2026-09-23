import { signal, type Provider } from '@angular/core';
import type { AccountId } from '../../core/domain/account.model';
import type { ChatFormView, ChatReplyView } from '../../core/domain/conversation.model';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { AnonymousVisitorService } from '../../core/session/anonymous-visitor.service';
import { DemoSessionService } from '../../core/session/demo-session.service';

/**
 * 單元測試用：以記憶體儲存的 mock repository 與可切換的帳號組裝對話畫面依賴。
 * `accountId` 傳 `null` 代表沒有選過 Demo 身分，也就是未登入的官網訪客。
 */
export function provideAssistantUseTesting(
  accountId: AccountId | null = 'account-external-customer',
) {
  const storage = createMemoryStorage();
  const visitorStorage = createMemoryStorage();
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    visitorStorage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
  const activeAccountId = signal<AccountId | null>(accountId);
  const visitor = new AnonymousVisitorService({ storage: createMemoryStorage() });
  if (accountId === null) visitor.ensureVisitor();
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
    { provide: AnonymousVisitorService, useValue: visitor },
  ];

  return { providers, repository, activeAccountId, storage, visitorStorage, visitor };
}

/** 以 repository 產生一則指定問題的回覆，避免元件測試直接讀取 seed。 */
export function replyFor(text: string): ChatReplyView {
  const { repository } = provideAssistantUseTesting();
  const result = repository.sendChatMessage('account-external-customer', 'assistant-customer-service', text);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  const last = result.data.messages[result.data.messages.length - 1];
  if (last.author !== 'assistant') throw new Error('expected assistant reply');
  return last.reply;
}

export function orderForm(): ChatFormView {
  const reply = replyFor('我要回報訂單問題');
  if (reply.kind !== 'form-request') throw new Error('expected form request');
  return reply.form;
}
