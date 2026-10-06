import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { ChatMessageView, ChatReplyView } from '../domain/conversation.model';
import type { SendChatMessageResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { mockChatProposedCases, resetMockChatProposedCasesForTest } from './mock-chat-cases';
import { MockDemoRepository } from './mock-demo-repository';

/*
 * 純 Demo 模式的助理提議開案（issue #254）：與後端相同的優先順序（數據庫查詢 → 表單 → 案件）、只對內部帳號
 * 提議、預設不提議、確認只能一次，確認後的案件只有確認過的標題與說明並連結對話串。
 */
const ASSISTANT = 'assistant-customer-service';
const REPAIR = 'case-type-equipment-repair';
const QUESTION = '二號冷藏庫溫度降不下來，需要報修';

function createRepository() {
  const box = { current: 'account-smb-admin' as AccountId };
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-10-07T02:00:00.000Z'),
    viewer: () => box.current,
  });
  return { repository, as: (account: AccountId) => (box.current = account) };
}

function lastReply(result: SendChatMessageResult): { message: ChatMessageView; reply: ChatReplyView; threadId: string | null } {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  const message = result.data.messages[result.data.messages.length - 1];
  if (message?.author !== 'assistant') throw new Error('expected an assistant reply');
  return { message, reply: message.reply, threadId: result.data.threadId };
}

async function addRepair(repository: MockDemoRepository) {
  const result = await firstValueFrom(repository.setAssistantCaseType(ASSISTANT, REPAIR, true));
  if (result.status !== 'ready') throw new Error(result.status);
  return result.data;
}

afterEach(() => resetMockChatProposedCasesForTest());

describe('MockDemoRepository case proposals (issue #254)', () => {
  it('proposes nothing until the owner adds a type, and only active types can be added', async () => {
    const { repository, as } = createRepository();
    as('account-internal-employee');
    expect(lastReply(repository.sendChatMessage('account-internal-employee', ASSISTANT, QUESTION)).reply.kind).not.toBe('case-proposal');

    as('account-smb-admin');
    const refused = await firstValueFrom(repository.setAssistantCaseType(ASSISTANT, 'case-type-stocktake', true));
    expect(refused).toMatchObject({ status: 'validation-failed', errors: [{ field: 'caseTypeIds' }] });
    expect((await addRepair(repository)).caseTypeIds).toEqual([REPAIR]);
    expect((await addRepair(repository)).caseTypeIds).toEqual([REPAIR]);

    as('account-internal-employee');
    const { reply } = lastReply(repository.sendChatMessage('account-internal-employee', ASSISTANT, QUESTION));
    expect(reply).toEqual({
      kind: 'case-proposal',
      text: '這件事可以開一件「設備故障報修」案件，請確認內容。',
      proposal: {
        typeId: REPAIR, typeName: '設備故障報修', title: QUESTION, description: '', status: 'proposed', available: true,
        group: { id: 'case-group-equipment', name: '設備組', archived: false }, dueHours: 72, caseId: null,
      },
    });
  });

  it('keeps the precedence: a database query, then the form, then the case; never for an external customer', async () => {
    const { repository, as } = createRepository();
    await addRepair(repository);

    expect(lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, '近 30 天有幾筆訂單問題回報？')).reply.kind).toBe('database-query');
    expect(lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, '訂單出貨延遲，請安排處理')).reply.kind).toBe('form-request');
    expect(lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, QUESTION)).reply.kind).toBe('case-proposal');

    as('account-external-customer');
    expect(lastReply(repository.sendChatMessage('account-external-customer', ASSISTANT, QUESTION)).reply.kind).not.toBe('case-proposal');
  });

  it('confirms once with the edited title and description, linked to the thread; a second time is a conflict', async () => {
    const { repository } = createRepository();
    await addRepair(repository);
    const { message, threadId } = lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, QUESTION));

    const invalid = await firstValueFrom(repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT, message.id, { title: ' ', description: '' }));
    expect(invalid).toMatchObject({ status: 'validation-failed', reason: null, fieldErrors: { title: '請輸入案件標題。' } });

    const confirmed = await firstValueFrom(repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT, message.id, {
      title: ' 二號冷藏庫溫度異常 ', description: '請派人檢查。',
    }));
    if (confirmed.status !== 'ready' || confirmed.data.author !== 'assistant' || confirmed.data.reply.kind !== 'case-proposal') {
      throw new Error('expected a confirmed proposal');
    }
    const proposal = confirmed.data.reply.proposal;
    expect(proposal).toMatchObject({ status: 'confirmed', available: false, title: '二號冷藏庫溫度異常', description: '請派人檢查。' });
    expect(mockChatProposedCases()).toEqual([expect.objectContaining({
      id: proposal?.caseId, title: '二號冷藏庫溫度異常', description: '請派人檢查。', createdBy: 'account-smb-admin',
      assistantId: ASSISTANT, threadId, dueAt: '2026-10-10T02:00:00.000Z',
    })]);

    // Read back as confirmed; a second confirmation or a dismissal is a conflict and creates nothing.
    const chat = await firstValueFrom(repository.getAssistantChat(ASSISTANT, threadId ?? undefined));
    if (chat.status !== 'ready') throw new Error(chat.status);
    expect(chat.data.messages.at(-1)).toEqual(confirmed.data);
    expect(await firstValueFrom(repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT, message.id, { title: '再一次', description: '' })))
      .toEqual({ status: 'conflict', message: '這個提議已經處理過了：已建立案件，或已選擇不用了。' });
    expect(await firstValueFrom(repository.dismissChatCaseProposal('account-smb-admin', ASSISTANT, message.id))).toMatchObject({ status: 'conflict' });
    expect(mockChatProposedCases()).toHaveLength(1);
  });

  it('dismisses without a case, and a removed type reads as unavailable and cannot be confirmed', async () => {
    const { repository } = createRepository();
    await addRepair(repository);
    const first = lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, QUESTION));
    const second = lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, '溫室風扇停了，需要報修', first.threadId ?? undefined));

    const dismissed = await firstValueFrom(repository.dismissChatCaseProposal('account-smb-admin', ASSISTANT, first.message.id));
    expect(dismissed).toMatchObject({ status: 'ready', data: { reply: { proposal: { status: 'dismissed', available: false, caseId: null } } } });

    await firstValueFrom(repository.setAssistantCaseType(ASSISTANT, REPAIR, false));
    const chat = await firstValueFrom(repository.getAssistantChat(ASSISTANT, first.threadId ?? undefined));
    if (chat.status !== 'ready') throw new Error(chat.status);
    expect(chat.data.messages.find((message) => message.id === second.message.id)).toMatchObject({
      reply: { kind: 'case-proposal', proposal: { status: 'proposed', available: false } },
    });
    expect(await firstValueFrom(repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT, second.message.id, { title: '風扇', description: '' })))
      .toMatchObject({ status: 'validation-failed', reason: 'case-type-not-proposable' });
    expect(mockChatProposedCases()).toHaveLength(0);
  });

  it('refuses another account’s proposal and an external customer the same way the API does', async () => {
    const { repository } = createRepository();
    await addRepair(repository);
    const { message } = lastReply(repository.sendChatMessage('account-smb-admin', ASSISTANT, QUESTION));

    expect(await firstValueFrom(repository.confirmChatCaseProposal('account-internal-employee', ASSISTANT, message.id, { title: '冷藏庫', description: '' })))
      .toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
    expect(await firstValueFrom(repository.confirmChatCaseProposal('account-external-customer', ASSISTANT, message.id, { title: '冷藏庫', description: '' })))
      .toMatchObject({ status: 'permission-denied', reason: 'case' });
  });
});
