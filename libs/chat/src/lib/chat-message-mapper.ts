import type { ChatCitationView, ChatMessageView, ChatReplyView } from './chat-view.model';

/**
 * 後端 `ChatReplyView` 的線上形狀（一個扁平物件，不適用的欄位為 null），只列出 lib 自己會讀的欄位。
 * 生成的 API 型別（admin 的 `api-schema.ts`）與官網訪客 API 的型別都結構相容，所以 lib 不必匯入它們。
 */
export interface ChatReplyWire {
  readonly kind: string;
  readonly text: string;
  readonly citations: readonly ChatCitationView[];
  readonly notice?: string | null;
  readonly nextSteps: readonly string[];
}

/** 後端 `ChatMessageView` 的線上形狀；`reply` 有值是助理的回覆，否則是使用者自己的提問（`text`）。 */
export interface ChatMessageWire<TReply extends ChatReplyWire = ChatReplyWire> {
  readonly id: string;
  readonly text?: string | null;
  readonly reply?: TReply | null;
  readonly createdAt: string;
}

/**
 * 只有 admin 會收到的回覆種類（`form-request`、`submission-receipt`、`database-query`、`case-proposal`）由使用端轉換：
 * 它們牽涉表單、收據與數據庫的對應，不屬於共用的對話元件。回傳 `null` 代表「這個 kind 不是我處理的」，
 * 交回 lib 依通用規則轉換。
 */
export type ChatReplyExtensionMapper<TReply extends ChatReplyWire = ChatReplyWire> = (
  reply: TReply,
) => ChatReplyView | null;

/**
 * 後端的 `ChatReplyView` 是同一個扁平形狀（不適用的欄位為 null），不是前端的
 * discriminated union（`docs/plans/2026-09-27-backend-milestone-3-in-platform-chat.md`
 * 第 3 節「與前端型別的差異」）；這裡依 `kind` 轉回前端的變體。
 */
export function toChatReply<TReply extends ChatReplyWire>(
  reply: TReply,
  extension?: ChatReplyExtensionMapper<TReply>,
): ChatReplyView {
  const extended = extension?.(reply) ?? null;
  if (extended !== null) return extended;
  if (reply.kind === 'general-knowledge') {
    return { kind: 'general-knowledge', text: reply.text, notice: reply.notice ?? '' };
  }
  if (reply.kind === 'no-result') {
    return { kind: 'no-result', text: reply.text, nextSteps: [...reply.nextSteps] };
  }
  // 'company-data'，以及任何後端未來新增、前端還不認得的 kind 一律當成組織資料回覆。
  return {
    kind: 'company-data',
    text: reply.text,
    citations: reply.citations.map((citation) => ({
      id: citation.id,
      knowledgeBaseName: citation.knowledgeBaseName,
      documentName: citation.documentName,
      excerpt: citation.excerpt,
      updatedLabel: citation.updatedLabel,
    })),
    citationNotice: reply.notice ?? null,
  };
}

/** 也給 AG-UI 串流的 `smartagri.reply` 使用（`AgUiChatRunner`；值與 `GET chat` 的訊息相同）。 */
export function toChatMessage<TReply extends ChatReplyWire>(
  message: ChatMessageWire<TReply>,
  extension?: ChatReplyExtensionMapper<TReply>,
): ChatMessageView {
  // issue #106：後端現在一律送出 `reply`（有值或明確的 `null`），不再用
  // `JsonIgnore(WhenWritingNull)` 省略——PR #103 修過一次「省略鍵被誤判成 undefined」的 bug，
  // 這裡繼續容忍 `undefined` 只是防禦性寫法，不代表現在真的會發生。
  if (message.reply !== null && message.reply !== undefined) {
    return {
      id: message.id,
      author: 'assistant',
      reply: toChatReply(message.reply, extension),
      createdAt: message.createdAt,
    };
  }
  return { id: message.id, author: 'account', text: message.text ?? '', createdAt: message.createdAt };
}
