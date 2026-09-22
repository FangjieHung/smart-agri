import type { AccountId } from '../domain/account.model';
import type { AssistantChatView, ChatMessageView, ChatReplyView } from '../domain/conversation.model';
import type { SendChatMessageResult, SubmitChatFormResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';
const ORDER_ANSWERS = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

function createRepository(storage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function chatOf(result: SendChatMessageResult | SubmitChatFormResult): AssistantChatView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function lastReply(chat: AssistantChatView): ChatReplyView {
  const last = chat.messages[chat.messages.length - 1] as ChatMessageView | undefined;
  if (last?.author !== 'assistant') throw new Error('expected an assistant reply');
  return last.reply;
}

function ask(repository: MockDemoRepository, text: string, account: AccountId = 'account-external-customer') {
  return lastReply(chatOf(repository.sendChatMessage(account, ASSISTANT, text)));
}

describe('MockDemoRepository assistant chat', () => {
  it('opens an empty private conversation with a welcome, privacy notice and fixture prompts', () => {
    const chat = chatOf(createRepository().getAssistantChat('account-external-customer', ASSISTANT));

    expect(chat.assistantName).toBe('客服助理');
    expect(chat.messages).toEqual([]);
    expect(chat.welcome).not.toBe('');
    expect(chat.privacyNotice).toContain('助理建立者');
    expect(chat.suggestedPrompts.map((prompt) => prompt.id)).toEqual([
      'chat-refund-window',
      'chat-leather-wash',
      'chat-leather-care',
      'chat-order-issue',
    ]);
    expect(Object.isFrozen(chat)).toBe(true);
  });

  it('denies an unknown or unusable assistant with the same message', () => {
    const repository = createRepository();
    const unknown = repository.getAssistantChat('account-external-customer', 'assistant-does-not-exist');
    const membersOnly = repository.getAssistantChat('account-external-customer', 'assistant-internal-onboarding');

    expect(unknown).toMatchObject({ status: 'permission-denied', reason: 'assistant-use' });
    expect(membersOnly).toEqual(unknown);
    expect(JSON.stringify(membersOnly)).not.toContain('內部教育訓練助理');
  });

  it('answers from company data with expandable citations from connected knowledge bases', () => {
    const reply = ask(createRepository(), '收到商品後幾天內可以退貨？');

    expect(reply.kind).toBe('company-data');
    if (reply.kind !== 'company-data') return;
    expect(reply.text).toContain('7 天');
    expect(reply.citations.length).toBeGreaterThan(0);
    expect(reply.citations[0]).toMatchObject({
      knowledgeBaseName: '退換貨政策',
      documentName: '退換貨辦法 2026 版.pdf',
    });
    expect(reply.citations[0].excerpt).not.toBe('');
  });

  it('matches new questions by keywords from the predefined response map', () => {
    expect(ask(createRepository(), '請問皮革包包能不能用水清洗')).toMatchObject({ kind: 'company-data' });
  });

  it('labels general knowledge separately from company data', () => {
    const reply = ask(createRepository(), '皮革商品平常要怎麼保養？');

    expect(reply.kind).toBe('general-knowledge');
    if (reply.kind !== 'general-knowledge') return;
    expect(reply.notice).toContain('不是公司資料');
    expect(JSON.stringify(reply)).not.toContain('citations');
  });

  it('refuses unknown questions with next steps instead of simulating an answer', () => {
    const reply = ask(createRepository(), '可以幫我訂下週的機票嗎？');

    expect(reply.kind).toBe('no-result');
    if (reply.kind !== 'no-result') return;
    expect(reply.text).toContain('查無資料');
    expect(reply.nextSteps.length).toBeGreaterThan(0);
  });

  it('rejects an empty question without storing it', () => {
    const repository = createRepository();

    expect(repository.sendChatMessage('account-external-customer', ASSISTANT, '   ')).toMatchObject({
      status: 'validation-failed',
    });
    expect(chatOf(repository.getAssistantChat('account-external-customer', ASSISTANT)).messages).toEqual([]);
  });

  it('keeps each account’s conversation private, including from the assistant owner', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);
    repository.sendChatMessage('account-external-customer', ASSISTANT, '我的私人問題：退貨要幾天？');

    const customer = chatOf(createRepository(storage).getAssistantChat('account-external-customer', ASSISTANT));
    const employee = chatOf(repository.getAssistantChat('account-internal-employee', ASSISTANT));
    const owner = chatOf(repository.getAssistantChat('account-smb-admin', ASSISTANT));
    const analytics = repository.getAssistantAnalytics('account-smb-admin', ASSISTANT);

    expect(customer.messages).toHaveLength(2);
    expect(customer.messages[0]).toMatchObject({ author: 'account', text: '我的私人問題：退貨要幾天？' });
    expect(employee.messages).toEqual([]);
    expect(owner.messages).toEqual([]);
    expect(analytics).toMatchObject({ status: 'ready', data: { conversationCount: 19 } });
    expect(JSON.stringify(analytics)).not.toContain('我的私人問題');
  });

  it('offers an inline form with recipient, purpose, viewers and a sensitive-data hint', () => {
    const reply = ask(createRepository(), '我要回報訂單問題');

    expect(reply.kind).toBe('form-request');
    if (reply.kind !== 'form-request') return;
    expect(reply.form.id).toBe('database-orders');
    expect(reply.form.fields.map((field) => field.label)).toEqual(['訂單編號', '問題類型', '回報日期']);
    expect(reply.form.consent).toMatchObject({
      recipient: expect.stringContaining('安心商行'),
      purpose: '收集訂單問題回報，讓客服追蹤處理進度。',
      viewers: ['安心商行管理者'],
    });
    expect(reply.form.consent.sensitiveNotice).toContain('敏感');
    expect(reply.form.consent.withdrawalNotice).toContain('撤回');
  });

  it('reviews the form without creating a record and reports field errors', () => {
    const repository = createRepository();

    expect(
      repository.reviewChatForm('account-external-customer', ASSISTANT, 'database-orders', {}),
    ).toMatchObject({
      status: 'validation-failed',
      errors: expect.arrayContaining([{ fieldId: 'field-order-number', message: '「訂單編號」為必填。' }]),
    });
    expect(
      repository.reviewChatForm('account-external-customer', ASSISTANT, 'database-orders', ORDER_ANSWERS),
    ).toMatchObject({
      status: 'ready',
      data: { saved: false, entries: expect.arrayContaining([expect.objectContaining({ display: 'DEMO-2001' })]) },
    });
    expect(repository.listDatabaseSummaries('account-smb-admin')).toMatchObject({
      data: [{ id: 'database-orders', recordCount: 0 }, expect.anything()],
    });
  });

  it('cannot submit without consent', () => {
    const repository = createRepository();

    expect(
      repository.submitChatForm('account-external-customer', ASSISTANT, {
        formId: 'database-orders',
        answers: ORDER_ANSWERS,
        consent: false,
      }),
    ).toMatchObject({ status: 'validation-failed', errors: [{ fieldId: null }] });
    expect(repository.getDatabaseTracking('account-smb-admin', 'database-orders')).toMatchObject({
      status: 'ready',
      data: { subjects: [] },
    });
  });

  it('shows a consented submission only to the designated data manager', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);

    const submitted = repository.submitChatForm('account-external-customer', ASSISTANT, {
      formId: 'database-orders',
      answers: ORDER_ANSWERS,
      consent: true,
    });
    const receipt = lastReply(chatOf(submitted));
    expect(receipt).toMatchObject({ kind: 'submission-receipt', recipient: expect.stringContaining('安心商行') });

    const manager = createRepository(storage).getDatabaseTracking('account-smb-admin', 'database-orders');
    expect(manager.status).toBe('ready');
    if (manager.status !== 'ready') return;
    expect(manager.data.subjects).toHaveLength(1);
    expect(manager.data.subjects[0].displayName).toBe('外部客戶');
    expect(manager.data.subjects[0].records[0]).toMatchObject({
      source: 'assistant-conversation',
      entries: expect.arrayContaining([{ fieldId: 'field-order-number', label: '訂單編號', display: 'DEMO-2001' }]),
    });

    expect(repository.getDatabaseTracking('account-internal-employee', 'database-orders').status).toBe(
      'permission-denied',
    );
    expect(repository.getDatabaseTracking('account-external-customer', 'database-orders').status).toBe(
      'permission-denied',
    );
    expect(JSON.stringify(repository.getAssistantChat('account-smb-admin', ASSISTANT))).not.toContain('DEMO-2001');
  });

  it('does not offer the form when the assistant is not connected to the database', () => {
    const repository = new MockDemoRepository({
      ...DEMO_SEED,
      assistants: DEMO_SEED.assistants.map((assistant) => ({ ...assistant, databaseIds: [] })),
    });

    expect(ask(repository, '我要回報訂單問题')).toMatchObject({ kind: 'no-result' });
    expect(
      repository.submitChatForm('account-external-customer', ASSISTANT, {
        formId: 'database-orders',
        answers: ORDER_ANSWERS,
        consent: true,
      }),
    ).toMatchObject({ status: 'permission-denied' });
  });
});
