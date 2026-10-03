import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { syncValue } from '../../../core/repositories/sync-value.testing';
import { provideRouter } from '@angular/router';
import type { ChatViewerId } from '../../../core/domain/account.model';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { AssistantIssuesRepository } from '../../../core/repositories/assistant-issues.repository';
import { firstValueFrom, of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import { ChatConversationComponent } from './chat-conversation.component';

const ASSISTANT = 'assistant-customer-service';
const ANSWERS = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

/** 先以 repository 送出一筆已同意的紀錄，再開啟畫面，讓測試直接停在收據上。 */
function setup(anonymous = false, question?: string) {
  const testing = provideAssistantUseTesting(anonymous ? null : 'account-external-customer');
  const viewerId: ChatViewerId = anonymous
    ? (testing.visitor.visitorId() as ChatViewerId)
    : 'account-external-customer';
  const submitted = syncValue(testing.repository.submitChatForm(viewerId, ASSISTANT, {
    formId: 'database-orders', formVersion: 1, submissionId: crypto.randomUUID(),
    answers: ANSWERS,
    consent: true,
  }));
  if (submitted.status !== 'ready') throw new Error(`expected ready, got ${submitted.status}`);
  if (question) {
    const answered = testing.repository.sendChatMessage(viewerId, ASSISTANT, question);
    if (answered.status !== 'ready') throw new Error(`expected reply, got ${answered.status}`);
  }

  TestBed.configureTestingModule({
    imports: [ChatConversationComponent],
    providers: [provideRouter([]), ...testing.providers],
  });
  const fixture: ComponentFixture<ChatConversationComponent> =
    TestBed.createComponent(ChatConversationComponent);
  fixture.componentRef.setInput('assistantId', ASSISTANT);
  if (anonymous) fixture.componentRef.setInput('allowAnonymous', true);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);

  return { fixture, host: fixture.nativeElement as HTMLElement, viewerId, ...testing };
}

function click(host: HTMLElement, selector: string): void {
  const button = host.querySelector<HTMLButtonElement>(selector);
  if (button === null) throw new Error(`missing ${selector}`);
  button.focus();
  button.click();
}

function trackingOf(repository: ReturnType<typeof setup>['repository'], subjectId: string) {
  const result = repository.readDatabaseTracking('account-smb-admin', 'database-orders');
  if (result.status !== 'ready') throw new Error('expected ready');
  return result.data.subjects.find((subject) => subject.id === subjectId);
}

describe('ChatConversationComponent withdrawal', () => {
  afterEach(() =>
    document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()),
  );

  it('offers withdrawal on the submission receipt and explains what it leaves behind', () => {
    const { host } = setup();
    const receipt = host.querySelector('[data-kind="submission-receipt"]');

    expect(receipt?.textContent).toContain('DEMO-2001');
    expect(receipt?.querySelector('.withdrawal-notice')?.textContent).toContain('撤回');
    expect(receipt?.querySelector('button.withdraw')?.textContent).toContain('撤回');
  });

  it('asks for confirmation, traps focus and returns it when the user backs out', async () => {
    const { fixture, host } = setup();
    click(host, 'button.withdraw');
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    expect(dialog?.getAttribute('aria-labelledby')).not.toBeNull();
    expect(document.activeElement).toBe(host.querySelector('button.confirm-cancel'));

    click(host, 'button.confirm-cancel');
    fixture.detectChanges();

    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(host.querySelector('button.withdraw'));
  });

  it('withdraws the record, updates the receipt and drops it from the collection records', async () => {
    const { fixture, host, repository } = setup();
    expect(trackingOf(repository, 'subject-account-external-customer')?.records).toHaveLength(1);

    click(host, 'button.withdraw');
    fixture.detectChanges();
    await fixture.whenStable();
    click(host, 'button.confirm-withdraw');
    fixture.detectChanges();

    const receipt = host.querySelector('[data-kind="submission-receipt"]');
    expect(receipt?.querySelector('button.withdraw')).toBeNull();
    expect(receipt?.querySelector('.withdrawal-notice')?.textContent).toContain('撤回');
    expect(host.querySelector('.withdraw-feedback')?.getAttribute('role')).toBe('status');

    const subject = trackingOf(repository, 'subject-account-external-customer');
    expect(subject?.records).toEqual([]);
    expect(subject?.withdrawals).toHaveLength(1);
  });

  it('warns the anonymous visitor that the record is only reachable in this tab session', async () => {
    const { fixture, host, repository, viewerId } = setup(true);
    expect(host.querySelector('.withdrawal-notice')?.textContent).toContain('分頁');

    click(host, 'button.withdraw');
    fixture.detectChanges();
    await fixture.whenStable();
    click(host, 'button.confirm-withdraw');
    fixture.detectChanges();

    expect(host.querySelector('button.withdraw')).toBeNull();
    expect(trackingOf(repository, `subject-${viewerId}`)?.withdrawals).toHaveLength(1);
  });
});

describe('ChatConversationComponent handoff', () => {
  afterEach(() =>
    document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()),
  );

  it('previews one exchange and sends it only after explicit confirmation', async () => {
    const { fixture, host } = setup(false, '收到商品後七天內可申請退貨嗎？');
    const repository = TestBed.inject(AssistantIssuesRepository);
    click(host, 'button.handoff-trigger');
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog?.textContent).toContain('分享的問題');
    expect(dialog?.textContent).toContain('分享的回覆');
    expect(dialog?.textContent).toContain('助理擁有者或被指派的處理人');
    expect((await firstValueFrom(repository.list({ scope: 'forwarded' }))).status).toBe('ready');

    click(host, 'button.confirm-cancel');
    fixture.detectChanges();
    expect(host.querySelector('[role="dialog"]')).toBeNull();
    const before = await firstValueFrom(repository.list({ scope: 'forwarded' }));
    expect(before).toMatchObject({ status: 'ready', data: [] });

    click(host, 'button.handoff-trigger');
    fixture.detectChanges();
    click(host, 'button.confirm-handoff');
    fixture.detectChanges();
    expect(host.querySelector('.handoff-feedback')?.textContent).toContain('已轉交');
    const after = await firstValueFrom(repository.list({ scope: 'forwarded' }));
    expect(after).toMatchObject({ status: 'ready', data: [{ source: 'handoff' }] });
  });

  it('does not offer handoff to an anonymous visitor', () => {
    const { host } = setup(true, '收到商品後七天內可申請退貨嗎？');
    expect(host.querySelector('button.handoff-trigger')).toBeNull();
  });

  it('prevents duplicate handoff submission while the first request is pending', async () => {
    const { fixture, host } = setup(false, '收到商品後七天內可申請退貨嗎？');
    const issues = TestBed.inject(AssistantIssuesRepository);
    const pending = new Subject<ReturnType<typeof issues.createHandoff> extends import('rxjs').Observable<infer T> ? T : never>();
    const create = vi.spyOn(issues, 'createHandoff').mockReturnValue(pending);
    click(host, 'button.handoff-trigger');
    fixture.detectChanges();
    click(host, 'button.confirm-handoff');
    fixture.detectChanges();
    expect(host.querySelector<HTMLButtonElement>('button.confirm-handoff')?.disabled).toBe(true);
    click(host, 'button.confirm-handoff');
    expect(create).toHaveBeenCalledTimes(1);
    pending.complete();
  });
});

describe('ChatConversationComponent form request (#148)', () => {
  afterEach(() =>
    document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()),
  );

  /** 開啟一段已經有表單請求的對話，填好並走到同意畫面。 */
  function toConsent() {
    const testing = provideAssistantUseTesting('account-external-customer');
    const asked = testing.repository.sendChatMessage('account-external-customer', ASSISTANT, '我要回報訂單問題');
    if (asked.status !== 'ready') throw new Error('expected a form request');
    TestBed.configureTestingModule({
      imports: [ChatConversationComponent],
      providers: [provideRouter([]), ...testing.providers],
    });
    const fixture = TestBed.createComponent(ChatConversationComponent);
    fixture.componentRef.setInput('assistantId', ASSISTANT);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);

    vi.spyOn(testing.repository, 'reviewChatForm').mockReturnValue(
      of({ status: 'ready', data: { formId: 'database-orders', saved: false, entries: [] } }),
    );
    click(host, 'button.form-start');
    fixture.detectChanges();
    click(host, 'app-inline-form button[type="submit"]');
    fixture.detectChanges();
    click(host, '#consent-agree');
    fixture.detectChanges();
    return { fixture, host, repository: testing.repository };
  }

  it('keeps the consent step after a failed submission and retries with the same submission id', () => {
    const { fixture, host, repository } = toConsent();
    const submit = vi.spyOn(repository, 'submitChatForm')
      .mockReturnValueOnce(throwError(() => new Error('offline')));

    click(host, 'button.consent-submit');
    fixture.detectChanges();
    expect(host.querySelector('app-consent-confirmation .error')?.textContent).toContain('不會重複建立紀錄');

    submit.mockReturnValueOnce(of({
      status: 'permission-denied',
      reason: 'assistant-form',
      message: '這份表單目前無法使用：助理已不再連接這個資料庫，或你沒有填寫它的權限。',
    }));
    click(host, 'button.consent-submit');
    fixture.detectChanges();
    expect(host.querySelector('app-consent-confirmation .error')?.textContent).toContain('目前無法使用');

    const keys = submit.mock.calls.map((call) => call[2].submissionId);
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('ends the fill when the form changed, without submitting anything', () => {
    const { fixture, host, repository } = toConsent();
    vi.spyOn(repository, 'submitChatForm').mockReturnValue(of({
      status: 'conflict',
      reason: 'form-version-changed',
      message: '這份表單已更新，請重新載入最新的表單後再填寫。你這次填寫的內容尚未送出。',
    }));

    click(host, 'button.consent-submit');
    fixture.detectChanges();

    expect(host.querySelector('app-consent-confirmation')).toBeNull();
    expect(host.querySelector('.form-notice')?.textContent).toContain('尚未送出');
  });

  it('cancelling the form records nothing', () => {
    const testing = provideAssistantUseTesting('account-external-customer');
    testing.repository.sendChatMessage('account-external-customer', ASSISTANT, '我要回報訂單問題');
    TestBed.configureTestingModule({
      imports: [ChatConversationComponent],
      providers: [provideRouter([]), ...testing.providers],
    });
    const fixture = TestBed.createComponent(ChatConversationComponent);
    fixture.componentRef.setInput('assistantId', ASSISTANT);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const submit = vi.spyOn(testing.repository, 'submitChatForm');

    click(host, 'button.form-start');
    fixture.detectChanges();
    click(host, 'app-inline-form button.secondary');
    fixture.detectChanges();

    expect(host.querySelector('app-inline-form')).toBeNull();
    expect(submit).not.toHaveBeenCalled();
    expect(trackingOf(testing.repository, 'subject-account-external-customer')).toBeUndefined();
  });
});
