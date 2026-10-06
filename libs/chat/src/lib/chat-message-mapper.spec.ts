import { toChatMessage, type ChatMessageWire, type ChatReplyWire } from './chat-message-mapper';
import type { ChatReplyView } from './chat-view.model';

const CREATED_AT = '2026-10-06T01:00:00.000Z';

const reply = (overrides: Partial<ChatReplyWire>): ChatReplyWire => ({
  kind: 'company-data',
  text: '7 天內可以退貨 [1]。',
  citations: [],
  notice: null,
  nextSteps: [],
  ...overrides,
});

const assistant = (wire: ChatReplyWire): ChatMessageWire => ({ id: 'message-2', reply: wire, createdAt: CREATED_AT });

describe('toChatMessage', () => {
  it('maps a message without a reply to the account’s own question', () => {
    expect(toChatMessage({ id: 'message-1', text: '退貨要幾天？', reply: null, createdAt: CREATED_AT })).toEqual({
      id: 'message-1',
      author: 'account',
      text: '退貨要幾天？',
      createdAt: CREATED_AT,
    });
  });

  it('treats an omitted reply key like null (PR #103) and a missing text as empty', () => {
    expect(toChatMessage({ id: 'message-1', createdAt: CREATED_AT })).toEqual({
      id: 'message-1',
      author: 'account',
      text: '',
      createdAt: CREATED_AT,
    });
  });

  it('maps company-data with its citations and notice', () => {
    const message = toChatMessage(
      assistant(
        reply({
          citations: [
            { id: 'c-1', knowledgeBaseName: '退換貨政策', documentName: '辦法.pdf', excerpt: '七日內', updatedLabel: '2026-09-18' },
          ],
          notice: '引用出處已關閉',
        }),
      ),
    );

    expect(message).toEqual({
      id: 'message-2',
      author: 'assistant',
      createdAt: CREATED_AT,
      reply: {
        kind: 'company-data',
        text: '7 天內可以退貨 [1]。',
        citations: [
          { id: 'c-1', knowledgeBaseName: '退換貨政策', documentName: '辦法.pdf', excerpt: '七日內', updatedLabel: '2026-09-18' },
        ],
        citationNotice: '引用出處已關閉',
      },
    });
  });

  it('maps general-knowledge (missing notice becomes empty) and no-result', () => {
    expect(toChatMessage(assistant(reply({ kind: 'general-knowledge', text: '一般建議', notice: undefined })))).toMatchObject({
      reply: { kind: 'general-knowledge', text: '一般建議', notice: '' },
    });
    expect(toChatMessage(assistant(reply({ kind: 'no-result', text: '查無資料', nextSteps: ['改問別的'] })))).toMatchObject({
      reply: { kind: 'no-result', text: '查無資料', nextSteps: ['改問別的'] },
    });
  });

  it('treats a kind the front end does not know as company data', () => {
    expect(toChatMessage(assistant(reply({ kind: 'something-new' })))).toMatchObject({
      reply: { kind: 'company-data', citationNotice: null },
    });
  });

  it('lets the caller map its own kinds first and falls back to the shared rules for the rest', () => {
    const formRequest: ChatReplyView = { kind: 'form-request', text: '請填表', form: null };
    const extension = (wire: ChatReplyWire): ChatReplyView | null => (wire.kind === 'form-request' ? formRequest : null);

    expect(toChatMessage(assistant(reply({ kind: 'form-request' })), extension)).toMatchObject({ reply: formRequest });
    expect(toChatMessage(assistant(reply({ kind: 'no-result', nextSteps: [] })), extension)).toMatchObject({
      reply: { kind: 'no-result' },
    });
  });
});
