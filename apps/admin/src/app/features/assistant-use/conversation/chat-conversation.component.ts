import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
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
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { AnonymousVisitorService } from '../../../core/session/anonymous-visitor.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { CitationDrawerComponent } from '../citation-drawer/citation-drawer.component';
import { ConsentConfirmationComponent } from '../consent-confirmation/consent-confirmation.component';
import { InlineFormComponent } from '../inline-form/inline-form.component';
import { ChatMessageComponent, type CitationRequest } from '../message/chat-message.component';

type FormFlow =
  | { readonly step: 'closed' }
  | {
      readonly step: 'form';
      readonly form: ChatFormView;
      readonly answers: DatabaseTrialAnswers;
      readonly errors: readonly DatabaseFieldError[];
    }
  | {
      readonly step: 'consent';
      readonly form: ChatFormView;
      readonly answers: DatabaseTrialAnswers;
      readonly entries: readonly DatabaseRecordEntryView[];
      readonly error: string;
    };

const CLOSED: FormFlow = { step: 'closed' };

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
    RouterLink,
    StatePanelComponent,
    ChatMessageComponent,
    CitationDrawerComponent,
    InlineFormComponent,
    ConsentConfirmationComponent,
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
  private readonly injector = inject(Injector);

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

  /** 對話內容變更後遞增，讓 repository 結果重新讀取。 */
  private readonly revision = signal(0);

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

  protected readonly result = computed(() => {
    this.revision();
    const viewerId = this.viewerId();
    if (viewerId === null) return null;

    return this.repository.getAssistantChat(
      viewerId,
      this.assistantId(),
      this.threadId() ?? undefined,
    );
  });
  protected readonly chat = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  /** 切換帳號、助理或對話時，清除草稿、表單與引用狀態，避免殘留前一段對話的內容。 */
  protected readonly draft = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly composerError = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly flow = linkedSignal<string, FormFlow>({ source: this.scope, computation: () => CLOSED });
  protected readonly citations = linkedSignal<string, CitationRequest | null>({
    source: this.scope,
    computation: () => null,
  });

  private readonly log = viewChild<ElementRef<HTMLElement>>('log');
  private readonly composerInput = viewChild<ElementRef<HTMLInputElement>>('composerInput');

  protected updateDraft(event: Event): void {
    this.draft.set((event.target as HTMLInputElement).value);
    this.composerError.set('');
  }

  protected submitDraft(event: Event): void {
    event.preventDefault();
    this.ask(this.draft());
  }

  protected ask(text: string): void {
    const viewerId = this.viewerId();
    if (viewerId === null) return;

    const result = this.repository.sendChatMessage(
      viewerId,
      this.assistantId(),
      text,
      this.threadId() ?? undefined,
    );
    if (result.status === 'validation-failed') {
      this.composerError.set(result.message);
      return;
    }
    this.draft.set('');
    this.composerError.set('');
    // 輸入框可能還沒經過變更偵測同步草稿，直接清空避免殘留已送出的文字。
    const input = this.composerInput()?.nativeElement;
    if (input) input.value = '';
    this.refreshAndReveal(result.status === 'ready' ? result.data.threadId : null);
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
    this.flow.set({ step: 'form', form, answers: {}, errors: [] });
  }

  protected cancelForm(): void {
    this.flow.set(CLOSED);
    this.composerInput()?.nativeElement.focus();
  }

  protected reviewForm(answers: DatabaseTrialAnswers): void {
    const flow = this.flow();
    const viewerId = this.viewerId();
    if (flow.step !== 'form' || viewerId === null) return;

    const result = this.repository.reviewChatForm(viewerId, this.assistantId(), flow.form.id, answers);
    if (result.status === 'validation-failed') {
      this.flow.set({ ...flow, answers, errors: result.errors });
    } else if (result.status === 'ready' || result.status === 'partial-failure') {
      this.flow.set({ step: 'consent', form: flow.form, answers, entries: result.data.entries, error: '' });
    } else if (result.status === 'permission-denied') {
      this.flow.set({ ...flow, answers, errors: [{ fieldId: null, message: result.message }] });
    }
  }

  protected backToForm(): void {
    const flow = this.flow();
    if (flow.step === 'consent') {
      this.flow.set({ step: 'form', form: flow.form, answers: flow.answers, errors: [] });
    }
  }

  protected confirmConsent(): void {
    const flow = this.flow();
    const viewerId = this.viewerId();
    if (flow.step !== 'consent' || viewerId === null) return;

    const result = this.repository.submitChatForm(
      viewerId,
      this.assistantId(),
      { formId: flow.form.id, answers: flow.answers, consent: true },
      this.threadId() ?? undefined,
    );
    if (result.status === 'validation-failed') {
      const fieldErrors = result.errors.filter((error) => error.fieldId !== null);
      this.flow.set(
        fieldErrors.length > 0
          ? { step: 'form', form: flow.form, answers: flow.answers, errors: fieldErrors }
          : { ...flow, error: result.errors[0]?.message ?? result.message },
      );
      return;
    }
    if (result.status === 'permission-denied') {
      this.flow.set({ ...flow, error: result.message });
      return;
    }
    this.flow.set(CLOSED);
    this.refreshAndReveal(result.status === 'ready' ? result.data.threadId : null);
  }

  protected isFormRequest(message: ChatMessageView): boolean {
    return message.author === 'assistant' && message.reply.kind === 'form-request';
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
    this.revision.update((value) => value + 1);
    this.changed.emit(threadId);
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
