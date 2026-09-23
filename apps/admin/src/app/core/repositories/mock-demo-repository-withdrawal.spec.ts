import type { ChatViewerId } from '../domain/account.model';
import type { AssistantChatView, ChatReplyView } from '../domain/conversation.model';
import type { DatabaseRecordId, TrackedSubjectView } from '../domain/database.model';
import type { SubmitChatFormResult, WithdrawChatSubmissionResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';
const CUSTOMER = 'account-external-customer';
const MANAGER = 'account-smb-admin';
const VISITOR = 'visitor-e2e-withdrawal';
const FORM = 'database-orders';

function answers(orderNumber: string) {
  return {
    'field-order-number': orderNumber,
    'field-issue-type': '配送延遲',
    'field-reported-on': '2026-09-21',
  };
}

function createRepository(storage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    visitorStorage: createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function chatOf(result: SubmitChatFormResult | WithdrawChatSubmissionResult): AssistantChatView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function receipts(chat: AssistantChatView): readonly Extract<ChatReplyView, { kind: 'submission-receipt' }>[] {
  return chat.messages.flatMap((message) =>
    message.author === 'assistant' && message.reply.kind === 'submission-receipt' ? [message.reply] : [],
  );
}

function lastReceipt(chat: AssistantChatView) {
  const all = receipts(chat);
  const last = all[all.length - 1];
  if (last === undefined) throw new Error('expected a submission receipt');
  return last;
}

/** 送出一筆已同意的表單，回傳收據上的紀錄 id。 */
function submit(
  repository: MockDemoRepository,
  viewerId: ChatViewerId,
  orderNumber: string,
): DatabaseRecordId {
  const chat = chatOf(
    repository.submitChatForm(viewerId, ASSISTANT, {
      formId: FORM,
      answers: answers(orderNumber),
      consent: true,
    }),
  );
  const recordId = lastReceipt(chat).recordId;
  if (recordId === null) throw new Error('expected the receipt to carry a record id');
  return recordId;
}

function subjectOf(repository: MockDemoRepository, subjectId: string): TrackedSubjectView | undefined {
  const tracking = repository.getDatabaseTracking(MANAGER, FORM);
  if (tracking.status !== 'ready') throw new Error(`expected ready, got ${tracking.status}`);
  return tracking.data.subjects.find((subject) => subject.id === subjectId);
}

function messageOf(result: WithdrawChatSubmissionResult): string {
  if (result.status === 'validation-failed' || result.status === 'permission-denied') return result.message;
  throw new Error(`expected a refusal, got ${result.status}`);
}

describe('MockDemoRepository consent withdrawal', () => {
  it('offers withdrawal on the receipt and explains what a withdrawal leaves behind', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');
    const receipt = lastReceipt(chatOf(repository.getAssistantChat(CUSTOMER, ASSISTANT)));

    expect(receipt.recordId).toBe(recordId);
    expect(receipt.withdrawal.status).toBe('available');
    expect(receipt.withdrawal.notice).toContain('撤回');
    expect(receipt.withdrawal.withdrawnDateLabel).toBe('');
  });

  it('lets the submitter withdraw and marks the receipt as withdrawn', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');

    const result = repository.withdrawChatSubmission(CUSTOMER, ASSISTANT, recordId);
    expect(result.status).toBe('ready');

    const receipt = lastReceipt(chatOf(repository.getAssistantChat(CUSTOMER, ASSISTANT)));
    expect(receipt.withdrawal.status).toBe('withdrawn');
    expect(receipt.withdrawal.withdrawnDateLabel).toBe('2026-09-22');
  });

  it('removes the withdrawn content from the collection records and keeps only a trace', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');
    const before = subjectOf(repository, `subject-${CUSTOMER}`);

    expect(before?.records).toHaveLength(1);
    expect(JSON.stringify(before)).toContain('DEMO-9001');

    repository.withdrawChatSubmission(CUSTOMER, ASSISTANT, recordId);
    const after = subjectOf(repository, `subject-${CUSTOMER}`);

    expect(after?.records).toEqual([]);
    expect(after?.withdrawals).toHaveLength(1);
    expect(after?.withdrawals[0].id).toBe(recordId);
    expect(after?.withdrawals[0].withdrawnDateLabel).toBe('2026-09-22');
    // 軌跡只有時間與來源，沒有任何填寫內容。
    expect(JSON.stringify(after)).not.toContain('DEMO-9001');
  });

  it('recomputes the trend without the withdrawn record and falls back below two records', () => {
    const repository = createRepository();
    const first = submit(repository, CUSTOMER, 'DEMO-9001');
    submit(repository, CUSTOMER, 'DEMO-9002');

    expect(subjectOf(repository, `subject-${CUSTOMER}`)?.comparison).toMatchObject({
      status: 'available',
      recordCount: 2,
    });

    repository.withdrawChatSubmission(CUSTOMER, ASSISTANT, first);

    expect(subjectOf(repository, `subject-${CUSTOMER}`)?.comparison).toMatchObject({
      status: 'insufficient-records',
      recordCount: 1,
    });
  });

  it('lets an anonymous visitor withdraw a record submitted in the same tab session', () => {
    const repository = createRepository();
    const recordId = submit(repository, VISITOR, 'DEMO-9003');

    expect(lastReceipt(chatOf(repository.getAssistantChat(VISITOR, ASSISTANT))).withdrawal.notice).toContain(
      '分頁',
    );
    expect(repository.withdrawChatSubmission(VISITOR, ASSISTANT, recordId).status).toBe('ready');

    const subject = subjectOf(repository, `subject-${VISITOR}`);
    expect(subject?.records).toEqual([]);
    expect(subject?.withdrawals).toHaveLength(1);
  });

  it('refuses the data manager withdrawing on someone else’s behalf without leaking anything', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');

    const denied = repository.withdrawChatSubmission(MANAGER, ASSISTANT, recordId);
    const unknown = repository.withdrawChatSubmission(MANAGER, ASSISTANT, 'record-chat-999');

    expect(denied).toMatchObject({ status: 'permission-denied', reason: 'submission-withdrawal' });
    expect(messageOf(denied)).toBe(messageOf(unknown));
    expect(messageOf(denied)).not.toContain('DEMO-9001');
    // 拒絕之後紀錄仍然完整，資料管理者沒有代為刪除的路徑。
    expect(subjectOf(repository, `subject-${CUSTOMER}`)?.records).toHaveLength(1);
  });

  it('refuses another submitter withdrawing a record that is not theirs', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');

    expect(repository.withdrawChatSubmission(VISITOR, ASSISTANT, recordId)).toMatchObject({
      status: 'permission-denied',
      reason: 'submission-withdrawal',
    });
  });

  it('refuses to withdraw the same record twice', () => {
    const repository = createRepository();
    const recordId = submit(repository, CUSTOMER, 'DEMO-9001');

    expect(repository.withdrawChatSubmission(CUSTOMER, ASSISTANT, recordId).status).toBe('ready');
    const again = repository.withdrawChatSubmission(CUSTOMER, ASSISTANT, recordId);

    expect(again).toMatchObject({ status: 'validation-failed' });
    expect(messageOf(again)).toContain('已經撤回');
    expect(subjectOf(repository, `subject-${CUSTOMER}`)?.withdrawals).toHaveLength(1);
  });

  it('says so instead of pretending when an older receipt cannot identify its record', () => {
    const storage = createMemoryStorage();
    // 這個版本的收據還沒有 recordId：無法指認紀錄，就不提供撤回入口。
    storage.setItem(
      `sme-demo:chat:${CUSTOMER}:${ASSISTANT}`,
      JSON.stringify({
        version: 2,
        threads: [
          {
            id: 'chat-thread-1',
            title: '回報訂單問題',
            titleSource: 'derived',
            createdAt: '2026-09-21T02:00:00.000Z',
            updatedAt: '2026-09-21T02:00:00.000Z',
            messages: [
              {
                id: 'chat-message-1',
                author: 'assistant',
                createdAt: '2026-09-21T02:00:00.000Z',
                reply: { kind: 'submission-receipt', text: '已送出。', recipient: '安心商行', entries: [] },
              },
            ],
          },
        ],
      }),
    );
    const repository = createRepository(storage);
    const receipt = lastReceipt(chatOf(repository.getAssistantChat(CUSTOMER, ASSISTANT)));

    expect(receipt.recordId).toBeNull();
    expect(receipt.withdrawal.status).toBe('unavailable');
    expect(receipt.withdrawal.notice).not.toBe('');
  });
});
