import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { ChatThreadSummaryView } from '../../../core/domain/conversation.model';
import { ConversationRailComponent } from './conversation-rail.component';

const THREADS: readonly ChatThreadSummaryView[] = [
  { id: 'chat-thread-2', title: '退貨問題', messageCount: 4, updatedAt: '2026-09-22T02:00:02.000Z' },
  { id: 'chat-thread-1', title: '保養方式', messageCount: 2, updatedAt: '2026-09-22T02:00:00.000Z' },
];

function setup(overrides: Partial<Record<string, unknown>> = {}) {
  TestBed.configureTestingModule({ imports: [ConversationRailComponent] });
  const fixture: ComponentFixture<ConversationRailComponent> =
    TestBed.createComponent(ConversationRailComponent);
  fixture.componentRef.setInput('threads', THREADS);
  fixture.componentRef.setInput('activeThreadId', 'chat-thread-2');
  fixture.componentRef.setInput('historyMode', 'saved');
  fixture.componentRef.setInput('historyNotice', '');
  for (const [key, value] of Object.entries(overrides)) {
    fixture.componentRef.setInput(key, value);
  }
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('ConversationRailComponent', () => {
  afterEach(() => document.body.querySelectorAll('app-conversation-rail').forEach((node) => node.remove()));

  it('shows compact recent chats with assistant names and opens the selected thread', () => {
    const recent = [{ ...THREADS[0], assistantId: 'assistant-customer-service' as const, assistantName: '客服助理' }];
    const { fixture, host } = setup({ recentThreads: recent });
    const selected: string[] = [];
    fixture.componentInstance.selectRecentThread.subscribe((thread) => selected.push(thread.id));

    expect(host.querySelector('.recent-chats h2')?.textContent).toBe('Chats');
    expect(host.querySelector('.recent-chats')?.textContent).toContain('客服助理');
    expect(host.querySelector('.thread-rename')).toBeNull();
    host.querySelector<HTMLButtonElement>('.recent-chats button')?.click();
    expect(selected).toEqual(['chat-thread-2']);
  });

  it('renders a labelled list of threads with the open one marked as current', () => {
    const { host } = setup();

    const list = host.querySelector('ul.thread-list');
    expect(list).not.toBeNull();
    expect(list?.getAttribute('aria-labelledby')).toBe(
      host.querySelector('.rail-title')?.getAttribute('id'),
    );
    const items = host.querySelectorAll('ul.thread-list > li');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('退貨問題');
    expect(host.querySelector('button.thread-open[aria-current="true"]')?.textContent).toContain(
      '退貨問題',
    );
  });

  it('emits selection and a new conversation request', () => {
    const { fixture, host } = setup();
    const selected: string[] = [];
    let created = 0;
    fixture.componentInstance.selectThread.subscribe((id: string) => selected.push(id));
    fixture.componentInstance.newConversation.subscribe(() => (created += 1));

    host.querySelectorAll<HTMLButtonElement>('button.thread-open')[1].click();
    host.querySelector<HTMLButtonElement>('button.new-conversation')?.click();

    expect(selected).toEqual(['chat-thread-1']);
    expect(created).toBe(1);
  });

  it('renames a thread from the keyboard and cancels with Escape', async () => {
    const { fixture, host } = setup();
    const renamed: { id: string; title: string }[] = [];
    fixture.componentInstance.renameThread.subscribe((value: { id: string; title: string }) =>
      renamed.push(value),
    );

    host.querySelectorAll<HTMLButtonElement>('button.thread-rename')[0].click();
    fixture.detectChanges();
    await fixture.whenStable();

    const input = host.querySelector<HTMLInputElement>('input.rename-input');
    if (input === null) throw new Error('missing rename input');
    expect(document.activeElement).toBe(input);
    input.value = '退貨與退款';
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLFormElement>('form.rename-form')?.dispatchEvent(
      new Event('submit', { cancelable: true }),
    );
    fixture.detectChanges();

    expect(renamed).toEqual([{ id: 'chat-thread-2', title: '退貨與退款' }]);
    expect(host.querySelector('input.rename-input')).toBeNull();

    host.querySelectorAll<HTMLButtonElement>('button.thread-rename')[0].click();
    fixture.detectChanges();
    host
      .querySelector('input.rename-input')
      ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(host.querySelector('input.rename-input')).toBeNull();
  });

  it('asks for confirmation before deleting and traps focus in the dialog', async () => {
    const { fixture, host } = setup();
    const deleted: string[] = [];
    fixture.componentInstance.deleteThread.subscribe((id: string) => deleted.push(id));

    const trigger = host.querySelectorAll<HTMLButtonElement>('button.thread-delete')[0];
    trigger.focus();
    trigger.click();
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    expect(dialog?.textContent).toContain('退貨問題');
    expect(host.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThan(0);
    expect(document.activeElement).toBe(host.querySelector('button.confirm-cancel'));
    expect(deleted).toEqual([]);

    dialog?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(deleted).toEqual([]);

    host.querySelectorAll<HTMLButtonElement>('button.thread-delete')[0].click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.querySelector<HTMLButtonElement>('button.confirm-delete')?.click();
    fixture.detectChanges();

    expect(deleted).toEqual(['chat-thread-2']);
    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });

  it('explains why there is no history instead of showing an empty list', () => {
    const { host } = setup({
      threads: [],
      activeThreadId: null,
      historyMode: 'not-saved',
      historyNotice: '這個助理設定為不保存對話紀錄。',
    });

    expect(host.querySelector('ul.thread-list')).toBeNull();
    expect(host.querySelector('[data-state="history-off"]')?.textContent).toContain('不保存對話紀錄');
    expect(host.querySelector('button.new-conversation')).toBeNull();
  });

  it('shows an empty state when history is on but nothing has been asked yet', () => {
    const { host } = setup({ threads: [], activeThreadId: null });

    expect(host.querySelector('ul.thread-list')).toBeNull();
    expect(host.querySelector('.rail-empty')?.textContent).not.toBe('');
    expect(host.querySelector('button.new-conversation')).not.toBeNull();
  });
});
