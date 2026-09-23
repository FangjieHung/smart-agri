import type { AccountId, VisitorId } from '../domain/account.model';
import type { AssistantChatView } from '../domain/conversation.model';
import type { SendChatMessageResult, SubmitChatFormResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const PUBLISHED = 'assistant-customer-service';
const INTERNAL_ONLY = 'assistant-internal-onboarding';
const VISITOR: VisitorId = 'visitor-aaaaaaaa';
const OTHER_VISITOR: VisitorId = 'visitor-bbbbbbbb';
const ORDER_ANSWERS = {
  'field-order-number': 'DEMO-9001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

/** shared＝所有帳號共用的 localStorage；visitor＝只屬於一個瀏覽器分頁的 sessionStorage。 */
function createRepository(shared = createMemoryStorage(), visitorStorage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage: shared,
    visitorStorage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function chatOf(result: SendChatMessageResult | SubmitChatFormResult): AssistantChatView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function ask(
  repository: MockDemoRepository,
  viewer: AccountId | VisitorId,
  text: string,
): AssistantChatView {
  return chatOf(repository.sendChatMessage(viewer, PUBLISHED, text));
}

describe('MockDemoRepository anonymous visitors', () => {
  it('opens an assistant that is published to an external channel', () => {
    const result = createRepository().getAssistantChat(VISITOR, PUBLISHED);

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.assistantName).toBe('客服助理');
    expect(result.data.messages).toEqual([]);
  });

  it('tells the visitor the conversation ends with the browser tab', () => {
    const result = createRepository().getAssistantChat(VISITOR, PUBLISHED);

    if (result.status !== 'ready') throw new Error('expected ready');
    expect(result.data.privacyNotice).toContain('關閉這個分頁');
    expect(result.data.privacyNotice).not.toContain('你的帳號');
  });

  it('refuses an internal-only assistant with the same answer as an unknown one', () => {
    const repository = createRepository();

    const internal = repository.getAssistantChat(VISITOR, INTERNAL_ONLY);
    const unknown = repository.getAssistantChat(VISITOR, 'assistant-does-not-exist');

    expect(internal.status).toBe('permission-denied');
    expect(internal).toEqual(unknown);
    if (internal.status !== 'permission-denied') return;
    expect(internal.message).not.toContain('內部教育訓練助理');
    expect(internal.reason).toBe('assistant-use');
  });

  it('closes the anonymous door when the external channel is paused', () => {
    const repository = createRepository();
    repository.setPublishingChannelPaused('account-smb-admin', PUBLISHED, 'website', true);

    expect(repository.getAssistantChat(VISITOR, PUBLISHED).status).toBe('permission-denied');
  });

  it('closes the anonymous door while the website channel needs attention', () => {
    const repository = createRepository();
    repository.setScenario('disconnected-channel');

    expect(repository.getAssistantChat(VISITOR, PUBLISHED).status).toBe('permission-denied');
  });

  it('keeps the visitor conversation out of every demo account and every other visitor', () => {
    const repository = createRepository();
    ask(repository, VISITOR, '我的訪客問題：退貨要幾天？');

    const accounts: readonly AccountId[] = [
      'account-smb-admin',
      'account-internal-employee',
      'account-external-customer',
    ];
    for (const account of accounts) {
      const view = repository.getAssistantChat(account, PUBLISHED);
      if (view.status !== 'ready') throw new Error(`expected ready for ${account}`);
      expect(view.data.messages).toEqual([]);
      const threads = repository.listChatThreads(account, PUBLISHED);
      if (threads.status !== 'ready') throw new Error(`expected ready threads for ${account}`);
      expect(threads.data.threads).toEqual([]);
    }

    const other = repository.getAssistantChat(OTHER_VISITOR, PUBLISHED);
    if (other.status !== 'ready') throw new Error('expected ready');
    expect(other.data.messages).toEqual([]);
  });

  it('stores the visitor conversation only in the per-tab storage', () => {
    const shared = createMemoryStorage();
    const visitorStorage = createMemoryStorage();
    const repository = createRepository(shared, visitorStorage);

    ask(repository, VISITOR, '收到商品後幾天內可以退貨？');

    expect(visitorStorage.getItem(`sme-demo:chat:${VISITOR}:${PUBLISHED}`)).not.toBeNull();
    expect(shared.getItem(`sme-demo:chat:${VISITOR}:${PUBLISHED}`)).toBeNull();
  });

  it('leaves the owner’s anonymous usage count untouched by visitor conversations', () => {
    const repository = createRepository();
    ask(repository, VISITOR, '收到商品後幾天內可以退貨？');

    const analytics = repository.getAssistantAnalytics('account-smb-admin', PUBLISHED);

    if (analytics.status !== 'ready') throw new Error('expected ready');
    expect(analytics.data.conversationCount).toBe(18);
  });

  it('answers a visitor from the same fixtures as a signed-in account', () => {
    const chat = ask(createRepository(), VISITOR, '收到商品後幾天內可以退貨？');

    const last = chat.messages[chat.messages.length - 1];
    if (last?.author !== 'assistant') throw new Error('expected an assistant reply');
    expect(last.reply.kind).toBe('company-data');
  });

  it('still discloses recipient, purpose, viewers and the sensitive-data notice to a visitor', () => {
    const chat = ask(createRepository(), VISITOR, '我要回報訂單問題');

    const last = chat.messages[chat.messages.length - 1];
    if (last?.author !== 'assistant' || last.reply.kind !== 'form-request') {
      throw new Error('expected a form request');
    }
    const consent = last.reply.form.consent;
    expect(consent.recipient).toContain('安心商行管理者');
    expect(consent.purpose).not.toBe('');
    expect(consent.viewers).toContain('安心商行管理者');
    expect(consent.sensitiveNotice).toContain('敏感');
    expect(consent.withdrawalNotice).toContain('撤回');
  });

  it('refuses to save an anonymous submission without consent', () => {
    const repository = createRepository();
    ask(repository, VISITOR, '我要回報訂單問題');

    const result = repository.submitChatForm(VISITOR, PUBLISHED, {
      formId: 'database-orders',
      answers: ORDER_ANSWERS,
      consent: false,
    });

    expect(result.status).toBe('validation-failed');
    const tracking = repository.getDatabaseTracking('account-smb-admin', 'database-orders');
    if (tracking.status !== 'ready') throw new Error('expected ready');
    expect(JSON.stringify(tracking.data)).not.toContain('DEMO-9001');
  });

  it('delivers a consented anonymous submission to the data manager without inventing an identity', () => {
    const repository = createRepository();
    ask(repository, VISITOR, '我要回報訂單問題');
    chatOf(
      repository.submitChatForm(VISITOR, PUBLISHED, {
        formId: 'database-orders',
        answers: ORDER_ANSWERS,
        consent: true,
      }),
    );

    const tracking = repository.getDatabaseTracking('account-smb-admin', 'database-orders');

    if (tracking.status !== 'ready') throw new Error('expected ready');
    const subject = tracking.data.subjects.find((candidate) =>
      candidate.displayName.includes('未登入訪客'),
    );
    expect(subject).toBeDefined();
    expect(JSON.stringify(subject)).toContain('DEMO-9001');
    // 匿名訪客不會被冒認成任何 Demo 帳號。
    expect(subject?.displayName).not.toContain('外部客戶');
    expect(subject?.displayName).not.toContain('已停用的帳號');
  });

  it('keeps the anonymous submission away from an account that is not the data manager', () => {
    const repository = createRepository();
    ask(repository, VISITOR, '我要回報訂單問題');
    repository.submitChatForm(VISITOR, PUBLISHED, {
      formId: 'database-orders',
      answers: ORDER_ANSWERS,
      consent: true,
    });

    const tracking = repository.getDatabaseTracking('account-internal-employee', 'database-orders');

    expect(tracking.status).toBe('permission-denied');
  });

  it('never lets a visitor reach another visitor’s conversation through a thread id', () => {
    const repository = createRepository();
    const chat = ask(repository, VISITOR, '我的訪客問題：退貨要幾天？');
    const threadId = chat.threadId;
    expect(threadId).not.toBeNull();

    const stolen = repository.getAssistantChat(OTHER_VISITOR, PUBLISHED, threadId ?? '');

    expect(stolen.status).toBe('permission-denied');
    if (stolen.status !== 'permission-denied') return;
    expect(stolen.reason).toBe('chat-thread');
  });
});
