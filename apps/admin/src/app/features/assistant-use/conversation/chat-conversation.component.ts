import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { finalize, type Subscription } from 'rxjs';
import { CHAT_RUNNER, type ChatHistoryEntry, type ChatRunError, type ChatRunEvent } from '../../../core/chat/chat-runner';
import { isVisitorId, type ChatViewerId } from '../../../core/domain/account.model';
import type {
  ChatCitationView,
  ChatFormView,
  ChatMessageView,
  ChatThreadId,
} from '../../../core/domain/conversation.model';
import type {
  DatabaseFieldError,
  DatabaseRecordEntryView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { AssistantIssuesRepository, type CreateAssistantHandoffRequest } from '../../../core/repositories/assistant-issues.repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { AnonymousVisitorService } from '../../../core/session/anonymous-visitor.service';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { CitationDrawerComponent } from '../citation-drawer/citation-drawer.component';
import { ConsentConfirmationComponent } from '../consent-confirmation/consent-confirmation.component';
import { InlineFormComponent } from '../inline-form/inline-form.component';
import { StreamingReplyComponent } from '../streaming-reply/streaming-reply.component';
import {
  ChatMessageComponent,
  type CitationRequest,
  type WithdrawRequest,
} from '../message/chat-message.component';

/**
 * 對話中表單的流程。`submissionId` 是冪等鍵（issue #148）：開始填寫時產生一次，確認與失敗重試都
 * 沿用，伺服器不會重複建立紀錄；只有成功、或伺服器說這個編號已用在別的內容時才換新的。
 * `busy`：檢查或送出中，按鈕停用避免重複送出。
 */
type FormFlow =
  | { readonly step: 'closed' }
  | {
      readonly step: 'form';
      readonly form: ChatFormView;
      readonly answers: DatabaseTrialAnswers;
      readonly errors: readonly DatabaseFieldError[];
      readonly submissionId: string;
      readonly busy: boolean;
    }
  | {
      readonly step: 'consent';
      readonly form: ChatFormView;
      readonly answers: DatabaseTrialAnswers;
      readonly entries: readonly DatabaseRecordEntryView[];
      readonly error: string;
      readonly submissionId: string;
      readonly busy: boolean;
    };

const FORM_REVIEW_FAILED_MESSAGE = '目前無法檢查填寫內容，請稍後再試；資料還沒有送出。';
const FORM_SUBMIT_FAILED_MESSAGE = '送出失敗，資料可能還沒有送達。請再按一次「同意並送出」，不會重複建立紀錄。';

const CLOSED: FormFlow = { step: 'closed' };

/**
 * 這一頁送出、但還不在讀回資料裡的訊息（串流完成或停止的那幾則）。
 * `stopped`：使用者按了停止，只留下問題、不顯示半則回答。
 */
interface LocalEntry {
  readonly message: ChatMessageView;
  readonly note: 'stopped' | 'unanswered' | null;
}

/**
 * 目前這一則問題的狀態（issue #80）：
 * - `streaming`：已送出、正在串流；送出鍵鎖住，顯示「停止回答」；
 * - `failed`：`RUN_ERROR`、503 等錯誤，問題留在畫面上並提供重試。
 */
type RunState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'streaming'; readonly question: string; readonly text: string; readonly clientMessageId: string }
  | { readonly phase: 'failed'; readonly question: string; readonly error: ChatRunError; readonly clientMessageId: string };

const IDLE: RunState = { phase: 'idle' };

interface HandoffExchange {
  readonly questionId: string;
  readonly answerId: string;
  readonly question: string;
  readonly answer: string;
}

/**
 * 助理對話的單一實作：`/use/:assistantId`（嵌入用）與工作區的 `/app/chat` 都用這個元件，
 * 只靠 `header` 決定要不要顯示頁面外框。回覆全部來自 fixtures。
 *
 * 對話只屬於「目前的發起者」：可能是已選擇的 Demo 身分，也可能是 `allowAnonymous`
 * 開啟時這個瀏覽器分頁的未登入訪客。訪客模式下畫面不會出現任何 `/app` 連結。
 */
@Component({
  selector: 'app-chat-conversation',
  imports: [
    A11yModule,
    RouterLink,
    StatePanelComponent,
    ChatMessageComponent,
    CitationDrawerComponent,
    InlineFormComponent,
    ConsentConfirmationComponent,
    StreamingReplyComponent,
  ],
  templateUrl: './chat-conversation.component.html',
  styleUrl: './chat-conversation.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatConversationComponent {
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly visitor = inject(AnonymousVisitorService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly apiSession = inject(ApiSessionService);
  private readonly runner = inject(CHAT_RUNNER);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  readonly assistantId = input.required<string>();
  /** null 代表開啟最後一次使用的對話；沒有任何對話時是一段還沒建立的空白對話。 */
  readonly threadId = input<string | null>(null);
  /**
   * full：頁首含返回連結、助理名稱與用途（`/use`）。
   * minimal：只保留視覺隱藏的標題（`/use?embed=1`，嵌入網站時不顯示品牌外框）。
   * none：完全不顯示，由外層頁面提供標題（工作區）。
   */
  readonly header = input<'full' | 'minimal' | 'none'>('full');
  /**
   * 允許未登入的官網訪客使用（只有 `/use/:assistantId` 會開啟）。
   * 開啟後沒有 Demo 身分時改用這個分頁的匿名訪客 id，畫面也不再出現任何工作區連結。
   */
  readonly allowAnonymous = input(false);
  /** 訊息有變動時送出目前的對話 id，讓外層頁面同步網址與對話紀錄。 */
  readonly changed = output<ChatThreadId | null>();

  /** API 模式的回答來自真實模型，輸入框下方的說明不同。 */
  protected readonly apiMode = this.apiSession.apiMode;

  /** 目前的發起者：已選擇的 Demo 身分優先，其次才是這個分頁的匿名訪客。 */
  private readonly viewerId = computed<ChatViewerId | null>(
    () => this.session.activeAccountId() ?? (this.allowAnonymous() ? this.visitor.visitorId() : null),
  );
  /** 未登入訪客模式：隱藏所有工作區連結，並改用訪客專屬的說明文案。 */
  protected readonly anonymous = computed(() => {
    const viewerId = this.viewerId();
    return viewerId !== null && isVisitorId(viewerId);
  });
  private readonly scope = computed(
    () => `${this.viewerId() ?? ''}|${this.assistantId()}|${this.threadId() ?? ''}`,
  );

  /**
   * 非同步契約（issue #79）：`getAssistantChat` 不再接收 viewerId，由 repository 內部
   * 推導；這裡只在有發起者時才讀取，沒有時停在 loading（與過去 `viewerId === null` 時
   * 回傳 `null` 的行為相同）。寫入成功後呼叫 `chatResource.reload()` 重新讀取。
   */
  private readonly chatResource = repositoryResource({
    params: () => {
      const viewerId = this.viewerId();
      if (viewerId === null) return undefined;
      return { assistantId: this.assistantId(), threadId: this.threadId() ?? undefined };
    },
    stream: ({ assistantId, threadId }) => this.repository.getAssistantChat(assistantId, threadId),
  });
  protected readonly result = this.chatResource.view;
  protected readonly chat = computed(() => {
    const result = this.result();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });

  /** 切換帳號、助理或對話時，清除草稿、表單與引用狀態，避免殘留前一段對話的內容。 */
  protected readonly draft = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly composerError = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly flow = linkedSignal<string, FormFlow>({ source: this.scope, computation: () => CLOSED });
  protected readonly citations = linkedSignal<string, CitationRequest | null>({
    source: this.scope,
    computation: () => null,
  });
  /** 等待確認的撤回；撤回是破壞性動作，所以一定先問過再送出。 */
  protected readonly pendingWithdrawal = linkedSignal<string, WithdrawRequest | null>({
    source: this.scope,
    computation: () => null,
  });
  protected readonly withdrawFeedback = linkedSignal({ source: this.scope, computation: () => '' });
  /** 表單流程被迫結束時的說明（表單已改版、已無法使用），顯示在對話下方。 */
  protected readonly formNotice = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly withdrawError = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly pendingHandoff = linkedSignal<string, HandoffExchange | null>({ source: this.scope, computation: () => null });
  protected readonly handoffBusy = linkedSignal({ source: this.scope, computation: () => false });
  protected readonly handoffError = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly handoffIssueId = linkedSignal<string, string | null>({ source: this.scope, computation: () => null });

  /**
   * 串流完成（或停止）後不重新讀取，直接接在讀回的訊息後面：不保存對話的助理在 API
   * 模式讀不回來。換對話或重新讀取（例如撤回、換到新建的對話串）時清空，以讀回的為準。
   */
  protected readonly local = linkedSignal<unknown, readonly LocalEntry[]>({
    source: () => ({ scope: this.scope(), chat: this.chat() }),
    computation: () => [],
  });
  protected readonly run = linkedSignal<string, RunState>({ source: this.scope, computation: () => IDLE });
  protected readonly streaming = computed(() => this.run().phase === 'streaming');
  /** 給螢幕報讀器的狀態：開始回答與停止各說一次，串流文字本身不朗讀。 */
  protected readonly runStatus = linkedSignal({ source: this.scope, computation: () => '' });
  private activeRun: Subscription | null = null;
  private localCounter = 0;

  private readonly log = viewChild<ElementRef<HTMLElement>>('log');
  private readonly composerInput = viewChild<ElementRef<HTMLInputElement>>('composerInput');
  private readonly withdrawCancelButton = viewChild<ElementRef<HTMLButtonElement>>('withdrawCancelButton');
  private readonly handoffCancelButton = viewChild<ElementRef<HTMLButtonElement>>('handoffCancelButton');
  private readonly stopButton = viewChild<ElementRef<HTMLButtonElement>>('stopButton');

  constructor() {
    // 確認對話框一出現就把焦點帶到「取消」，與對話紀錄側欄的刪除確認一致。
    effect(() => this.withdrawCancelButton()?.nativeElement.focus());
    effect(() => this.handoffCancelButton()?.nativeElement.focus());
    // 換帳號、助理或對話時，進行中的回答不再屬於畫面上的對話：直接取消。
    effect(() => {
      this.scope();
      untracked(() => this.cancelActiveRun());
    });
    this.destroyRef.onDestroy(() => this.cancelActiveRun());
  }

  protected updateDraft(event: Event): void {
    this.draft.set((event.target as HTMLInputElement).value);
    this.composerError.set('');
  }

  protected submitDraft(event: Event): void {
    event.preventDefault();
    this.ask(this.draft());
  }

  /**
   * `retryMessageId` 只在 {@link retry} 呼叫時給：沿用失敗那次的 id，讓後端把這次重試
   * 的問題視為同一則，不重複保存（issue #105）。一般送出（新問題）沒有它，每次都是
   * 新的 id。
   */
  protected ask(text: string, retryMessageId?: string): void {
    const viewerId = this.viewerId();
    if (viewerId === null || this.streaming()) return;

    const question = text.trim();
    if (question.length === 0) {
      // 與 repository 相同的訊息；不必為了空白問題送出請求。
      this.composerError.set('請先輸入問題。');
      return;
    }
    const previous = this.run();
    // 上一則失敗、使用者改問別的：失敗的問題留在紀錄裡，標示沒有取得回答。
    if (previous.phase === 'failed') this.keepQuestion(previous.question, 'unanswered');

    const chat = this.chat();
    const history = chat?.historyMode === 'not-saved' ? this.historyEntries() : undefined;
    const clientMessageId = retryMessageId ?? crypto.randomUUID();
    this.draft.set('');
    this.composerError.set('');
    // 輸入框可能還沒經過變更偵測同步草稿，直接清空避免殘留已送出的文字。
    const input = this.composerInput()?.nativeElement;
    if (input) input.value = '';
    this.run.set({ phase: 'streaming', question, text: '', clientMessageId });
    this.runStatus.set('助理正在回答…');
    this.reveal();

    let threadId: string | null = null;
    let answered = false;
    this.activeRun = this.runner
      .run({
        viewerId,
        assistantId: this.assistantId(),
        question: text,
        threadId: this.threadId() ?? undefined,
        history,
        clientMessageId,
      })
      .subscribe({
        next: (event) => {
          if (event.type === 'thread') threadId = event.threadId;
          if (event.type === 'reply') answered = true;
          this.onRunEvent(event, question, text, clientMessageId);
        },
        complete: () => {
          this.activeRun = null;
          if (answered) this.changed.emit(threadId);
        },
      });
  }

  /** 停止：取消串流，只留下問題、不顯示半則回答（後端也只保存問題）。 */
  protected stop(): void {
    const current = this.run();
    if (current.phase !== 'streaming') return;
    this.cancelActiveRun();
    this.keepQuestion(current.question, 'stopped');
    this.run.set(IDLE);
    this.runStatus.set('已停止回答。');
    this.composerInput()?.nativeElement.focus();
    // 保存對話時問題已經寫入，讓外層重新讀取對話清單。
    this.changed.emit(null);
  }

  protected retry(): void {
    const current = this.run();
    if (current.phase !== 'failed') return;
    this.run.set(IDLE);
    this.ask(current.question, current.clientMessageId);
  }

  /** 串流中的問題以使用者訊息的樣子顯示；id 只在這一頁使用。 */
  protected pendingQuestion(text: string): ChatMessageView {
    return { id: 'pending-question', author: 'account', text, createdAt: '' };
  }

  private onRunEvent(event: ChatRunEvent, question: string, rawText: string, clientMessageId: string): void {
    switch (event.type) {
      case 'text-delta':
        this.run.update((current) =>
          current.phase === 'streaming' ? { ...current, text: current.text + event.delta } : current,
        );
        return;
      case 'reply':
        // 最終的回覆整則取代串流文字（驗證失敗時就是查無資料），id 以它為準。
        this.local.update((entries) => [
          ...entries,
          { message: this.localQuestion(question), note: null },
          { message: event.message, note: null },
        ]);
        this.finishRun();
        return;
      case 'thread':
        return;
      case 'error':
        if (event.error.kind === 'validation-failed') {
          // 問題本身不合規則：放回輸入框讓使用者修改，畫面上不留下這則問題。
          this.run.set(IDLE);
          this.runStatus.set('');
          this.draft.set(rawText);
          this.composerError.set(event.error.message);
          const input = this.composerInput()?.nativeElement;
          if (input) input.value = rawText;
          input?.focus();
          return;
        }
        this.run.set({ phase: 'failed', question, error: event.error, clientMessageId });
        this.finishRun();
        return;
    }
  }

  private finishRun(): void {
    const stopFocused = document.activeElement === this.stopButton()?.nativeElement;
    this.runStatus.set('');
    if (this.run().phase === 'streaming') this.run.set(IDLE);
    // 停止鍵隨串流結束消失；焦點在它上面時交回輸入框，不讓它掉回 body。
    if (stopFocused) this.composerInput()?.nativeElement.focus();
    this.reveal();
  }

  private keepQuestion(question: string, note: LocalEntry['note']): void {
    this.local.update((entries) => [...entries, { message: this.localQuestion(question), note }]);
  }

  private localQuestion(text: string): ChatMessageView {
    this.localCounter += 1;
    return { id: `local-question-${this.localCounter}`, author: 'account', text, createdAt: new Date().toISOString() };
  }

  /** 不保存對話的助理：把畫面上的問答當成前文（表單與收據不送）。 */
  private historyEntries(): ChatHistoryEntry[] {
    const messages = [...(this.chat()?.messages ?? []), ...this.local().map((entry) => entry.message)];
    return messages.flatMap((message): ChatHistoryEntry[] => {
      if (message.author === 'account') return [{ role: 'user', content: message.text }];
      const kind = message.reply.kind;
      if (kind === 'form-request' || kind === 'submission-receipt') return [];
      return [{ role: 'assistant', content: message.reply.text }];
    });
  }

  private cancelActiveRun(): void {
    this.activeRun?.unsubscribe();
    this.activeRun = null;
  }

  protected openCitations(request: CitationRequest): void {
    this.citations.set(request);
  }

  protected closeCitations(): void {
    const trigger = this.citations()?.trigger;
    this.citations.set(null);
    trigger?.focus();
  }

  protected citationList(): readonly ChatCitationView[] {
    return this.citations()?.citations ?? [];
  }

  protected startForm(form: ChatFormView): void {
    this.formNotice.set('');
    this.flow.set({ step: 'form', form, answers: {}, errors: [], submissionId: crypto.randomUUID(), busy: false });
  }

  protected cancelForm(): void {
    // 取消＝什麼都不送出，不會留下任何紀錄。
    this.flow.set(CLOSED);
    this.composerInput()?.nativeElement.focus();
  }

  protected reviewForm(answers: DatabaseTrialAnswers): void {
    const flow = this.flow();
    const viewerId = this.viewerId();
    if (flow.step !== 'form' || flow.busy || viewerId === null) return;

    this.flow.set({ ...flow, answers, busy: true });
    this.repository
      .reviewChatForm(viewerId, this.assistantId(), flow.form.id, flow.form.formVersion, answers)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          const current = { ...flow, answers, busy: false };
          if (result.status === 'validation-failed') {
            this.flow.set({ ...current, errors: result.errors });
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.flow.set({
              step: 'consent',
              form: flow.form,
              answers,
              entries: result.data.entries,
              error: '',
              submissionId: flow.submissionId,
              busy: false,
            });
          } else if (result.status === 'conflict') {
            this.closeStaleForm(result.message);
          } else if (result.status === 'permission-denied') {
            this.flow.set({ ...current, errors: [{ fieldId: null, message: result.message }] });
          }
        },
        error: () => this.flow.set({ ...flow, answers, busy: false, errors: [{ fieldId: null, message: FORM_REVIEW_FAILED_MESSAGE }] }),
      });
  }

  protected backToForm(): void {
    const flow = this.flow();
    if (flow.step === 'consent' && !flow.busy) {
      this.flow.set({
        step: 'form',
        form: flow.form,
        answers: flow.answers,
        errors: [],
        submissionId: flow.submissionId,
        busy: false,
      });
    }
  }

  protected confirmConsent(): void {
    const flow = this.flow();
    const viewerId = this.viewerId();
    if (flow.step !== 'consent' || flow.busy || viewerId === null) return;

    this.flow.set({ ...flow, busy: true, error: '' });
    this.repository
      .submitChatForm(
        viewerId,
        this.assistantId(),
        {
          formId: flow.form.id,
          formVersion: flow.form.formVersion,
          submissionId: flow.submissionId,
          answers: flow.answers,
          consent: true,
        },
        this.chat()?.threadId ?? this.threadId() ?? undefined,
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          const idle = { ...flow, busy: false };
          if (result.status === 'validation-failed') {
            const fieldErrors = result.errors.filter((error) => error.fieldId !== null);
            this.flow.set(
              fieldErrors.length > 0
                ? { step: 'form', form: flow.form, answers: flow.answers, errors: fieldErrors, submissionId: flow.submissionId, busy: false }
                : { ...idle, error: result.errors[0]?.message ?? result.message },
            );
            return;
          }
          if (result.status === 'conflict') {
            if (result.reason === 'form-version-changed') {
              this.closeStaleForm(result.message);
            } else {
              // 這個編號已用在別的內容上：換一個新的再送，不會覆蓋那一筆。
              this.flow.set({ ...idle, submissionId: crypto.randomUUID(), error: result.message });
            }
            return;
          }
          if (result.status === 'permission-denied') {
            this.flow.set({ ...idle, error: result.message });
            return;
          }
          if (result.status !== 'ready' && result.status !== 'partial-failure') return;

          this.flow.set(CLOSED);
          if (this.chat()?.historyMode === 'saved') {
            this.refreshAndReveal(result.data.threadId);
          } else {
            // 不保存對話：收據只在這一頁出現（紀錄本身已經存進資料庫）。
            this.local.update((entries) => [...entries, { message: result.data.message, note: null }]);
            this.reveal();
          }
        },
        error: () => this.flow.set({ ...flow, busy: false, error: FORM_SUBMIT_FAILED_MESSAGE }),
      });
  }

  /** 表單在顯示之後改版：結束這次填寫（沒有送出任何資料），重新讀取後再開啟最新的表單。 */
  private closeStaleForm(message: string): void {
    this.flow.set(CLOSED);
    this.formNotice.set(message);
    if (this.chat()?.historyMode === 'saved') this.chatResource.reload();
  }

  protected askWithdraw(request: WithdrawRequest): void {
    this.withdrawError.set('');
    this.withdrawFeedback.set('');
    this.pendingWithdrawal.set(request);
  }

  protected cancelWithdraw(): void {
    const trigger = this.pendingWithdrawal()?.trigger;
    this.pendingWithdrawal.set(null);
    trigger?.focus();
  }

  protected confirmWithdraw(): void {
    const pending = this.pendingWithdrawal();
    const viewerId = this.viewerId();
    this.pendingWithdrawal.set(null);
    if (pending === null || viewerId === null) return;

    const result = this.repository.withdrawChatSubmission(
      viewerId,
      this.assistantId(),
      pending.recordId,
      this.threadId() ?? undefined,
    );
    if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.withdrawError.set(result.message);
      pending.trigger.focus();
      return;
    }
    if (result.status === 'loading') return;

    this.withdrawError.set('');
    this.withdrawFeedback.set(
      '已撤回這筆資料：接收單位的收集紀錄已移除內容，只留下一筆「曾提交、已撤回」的軌跡。',
    );
    this.chatResource.reload();
    // 撤回鍵已經消失，把焦點交回輸入框，不讓它掉回 body。
    this.composerInput()?.nativeElement.focus();
  }

  protected isFormRequest(message: ChatMessageView): boolean {
    return message.author === 'assistant' && message.reply.kind === 'form-request';
  }

  protected handoffExchange(message: ChatMessageView): HandoffExchange | null {
    const chat = this.chat();
    if (this.anonymous() || !chat || message.author !== 'assistant'
      || message.reply.kind === 'form-request' || message.reply.kind === 'submission-receipt') return null;
    const messages = [...chat.messages, ...this.local().map((entry) => entry.message)];
    const index = messages.findIndex((entry) => entry.id === message.id);
    const question = messages[index - 1];
    if (index < 1 || question?.author !== 'account') return null;
    if (chat.historyMode === 'saved' && (question.id.startsWith('local-question-') || !chat.threadId)) return null;
    return { questionId: question.id, answerId: message.id, question: question.text, answer: message.reply.text };
  }

  protected askHandoff(message: ChatMessageView): void {
    const exchange = this.handoffExchange(message);
    if (!exchange || this.handoffBusy()) return;
    this.handoffError.set('');
    this.pendingHandoff.set(exchange);
  }

  protected cancelHandoff(): void {
    if (this.handoffBusy()) return;
    this.pendingHandoff.set(null);
  }

  protected confirmHandoff(): void {
    const exchange = this.pendingHandoff();
    const chat = this.chat();
    if (!exchange || !chat || this.handoffBusy()) return;
    const request: CreateAssistantHandoffRequest = chat.historyMode === 'saved'
      ? { threadId: chat.threadId ?? undefined, questionMessageId: exchange.questionId, answerMessageId: exchange.answerId, confirmed: true }
      : { sharedQuestion: exchange.question, sharedAnswer: exchange.answer, confirmed: true };
    this.handoffBusy.set(true);
    this.handoffError.set('');
    this.issues.createHandoff(this.assistantId(), request, {
      assistantName: chat.assistantName, question: exchange.question, answer: exchange.answer,
      historyMode: chat.historyMode,
    }).pipe(finalize(() => this.handoffBusy.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        if (result.status === 'ready') {
          this.pendingHandoff.set(null);
          this.handoffIssueId.set(result.data.id);
        } else {
          this.handoffError.set(result.message);
        }
      },
      error: () => this.handoffError.set('目前無法轉交，請稍後再試。'),
    });
  }

  /** 拒絕畫面的標題：訪客與帳號的字不同，但兩邊都不揭露助理名稱或是否存在。 */
  protected deniedTitle(reason: string): string {
    if (reason === 'chat-thread') return '找不到這段對話';
    return this.anonymous() ? '無法開啟這個助理' : '無法使用這個助理';
  }

  /** 訪客沒有工作區可回，所以不提供任何導回 `/app` 的動作。 */
  protected recoveryLabel(): string {
    return this.anonymous() ? '' : '返回首頁';
  }

  protected returnHome(): void {
    void this.router.navigateByUrl('/app/home');
  }

  private refreshAndReveal(threadId: ChatThreadId | null): void {
    this.chatResource.reload();
    this.changed.emit(threadId);
    this.reveal();
  }

  private reveal(): void {
    afterNextRender(
      () => {
        const last = this.log()?.nativeElement.lastElementChild;
        if (last instanceof HTMLElement && typeof last.scrollIntoView === 'function') {
          last.scrollIntoView({ block: 'nearest' });
        }
      },
      { injector: this.injector },
    );
  }
}
