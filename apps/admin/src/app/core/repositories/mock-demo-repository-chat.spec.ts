import { firstValueFrom } from 'rxjs';
import { syncValue } from './sync-value.testing';
import type { AccountId } from '../domain/account.model';
import type { AssistantChatView, ChatMessageView, ChatReplyView } from '../domain/conversation.model';
import type { RepositoryView, SendChatMessageResult, SubmitChatFormResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';
const ORDER_ANSWERS = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

/** `getAssistantChat` 的非同步契約不再接收 viewer；用這個 box 讓測試切換目前帳號。 */
const viewerBoxes = new WeakMap<MockDemoRepository, { current: AccountId | null }>();

function createRepository(storage = createMemoryStorage()) {
  const box = { current: 'account-external-customer' as AccountId | null };
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => box.current,
  });
  viewerBoxes.set(repository, box);
  return repository;
}

/** 以指定帳號讀取這個助理的對話（非同步契約，issue #79）。 */
async function chatAs(
  repository: MockDemoRepository,
  account: AccountId,
  assistantId: string,
): Promise<RepositoryView<AssistantChatView>> {
  const box = viewerBoxes.get(repository);
  if (box === undefined) throw new Error('unknown repository: use createRepository()');
  box.current = account;
  return firstValueFrom(repository.getAssistantChat(assistantId));
}

function chatOf(result: SendChatMessageResult): AssistantChatView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

/** 送出成功時回傳的收據訊息（issue #148 起不再回整段對話）。 */
function receiptOf(result: SubmitChatFormResult): ChatReplyView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  const message = result.data.message;
  if (message.author !== 'assistant') throw new Error('expected an assistant reply');
  return message.reply;
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
  it('opens an empty private conversation with a welcome, privacy notice and fixture prompts', async () => {
    const repository = createRepository();
    const chat = chatOf(await chatAs(repository, 'account-external-customer', ASSISTANT));

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

  it('denies an unknown or unusable assistant with the same message', async () => {
    const repository = createRepository();
    const unknown = await chatAs(repository, 'account-external-customer', 'assistant-does-not-exist');
    const membersOnly = await chatAs(repository, 'account-external-customer', 'assistant-internal-onboarding');

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
    expect(reply.notice).toContain('不是組織資料');
    expect(JSON.stringify(reply)).not.toContain('citations');
  });

  it('refuses unknown questions with next steps instead of simulating an answer', () => {
    const reply = ask(createRepository(), '可以幫我訂下週的機票嗎？');

    expect(reply.kind).toBe('no-result');
    if (reply.kind !== 'no-result') return;
    expect(reply.text).toContain('查無資料');
    expect(reply.nextSteps.length).toBeGreaterThan(0);
  });

  it('rejects an empty question without storing it', async () => {
    const repository = createRepository();

    expect(repository.sendChatMessage('account-external-customer', ASSISTANT, '   ')).toMatchObject({
      status: 'validation-failed',
    });
    expect(chatOf(await chatAs(repository, 'account-external-customer', ASSISTANT)).messages).toEqual([]);
  });

  it('keeps each account’s conversation private, including from the assistant owner', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);
    repository.sendChatMessage('account-external-customer', ASSISTANT, '我的私人問題：退貨要幾天？');

    const other = createRepository(storage);
    const customer = chatOf(await chatAs(other, 'account-external-customer', ASSISTANT));
    const employee = chatOf(await chatAs(repository, 'account-internal-employee', ASSISTANT));
    const owner = chatOf(await chatAs(repository, 'account-smb-admin', ASSISTANT));
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
    if (reply.kind !== 'form-request' || reply.form === null) throw new Error('expected a form');
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

  it('reviews the form without creating a record and reports field errors', async () => {
    const repository = createRepository();

    expect(
      syncValue(repository.reviewChatForm('account-external-customer', ASSISTANT, 'database-orders', 1, {})),
    ).toMatchObject({
      status: 'validation-failed',
      errors: expect.arrayContaining([{ fieldId: 'field-order-number', message: '「訂單編號」為必填。' }]),
    });
    expect(
      syncValue(repository.reviewChatForm('account-external-customer', ASSISTANT, 'database-orders', 1, ORDER_ANSWERS)),
    ).toMatchObject({
      status: 'ready',
      data: { saved: false, entries: expect.arrayContaining([expect.objectContaining({ display: 'DEMO-2001' })]) },
    });
    const box = viewerBoxes.get(repository);
    if (box === undefined) throw new Error('unknown repository: use createRepository()');
    box.current = 'account-smb-admin';
    expect(await firstValueFrom(repository.listDatabaseSummaries())).toMatchObject({
      data: [{ id: 'database-orders', recordCount: 0 }, expect.anything()],
    });
  });

  it('cannot submit without consent', () => {
    const repository = createRepository();

    expect(
      syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, {
        formId: 'database-orders', formVersion: 1, submissionId: crypto.randomUUID(),
        answers: ORDER_ANSWERS,
        consent: false,
      })),
    ).toMatchObject({ status: 'validation-failed', errors: [{ fieldId: null }] });
    expect(repository.getDatabaseTracking('account-smb-admin', 'database-orders')).toMatchObject({
      status: 'ready',
      data: { subjects: [] },
    });
  });

  it('shows a consented submission only to the designated data manager', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);

    const submitted = syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, {
      formId: 'database-orders', formVersion: 1, submissionId: crypto.randomUUID(),
      answers: ORDER_ANSWERS,
      consent: true,
    }));
    const receipt = receiptOf(submitted);
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
    expect(JSON.stringify(await chatAs(repository, 'account-smb-admin', ASSISTANT))).not.toContain('DEMO-2001');
  });

  it('answers a retry with the same submission id with the same receipt and one record (#148)', () => {
    const repository = createRepository();
    const submission = {
      formId: 'database-orders', formVersion: 1, submissionId: 'retry-key', answers: ORDER_ANSWERS, consent: true,
    };

    const first = receiptOf(syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, submission)));
    const again = receiptOf(syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, submission)));

    expect(again).toEqual(first);
    const tracking = repository.getDatabaseTracking('account-smb-admin', 'database-orders');
    if (tracking.status !== 'ready') throw new Error('expected tracking');
    expect(tracking.data.subjects[0].records).toHaveLength(1);
  });

  it('refuses a stale form version with form-version-changed and records nothing (#148)', () => {
    const repository = createRepository();

    expect(syncValue(repository.reviewChatForm('account-external-customer', ASSISTANT, 'database-orders', 2, ORDER_ANSWERS)))
      .toMatchObject({ status: 'conflict', reason: 'form-version-changed' });
    expect(syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, {
      formId: 'database-orders', formVersion: 2, submissionId: 'stale', answers: ORDER_ANSWERS, consent: true,
    }))).toMatchObject({ status: 'conflict', reason: 'form-version-changed' });
    expect(repository.getDatabaseTracking('account-smb-admin', 'database-orders')).toMatchObject({
      status: 'ready',
      data: { subjects: [] },
    });
  });

  it('does not offer the form when the assistant is not connected to the database', () => {
    const repository = new MockDemoRepository({
      ...DEMO_SEED,
      assistants: DEMO_SEED.assistants.map((assistant) => ({ ...assistant, databaseIds: [] })),
    });

    expect(ask(repository, '我要回報訂單問题')).toMatchObject({ kind: 'no-result' });
    expect(
      syncValue(repository.submitChatForm('account-external-customer', ASSISTANT, {
        formId: 'database-orders', formVersion: 1, submissionId: crypto.randomUUID(),
        answers: ORDER_ANSWERS,
        consent: true,
      })),
    ).toMatchObject({ status: 'permission-denied', reason: 'assistant-form' });
  });

  it('answers 查無資料 with the assistant’s own saved refusal message, not a fixed string', async () => {
    const repository = createRepository();
    const box = viewerBoxes.get(repository);
    if (box === undefined) throw new Error('unknown repository: use createRepository()');
    box.current = 'account-smb-admin';
    const settings = await firstValueFrom(repository.getAssistantSettings(ASSISTANT));
    if (settings.status !== 'ready') throw new Error('expected settings');

    // 未編輯過的助理：規則裡顯示的就是對話真的會說的那一句。
    expect(ask(repository, '可以幫我訂下週的機票嗎？').kind).toBe('no-result');
    expect(settings.data.rules.refusalMessage).toContain('查無資料');

    await firstValueFrom(
      repository.updateAssistantSettings(ASSISTANT, {
        rules: { ...settings.data.rules, refusalMessage: '這題我查不到，請打 02-1234-5678。' },
      }),
    );
    const reply = ask(repository, '可以幫我訂下週的機票嗎？');

    expect(reply.kind).toBe('no-result');
    if (reply.kind !== 'no-result') return;
    expect(reply.text).toBe('這題我查不到，請打 02-1234-5678。');
    // 下一步仍由 Demo 提供，規則改的是拒答文案本身。
    expect(reply.nextSteps.length).toBeGreaterThan(0);
  });

  it('leaves already saved refusals alone when the rule changes afterwards', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);
    ask(repository, '可以幫我訂下週的機票嗎？');
    const box = viewerBoxes.get(repository);
    if (box === undefined) throw new Error('unknown repository: use createRepository()');
    box.current = 'account-smb-admin';
    const settings = await firstValueFrom(repository.getAssistantSettings(ASSISTANT));
    if (settings.status !== 'ready') throw new Error('expected settings');
    await firstValueFrom(
      repository.updateAssistantSettings(ASSISTANT, {
        rules: { ...settings.data.rules, refusalMessage: '改過的拒答文案。' },
      }),
    );

    const reloaded = createRepository(storage);
    const chat = chatOf(await chatAs(reloaded, 'account-external-customer', ASSISTANT));
    expect(JSON.stringify(chat.messages)).not.toContain('改過的拒答文案。');
  });
});
