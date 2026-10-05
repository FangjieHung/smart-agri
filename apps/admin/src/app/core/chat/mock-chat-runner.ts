import { EMPTY, concat, concatMap, defer, finalize, from, ignoreElements, map, of, take, timer, type Observable } from 'rxjs';
import type { DemoRepository } from '../repositories/demo-repository';
import type { ChatRunEvent, ChatRunner, ChatRunRequest } from './chat-runner';

/** 每一片的間隔；整則最多 `MAX_SLICES` 片，Demo 的回答一秒內串完。 */
export const MOCK_SLICE_INTERVAL_MS = 40;
const MAX_SLICES = 12;
/**
 * 模擬「模型判斷要不要跳出表單」的時間（issue #171）：真實模型約多等 1.5 秒。Mock 一律以模型判斷模式
 * 示範，所以助理有可用表單時每題都先送 `form-check`、等這麼久再開始回答。
 */
export const MOCK_FORM_CHECK_MS = 1500;
const MIN_SLICE_LENGTH = 4;

/**
 * `?demoScenario=answer-rejected` 先串出的草稿：帶一個不存在的引用編號，
 * 最後的 `reply` 是查無資料，畫面整則替換（M3 計畫第 7 節決定 C）。
 */
export const MOCK_REJECTED_DRAFT = '根據目前的資料，這個問題可以這樣處理 [3]，細節請參考相關文件 [4]。';

/**
 * Mock 模式（含 Pages Demo）的 `ChatRunner`：用 `sendChatMessage` 取得預先準備的回覆，
 * 把文字切成片段依序送出，最後送 `reply`（與 `thread`），讓 Demo 也走同一套串流畫面。
 *
 * 與後端相同的保存規則：`sendChatMessage` 會同時寫入問題與回覆，所以回覆送出前被停止
 * （取消訂閱）時，用 `discardChatReply` 移除那則回覆，只留下問題。
 */
export class MockChatRunner implements ChatRunner {
  constructor(
    private readonly repository: DemoRepository,
    private readonly intervalMs = MOCK_SLICE_INTERVAL_MS,
    private readonly formCheckMs = MOCK_FORM_CHECK_MS,
  ) {}

  run(request: ChatRunRequest): Observable<ChatRunEvent> {
    return defer(() => {
      const result = this.repository.sendChatMessage(
        request.viewerId,
        request.assistantId,
        request.question,
        request.threadId,
      );
      if (result.status === 'validation-failed') {
        return of<ChatRunEvent>({
          type: 'error',
          error: { kind: 'validation-failed', message: result.message, retryable: false },
        });
      }
      if (result.status === 'permission-denied') {
        return of<ChatRunEvent>({
          type: 'error',
          error: { kind: 'permission-denied', message: result.message, retryable: false },
        });
      }
      if (result.status === 'loading') {
        return of<ChatRunEvent>({
          type: 'error',
          error: { kind: 'unavailable', message: '對話服務暫時無法使用，請稍後再試。', retryable: true },
        });
      }

      const chat = result.data;
      const reply = chat.messages[chat.messages.length - 1];
      if (reply === undefined || reply.author !== 'assistant') {
        return of<ChatRunEvent>({
          type: 'error',
          error: { kind: 'failed', message: '這則問題沒有取得回答，請再試一次。', retryable: true },
        });
      }

      const draft =
        this.repository.getScenario() === 'answer-rejected' && reply.reply.kind === 'no-result'
          ? MOCK_REJECTED_DRAFT
          : reply.reply.text;
      let delivered = false;
      // 間隔 0（單元測試）時同步送完，不經過計時器。
      const deltas = from(sliceText(draft)).pipe(
        concatMap((delta) => {
          const event: ChatRunEvent = { type: 'text-delta', delta };
          return this.intervalMs > 0 ? timer(this.intervalMs).pipe(map(() => event)) : of(event);
        }),
      );
      const finish = defer(() => {
        delivered = true;
        const events: ChatRunEvent[] = [{ type: 'reply', message: reply }];
        if (chat.threadId !== null) events.push({ type: 'thread', threadId: chat.threadId, title: chat.title });
        return from(events);
      });

      return concat(this.formCheck(request, reply.reply.kind), deltas, finish).pipe(
        finalize(() => {
          if (!delivered) {
            this.repository.discardChatReply(
              request.viewerId,
              request.assistantId,
              reply.id,
              chat.threadId ?? undefined,
            );
          }
        }),
      );
    });
  }

  /**
   * 與後端相同的條件（issue #171）：數據庫查詢先回答時不判斷表單；助理有這位發起者可用的表單時才送
   * `form-check`。間隔 0（單元測試）時不等待。
   */
  private formCheck(request: ChatRunRequest, replyKind: string): Observable<ChatRunEvent> {
    if (replyKind === 'database-query') return EMPTY;
    return this.repository.listChatForms(request.viewerId, request.assistantId).pipe(
      take(1),
      concatMap((result) => {
        if (result.status !== 'ready' || result.data.length === 0) return EMPTY;
        const event: ChatRunEvent = { type: 'form-check' };
        const wait = this.intervalMs > 0 && this.formCheckMs > 0 ? timer(this.formCheckMs).pipe(ignoreElements()) : EMPTY;
        return concat(of(event), wait);
      }),
    );
  }
}

/** 把回答切成最多 `MAX_SLICES` 片、每片至少 `MIN_SLICE_LENGTH` 個字元。 */
export function sliceText(text: string): readonly string[] {
  const characters = Array.from(text);
  if (characters.length === 0) return [];
  const size = Math.max(MIN_SLICE_LENGTH, Math.ceil(characters.length / MAX_SLICES));
  const slices: string[] = [];
  for (let start = 0; start < characters.length; start += size) {
    slices.push(characters.slice(start, start + size).join(''));
  }
  return slices;
}

