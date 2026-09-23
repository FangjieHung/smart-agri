import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { ChatShellPageComponent } from './chat-shell-page.component';

function setup(
  assistantId = 'assistant-customer-service',
  accountId: AccountId = 'account-external-customer',
  queryParams: Record<string, string> = {},
) {
  const testing = provideAssistantUseTesting(accountId);
  const params = new BehaviorSubject(convertToParamMap({ assistantId }));
  const query = new BehaviorSubject(convertToParamMap(queryParams));
  TestBed.configureTestingModule({
    imports: [ChatShellPageComponent],
    providers: [
      provideRouter([]),
      ...testing.providers,
      {
        provide: ActivatedRoute,
        useValue: {
          paramMap: params.asObservable(),
          queryParamMap: query.asObservable(),
          snapshot: { paramMap: params.value, queryParamMap: query.value },
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(ChatShellPageComponent);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  return { fixture, host: fixture.nativeElement as HTMLElement, ...testing };
}

function ask(fixture: ComponentFixture<ChatShellPageComponent>, text: string): void {
  const host = fixture.nativeElement as HTMLElement;
  const input = host.querySelector<HTMLInputElement>('#chat-input');
  if (input === null) throw new Error('missing composer');
  input.value = text;
  input.dispatchEvent(new Event('input'));
  host.querySelector<HTMLButtonElement>('form.composer button[type="submit"]')?.click();
  fixture.detectChanges();
}

describe('ChatShellPageComponent', () => {
  afterEach(() => document.body.querySelectorAll('app-chat-shell-page').forEach((node) => node.remove()));

  it('shows the assistant, a privacy notice and a labelled live conversation log', () => {
    const { host } = setup();

    expect(host.querySelector('h1')?.textContent).toContain('客服助理');
    expect(host.querySelector('.privacy-notice')?.textContent).toContain('助理建立者');
    const log = host.querySelector('[role="log"]');
    expect(log?.getAttribute('aria-live')).toBe('polite');
    expect(log?.getAttribute('aria-label')).toBe('對話內容');
    expect(host.querySelector('label[for="chat-input"]')).not.toBeNull();
  });

  it('answers a typed question from fixtures and clears the composer', () => {
    const { fixture, host } = setup();
    ask(fixture, '收到商品後幾天內可以退貨？');

    const messages = host.querySelectorAll('[role="log"] app-chat-message');
    expect(messages).toHaveLength(2);
    expect(messages[1].textContent).toContain('根據你的資料');
    expect(host.querySelector<HTMLInputElement>('#chat-input')?.value).toBe('');
  });

  it('asks a suggested prompt with one tap', () => {
    const { fixture, host } = setup();
    host.querySelector<HTMLButtonElement>('button.suggested-prompt')?.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('[role="log"] app-chat-message')).toHaveLength(2);
  });

  it('opens citations in a dialog, closes with Escape and returns focus to the trigger', async () => {
    const { fixture, host } = setup();
    ask(fixture, '收到商品後幾天內可以退貨？');
    const trigger = host.querySelector<HTMLButtonElement>('button.citation-toggle');
    if (trigger === null) throw new Error('missing citation toggle');
    trigger.focus();
    trigger.click();
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog?.textContent).toContain('退換貨辦法 2026 版.pdf');
    dialog?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(host.querySelector('button.citation-toggle'));
  });

  it('does not show another account’s conversation, even to the assistant owner', () => {
    const customer = setup();
    ask(customer.fixture, '我的私人問題：退貨要幾天？');
    customer.activeAccountId.set('account-smb-admin');
    customer.fixture.detectChanges();

    expect(customer.host.textContent).not.toContain('我的私人問題');
    expect(customer.host.querySelectorAll('[role="log"] app-chat-message')).toHaveLength(0);
  });

  it('runs the inline form through consent before the record is submitted', () => {
    const { fixture, host, repository } = setup();
    ask(fixture, '我要回報訂單問題');
    host.querySelector<HTMLButtonElement>('button.form-start')?.click();
    fixture.detectChanges();

    const orderInput = host.querySelector<HTMLInputElement>('#chat-field-field-order-number');
    if (orderInput === null) throw new Error('missing inline form');
    orderInput.value = 'DEMO-2001';
    orderInput.dispatchEvent(new Event('input'));
    host.querySelector<HTMLInputElement>('input[type="radio"][value="配送延遲"]')?.click();
    const dateInput = host.querySelector<HTMLInputElement>('#chat-field-field-reported-on');
    if (dateInput === null) throw new Error('missing date');
    dateInput.value = '2026-09-21';
    dateInput.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('app-inline-form button[type="submit"]')?.click();
    fixture.detectChanges();

    expect(host.querySelector('app-consent-confirmation')?.textContent).toContain('接收單位');
    expect(host.querySelector<HTMLButtonElement>('button.consent-submit')?.disabled).toBe(true);
    host.querySelector<HTMLInputElement>('#consent-agree')?.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('button.consent-submit')?.click();
    fixture.detectChanges();

    expect(host.querySelector('app-consent-confirmation')).toBeNull();
    expect(host.querySelector('[data-kind="submission-receipt"]')?.textContent).toContain('已送出');
    const tracking = repository.getDatabaseTracking('account-smb-admin', 'database-orders');
    expect(tracking).toMatchObject({ status: 'ready', data: { subjects: [{ displayName: '外部客戶' }] } });
  });

  it('stays single column without a conversation history rail', () => {
    const { host } = setup();

    expect(host.querySelector('app-conversation-rail')).toBeNull();
    expect(host.querySelector('.chat-header')).not.toBeNull();
  });

  it('hides the page chrome but keeps the conversation when embedded', () => {
    const { fixture, host } = setup('assistant-customer-service', 'account-external-customer', {
      embed: '1',
    });

    expect(host.querySelector('.chat-header')).toBeNull();
    expect(host.querySelector('a[href="/app/home"]')).toBeNull();
    expect(host.querySelector('h1')?.className).toContain('visually-hidden');
    expect(host.querySelector('.privacy-notice')).not.toBeNull();

    ask(fixture, '收到商品後幾天內可以退貨？');
    expect(host.querySelectorAll('[role="log"] app-chat-message')).toHaveLength(2);
  });

  it('shows the same permission message for an assistant the account cannot use', () => {
    const { host } = setup('assistant-internal-onboarding');

    expect(host.textContent).toContain('無法使用這個助理');
    expect(host.textContent).not.toContain('內部教育訓練助理');
    expect(host.querySelector('#chat-input')).toBeNull();
  });
});
