import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { ChatViewerId } from '../../../core/domain/account.model';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { ChatConversationComponent } from './chat-conversation.component';

const ASSISTANT = 'assistant-customer-service';
const ANSWERS = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

/** 先以 repository 送出一筆已同意的紀錄，再開啟畫面，讓測試直接停在收據上。 */
function setup(anonymous = false) {
  const testing = provideAssistantUseTesting(anonymous ? null : 'account-external-customer');
  const viewerId: ChatViewerId = anonymous
    ? (testing.visitor.visitorId() as ChatViewerId)
    : 'account-external-customer';
  const submitted = testing.repository.submitChatForm(viewerId, ASSISTANT, {
    formId: 'database-orders',
    answers: ANSWERS,
    consent: true,
  });
  if (submitted.status !== 'ready') throw new Error(`expected ready, got ${submitted.status}`);

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
  const result = repository.getDatabaseTracking('account-smb-admin', 'database-orders');
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
