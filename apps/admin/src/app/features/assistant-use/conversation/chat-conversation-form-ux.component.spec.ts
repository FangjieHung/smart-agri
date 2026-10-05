import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { Observable, of, type Subscriber } from 'rxjs';
import { vi } from 'vitest';
import { CHAT_RUNNER, type ChatRunEvent, type ChatRunner } from '../../../core/chat/chat-runner';
import type { ChatFormView } from '../../../core/domain/conversation.model';
import { orderForm, provideAssistantUseTesting } from '../assistant-use.testing';
import { FORM_CHECK_PHRASES, FORM_CHECK_STATUS_TEXT } from '../form-check-status/form-check-status.component';
import { ChatConversationComponent } from './chat-conversation.component';

const ASSISTANT = 'assistant-customer-service';
const VIEWER = 'account-external-customer';

/** 由測試逐一推送事件的 runner。 */
class ScriptedRunner implements ChatRunner {
  private current: Subscriber<ChatRunEvent> | null = null;

  run(): Observable<ChatRunEvent> {
    return new Observable<ChatRunEvent>((subscriber) => {
      this.current = subscriber;
    });
  }

  emit(event: ChatRunEvent): void {
    this.current?.next(event);
  }
}

interface SetupOptions {
  /** 先在 repository 問一題，讓對話停在「需要填寫資料」的訊息上。 */
  readonly formRequest?: boolean;
  /** 覆寫「回報資料」入口的表單清單；省略時用 mock 的規則（客服助理有一份訂單表單）。 */
  readonly forms?: readonly ChatFormView[];
  readonly compact?: boolean;
  readonly runner?: ChatRunner;
}

function setup(options: SetupOptions = {}) {
  const testing = provideAssistantUseTesting(VIEWER);
  if (options.formRequest) {
    const asked = testing.repository.sendChatMessage(VIEWER, ASSISTANT, '我要回報訂單問題');
    if (asked.status !== 'ready') throw new Error('expected a form request');
  }
  if (options.forms !== undefined) {
    vi.spyOn(testing.repository, 'listChatForms').mockReturnValue(of({ status: 'ready', data: options.forms }));
  }
  const dismiss = vi.spyOn(testing.repository, 'dismissChatForm');
  const submit = vi.spyOn(testing.repository, 'submitChatForm');
  TestBed.configureTestingModule({
    imports: [ChatConversationComponent],
    providers: [
      provideRouter([]),
      ...testing.providers,
      ...(options.runner ? [{ provide: CHAT_RUNNER, useValue: options.runner }] : []),
      ...(options.compact
        ? [{ provide: BreakpointObserver, useValue: { observe: () => of({ matches: true, breakpoints: {} }) } }]
        : []),
    ],
  });
  const fixture: ComponentFixture<ChatConversationComponent> = TestBed.createComponent(ChatConversationComponent);
  fixture.componentRef.setInput('assistantId', ASSISTANT);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository, dismiss, submit };
}

function click(host: HTMLElement, selector: string): void {
  const element = host.querySelector<HTMLElement>(selector);
  if (element === null) throw new Error(`missing ${selector}`);
  element.focus();
  element.click();
}

function buttonByText(host: ParentNode, text: string): HTMLButtonElement {
  const button = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((candidate) => candidate.textContent?.trim() === text);
  if (button === undefined) throw new Error(`missing button ${text}`);
  return button;
}

function key(target: Element | null, name: string): void {
  target?.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true }));
}

const secondForm = (): ChatFormView => ({ ...orderForm(), id: 'database-customer-records', title: '客戶資料更新' });

describe('ChatConversationComponent form-request UX (#171)', () => {
  afterEach(() => document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()));

  describe('① checking state', () => {
    function ask(host: HTMLElement, fixture: ComponentFixture<ChatConversationComponent>) {
      const input = host.querySelector<HTMLInputElement>('#chat-input');
      if (input === null) throw new Error('missing composer');
      input.value = '收到商品後幾天內可以退貨？';
      input.dispatchEvent(new Event('input'));
      click(host, 'form.composer button[type="submit"]');
      fixture.detectChanges();
    }

    it('shows only on the server event, with a fixed screen-reader text, until the first answer text', () => {
      const runner = new ScriptedRunner();
      const { fixture, host } = setup({ runner });
      ask(host, fixture);

      // Before the event: the usual 「正在回答…」 bubble, no checking state.
      expect(host.querySelector('[app-form-check-status]')).toBeNull();
      expect(host.querySelector('app-streaming-reply')).not.toBeNull();

      runner.emit({ type: 'form-check' });
      fixture.detectChanges();
      const status = host.querySelector('[app-form-check-status] [role="status"]');
      expect(status?.querySelector('.visually-hidden')?.textContent?.trim()).toBe(FORM_CHECK_STATUS_TEXT);
      const phrases = status?.querySelector('.phrases');
      expect(phrases?.getAttribute('aria-hidden')).toBe('true');
      expect(Array.from(phrases?.querySelectorAll('.phrase') ?? []).map((node) => node.textContent?.trim())).toEqual([
        ...FORM_CHECK_PHRASES,
      ]);
      expect(host.querySelector('app-streaming-reply')).toBeNull();
      expect(host.querySelector('button.stop')?.textContent?.trim()).toBe('停止回答');

      runner.emit({ type: 'text-delta', delta: '收到商品後' });
      fixture.detectChanges();
      expect(host.querySelector('app-streaming-reply')?.textContent).toContain('收到商品後');
    });

    it('is hidden by the form request itself (no answer text before it)', () => {
      const runner = new ScriptedRunner();
      const { fixture, host } = setup({ runner });
      ask(host, fixture);
      runner.emit({ type: 'form-check' });
      fixture.detectChanges();
      expect(host.querySelector('[app-form-check-status]')).not.toBeNull();

      runner.emit({
        type: 'reply',
        message: {
          id: 'form-request-1',
          author: 'assistant',
          createdAt: '2026-10-05T02:00:00.000Z',
          reply: { kind: 'form-request', text: '可以的，請在下方表單填寫資料。', form: orderForm() },
        },
      });
      fixture.detectChanges();
      expect(host.querySelector('[app-form-check-status]')).toBeNull();
      expect(host.querySelector('[data-kind="form-request"]')).not.toBeNull();
    });
  });

  describe('② leaving the card', () => {
    it('asks before filling in, then closes with × without sending anything and records the dismissal', () => {
      const { fixture, host, dismiss, submit } = setup({ formRequest: true });
      const request = host.querySelector('[data-kind="form-request"]');
      expect(request?.querySelector('.form-offer')?.textContent?.trim()).toBe('我找到一份可能相關的表單。要現在開始填寫嗎？');

      click(host, 'button.form-start');
      fixture.detectChanges();
      click(host, 'app-inline-form button[aria-label="關閉表單，不填寫"]');
      fixture.detectChanges();

      expect(host.querySelector('app-inline-form')).toBeNull();
      expect(submit).not.toHaveBeenCalled();
      const closed = host.querySelector('[data-kind="form-request"]');
      expect(closed?.querySelector('.form-dismissed')?.textContent?.trim()).toBe('已關閉表單，沒有送出任何資料。');
      expect(closed?.querySelector('button.form-start')).toBeNull();
      expect(dismiss).toHaveBeenCalledTimes(1);
      expect(dismiss.mock.calls[0].slice(0, 3)).toEqual([VIEWER, ASSISTANT, 'database-orders']);
      expect(typeof dismiss.mock.calls[0][3]).toBe('string');
      expect(document.activeElement).toBe(host.querySelector('#chat-input'));
      expect(host.querySelector<HTMLInputElement>('#chat-input')?.disabled).toBe(false);
    });

    it('「不用了」 behaves like ×', () => {
      const { fixture, host, dismiss } = setup({ formRequest: true });
      click(host, 'button.form-start');
      fixture.detectChanges();
      buttonByText(host.querySelector('app-inline-form') as HTMLElement, '不用了').click();
      fixture.detectChanges();

      expect(host.querySelector('app-inline-form')).toBeNull();
      expect(host.querySelector('.form-dismissed')).not.toBeNull();
      expect(dismiss).toHaveBeenCalledTimes(1);
    });

    it('confirms before leaving for 我送出的資料 when the card has input, and goes straight there otherwise', async () => {
      const { fixture, host } = setup({ formRequest: true });
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      click(host, 'button.form-start');
      fixture.detectChanges();

      const input = host.querySelector<HTMLInputElement>('#chat-field-field-order-number');
      if (input === null) throw new Error('missing input');
      input.value = 'DEMO-2001';
      input.dispatchEvent(new Event('input'));
      click(host, '.withdraw-hint a');
      fixture.detectChanges();
      await fixture.whenStable();

      const dialog = host.querySelector('[role="dialog"]');
      expect(dialog?.getAttribute('aria-modal')).toBe('true');
      expect(dialog?.textContent).toContain('離開會清除已填的內容');
      expect(document.activeElement?.textContent?.trim()).toBe('繼續填寫');
      expect(navigate).not.toHaveBeenCalled();

      // Back out: the card and its input stay.
      buttonByText(dialog as HTMLElement, '繼續填寫').click();
      fixture.detectChanges();
      expect(host.querySelector('[role="dialog"]')).toBeNull();
      expect(document.activeElement).toBe(host.querySelector('.withdraw-hint a'));

      click(host, '.withdraw-hint a');
      fixture.detectChanges();
      buttonByText(host.querySelector('[role="dialog"]') as HTMLElement, '離開').click();
      fixture.detectChanges();
      expect(navigate).toHaveBeenCalledWith('/app/activity');
      expect(host.querySelector('app-inline-form')).toBeNull();
    });
  });

  describe('③ the 回報資料 entry', () => {
    it('is not shown when the server lists no form', () => {
      const { host } = setup({ forms: [] });
      expect(Array.from(host.querySelectorAll('button')).some((button) => button.textContent?.includes('回報資料'))).toBe(false);
    });

    it('opens the only form directly, left of the input, without adding to the conversation', () => {
      const { fixture, host, dismiss } = setup();
      const composer = host.querySelector('form.composer');
      const trigger = buttonByText(composer as HTMLElement, '回報資料');
      expect(trigger.getAttribute('aria-haspopup')).toBeNull();
      expect(composer?.firstElementChild?.tagName.toLowerCase()).toBe('app-form-entry');
      const messagesBefore = host.querySelectorAll('[role="log"] li').length;

      trigger.click();
      fixture.detectChanges();
      expect(host.querySelector('app-inline-form h2')?.textContent).toContain(orderForm().title);
      expect(host.querySelectorAll('[role="log"] li').length).toBe(messagesBefore);

      // Closing a card the member opened does not count as dismissing a model-offered form.
      buttonByText(host.querySelector('app-inline-form') as HTMLElement, '不用了').click();
      fixture.detectChanges();
      expect(dismiss).not.toHaveBeenCalled();
    });

    it('moves focus into the already open card instead of opening another', () => {
      const { fixture, host } = setup({ formRequest: true });
      click(host, 'button.form-start');
      fixture.detectChanges();
      (document.activeElement as HTMLElement | null)?.blur();

      buttonByText(host.querySelector('form.composer') as HTMLElement, '回報資料').click();
      fixture.detectChanges();
      expect(host.querySelectorAll('app-inline-form')).toHaveLength(1);
      expect(host.querySelector('app-inline-form')?.contains(document.activeElement)).toBe(true);
    });

    it('is a keyboard menu when there are several forms', async () => {
      const { fixture, host } = setup({ forms: [orderForm(), secondForm()] });
      const trigger = buttonByText(host.querySelector('form.composer') as HTMLElement, '回報資料');
      expect(trigger.getAttribute('aria-haspopup')).toBe('menu');
      expect(trigger.getAttribute('aria-expanded')).toBe('false');

      trigger.click();
      fixture.detectChanges();
      await fixture.whenStable();
      const menu = host.querySelector('[role="menu"]');
      expect(trigger.getAttribute('aria-expanded')).toBe('true');
      expect(host.querySelector('.menu-title')?.textContent?.trim()).toBe('選擇要填寫的表單');
      expect(host.querySelector('.menu-note')?.textContent?.trim()).toBe('只列出你目前可以填寫的表單。');
      const items = Array.from(menu?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
      expect(items.map((item) => item.textContent?.trim())).toEqual([orderForm().title, '客戶資料更新']);
      expect(document.activeElement).toBe(items[0]);

      key(document.activeElement, 'ArrowDown');
      expect(document.activeElement).toBe(items[1]);
      key(document.activeElement, 'ArrowDown');
      expect(document.activeElement).toBe(items[0]);
      key(document.activeElement, 'Escape');
      fixture.detectChanges();
      expect(host.querySelector('[role="menu"]')).toBeNull();
      expect(document.activeElement).toBe(trigger);

      key(trigger, 'ArrowUp');
      fixture.detectChanges();
      await fixture.whenStable();
      const reopened = Array.from(host.querySelectorAll<HTMLElement>('[role="menuitem"]'));
      expect(document.activeElement).toBe(reopened[1]);
      reopened[1].click();
      fixture.detectChanges();
      expect(host.querySelector('[role="menu"]')).toBeNull();
      expect(host.querySelector('app-inline-form h2')?.textContent).toContain('客戶資料更新');
    });

    it('sits above the input and opens a bottom sheet dialog on a phone', async () => {
      const { fixture, host } = setup({ forms: [orderForm(), secondForm()], compact: true });
      expect(host.querySelector('form.composer app-form-entry')).toBeNull();
      const trigger = buttonByText(host.querySelector('.entry-row') as HTMLElement, '回報資料');
      expect(trigger.textContent?.trim()).toBe('回報資料');
      expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');

      trigger.click();
      fixture.detectChanges();
      await fixture.whenStable();
      const sheet = host.querySelector('.sheet');
      expect(sheet?.getAttribute('role')).toBe('dialog');
      expect(sheet?.getAttribute('aria-modal')).toBe('true');
      expect(sheet?.querySelector('h2')?.textContent?.trim()).toBe('回報資料');
      expect(sheet?.textContent).toContain('選擇要填寫的表單。只列出你目前可以填寫的表單。');
      expect(sheet?.contains(document.activeElement)).toBe(true);

      buttonByText(sheet as HTMLElement, '關閉').click();
      fixture.detectChanges();
      expect(host.querySelector('.sheet')).toBeNull();
      expect(document.activeElement).toBe(trigger);
    });
  });
});
