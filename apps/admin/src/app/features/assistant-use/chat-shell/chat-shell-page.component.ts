import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type {
  ChatCitationView,
  ChatFormView,
  ChatMessageView,
} from '../../../core/domain/conversation.model';
import type {
  DatabaseFieldError,
  DatabaseRecordEntryView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
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

/** 終端使用者的助理對話頁：手機優先，對話只屬於目前帳號，回覆全部來自 fixtures。 */
@Component({
  selector: 'app-chat-shell-page',
  imports: [
    RouterLink,
    StatePanelComponent,
    ChatMessageComponent,
    CitationDrawerComponent,
    InlineFormComponent,
    ConsentConfirmationComponent,
  ],
  templateUrl: './chat-shell-page.component.html',
  styleUrl: './chat-shell-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatShellPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly injector = inject(Injector);

  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  private readonly assistantId = computed(() => this.params().get('assistantId') ?? '');
  /** 對話內容變更後遞增，讓 repository 結果重新讀取。 */
  private readonly revision = signal(0);
  private readonly scope = computed(() => `${this.session.activeAccountId() ?? ''}|${this.assistantId()}`);

  protected readonly result = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.getAssistantChat(accountId, this.assistantId()) : null;
  });
  protected readonly chat = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  /** 切換帳號或助理時，清除草稿、表單與引用狀態，避免殘留前一位使用者的內容。 */
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
    const accountId = this.session.activeAccountId();
    if (!accountId) return;

    const result = this.repository.sendChatMessage(accountId, this.assistantId(), text);
    if (result.status === 'validation-failed') {
      this.composerError.set(result.message);
      return;
    }
    this.draft.set('');
    this.composerError.set('');
    // 輸入框可能還沒經過變更偵測同步草稿，直接清空避免殘留已送出的文字。
    const input = this.composerInput()?.nativeElement;
    if (input) input.value = '';
    this.refreshAndReveal();
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
    const accountId = this.session.activeAccountId();
    if (flow.step !== 'form' || !accountId) return;

    const result = this.repository.reviewChatForm(accountId, this.assistantId(), flow.form.id, answers);
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
    const accountId = this.session.activeAccountId();
    if (flow.step !== 'consent' || !accountId) return;

    const result = this.repository.submitChatForm(accountId, this.assistantId(), {
      formId: flow.form.id,
      answers: flow.answers,
      consent: true,
    });
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
    this.refreshAndReveal();
  }

  protected isFormRequest(message: ChatMessageView): boolean {
    return message.author === 'assistant' && message.reply.kind === 'form-request';
  }

  protected returnHome(): void {
    void this.router.navigateByUrl('/app/home');
  }

  private refreshAndReveal(): void {
    this.revision.update((value) => value + 1);
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
