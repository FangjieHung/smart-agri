import { TestBed } from '@angular/core/testing';
import { RecentChatsComponent, type RecentChatThreadView } from './recent-chats.component';

const RECENT: readonly RecentChatThreadView[] = [
  {
    id: 'chat-thread-2',
    title: '退貨問題',
    messageCount: 4,
    updatedAt: '2026-09-22T02:00:02.000Z',
    assistantId: 'assistant-customer-service',
    assistantName: '客服助理',
  },
];

function setup(threads: readonly RecentChatThreadView[]) {
  TestBed.configureTestingModule({ imports: [RecentChatsComponent] });
  const fixture = TestBed.createComponent(RecentChatsComponent);
  fixture.componentRef.setInput('threads', threads);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('RecentChatsComponent', () => {
  it('shows compact recent chats with assistant names and opens the selected thread', () => {
    const { fixture, host } = setup(RECENT);
    const selected: string[] = [];
    fixture.componentInstance.selectThread.subscribe((thread) => selected.push(thread.id));

    expect(host.querySelector('.recent-chats h2')?.textContent).toBe('Chats');
    expect(host.querySelector('.recent-chats')?.getAttribute('aria-labelledby')).toBe('recent-chats-title');
    expect(host.querySelector('.recent-chats')?.textContent).toContain('客服助理');
    expect(host.querySelector('.thread-rename')).toBeNull();
    host.querySelector<HTMLButtonElement>('.recent-chats button')?.click();
    expect(selected).toEqual(['chat-thread-2']);
  });

  it('says there is no history yet when the list is empty', () => {
    const { host } = setup([]);

    expect(host.querySelector('.recent-chats ul')).toBeNull();
    expect(host.querySelector('.recent-chats p')?.textContent).toBe('尚無對話紀錄');
  });
});
