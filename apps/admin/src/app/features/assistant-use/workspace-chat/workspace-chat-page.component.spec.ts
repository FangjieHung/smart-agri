import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import type { AssistantConfigurationView } from '../../../core/domain/assistant.model';
import type { DemoKeyValueStorage } from '../../../core/repositories/demo-repository';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { WorkspaceChatPageComponent } from './workspace-chat-page.component';

const EPHEMERAL: AssistantConfigurationView = {
  id: 'assistant-created-1',
  ownerAccountId: 'account-smb-admin',
  name: '不留紀錄助理',
  purpose: '示範關閉保存對話',
  status: 'ready',
  audience: 'members-and-external-customers',
  sharedWithAccountIds: [],
  knowledgeBaseIds: ['knowledge-refund-policy'],
  databaseIds: [],
  keepOwnConversations: false,
};

interface SetupOptions {
  readonly accountId?: AccountId;
  readonly mobile?: boolean;
  readonly seed?: (storage: DemoKeyValueStorage) => void;
}

function setup(
  params: Record<string, string> = { assistantId: 'assistant-customer-service' },
  options: SetupOptions = {},
) {
  const testing = provideAssistantUseTesting(options.accountId ?? 'account-external-customer');
  options.seed?.(testing.storage);
  const subject = new BehaviorSubject(convertToParamMap(params));
  TestBed.configureTestingModule({
    imports: [WorkspaceChatPageComponent],
    providers: [
      provideRouter([]),
      ...testing.providers,
      {
        provide: BreakpointObserver,
        useValue: { observe: () => of({ matches: options.mobile === true, breakpoints: {} }) },
      },
      {
        provide: ActivatedRoute,
        useValue: { paramMap: subject.asObservable(), snapshot: { paramMap: subject.value } },
      },
    ],
  });
  // 這個測試沒有掛真實路由表，導覽一律攔下來只檢查參數。
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture: ComponentFixture<WorkspaceChatPageComponent> =
    TestBed.createComponent(WorkspaceChatPageComponent);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  const setParams = (next: Record<string, string>) => {
    subject.next(convertToParamMap(next));
    fixture.detectChanges();
  };
  return { fixture, host: fixture.nativeElement as HTMLElement, setParams, navigate, ...testing };
}

function ask(fixture: ComponentFixture<WorkspaceChatPageComponent>, text: string): void {
  const host = fixture.nativeElement as HTMLElement;
  const input = host.querySelector<HTMLInputElement>('#chat-input');
  if (input === null) throw new Error('missing composer');
  input.value = text;
  input.dispatchEvent(new Event('input'));
  host.querySelector<HTMLButtonElement>('form.composer button[type="submit"]')?.click();
  fixture.detectChanges();
}

describe('WorkspaceChatPageComponent', () => {
  afterEach(() =>
    document.body.querySelectorAll('app-workspace-chat-page').forEach((node) => node.remove()),
  );

  it('shows the assistant picker when no assistant is selected', () => {
    const { host } = setup({});

    expect(host.querySelector('app-conversation-rail')).toBeNull();
    expect(host.querySelector('.assistant-picker')?.textContent).toContain('客服助理');
  });

  it('shows the history rail beside the conversation and keeps one page heading', () => {
    const { host, fixture } = setup();

    expect(host.querySelectorAll('h1')).toHaveLength(1);
    expect(host.querySelector('h1')?.textContent).toContain('客服助理');
    expect(host.querySelector('app-conversation-rail')).not.toBeNull();
    expect(host.querySelector('app-chat-conversation .chat-header')).toBeNull();

    ask(fixture, '收到商品後幾天內可以退貨？');
    expect(host.querySelectorAll('ul.thread-list > li')).toHaveLength(1);
    expect(host.querySelector('button.thread-open')?.textContent).toContain('退貨');
  });

  it('navigates to the conversation the rail selects and to a newly opened one', () => {
    const { fixture, host, repository, navigate } = setup();

    const first = repository.sendChatMessage(
      'account-external-customer',
      'assistant-customer-service',
      '收到商品後幾天內可以退貨？',
    );
    if (first.status !== 'ready') throw new Error('expected ready');
    ask(fixture, '皮革商品平常要怎麼保養？');

    navigate.mockClear();
    host.querySelector<HTMLButtonElement>('button.thread-open')?.click();
    expect(navigate).toHaveBeenCalledWith([
      '/app/chat',
      'assistant-customer-service',
      first.data.threadId,
    ]);

    navigate.mockClear();
    host.querySelector<HTMLButtonElement>('button.new-conversation')?.click();
    fixture.detectChanges();
    expect(navigate).toHaveBeenCalledTimes(1);
    expect((navigate.mock.calls[0][0] as string[])[2]).not.toBe(first.data.threadId);
  });

  it('announces the conversation it switched to', () => {
    const { fixture, host, setParams } = setup();
    ask(fixture, '收到商品後幾天內可以退貨？');
    const threadId = host.querySelector('button.thread-open')?.getAttribute('data-thread-id') ?? '';
    expect(threadId).not.toBe('');

    setParams({ assistantId: 'assistant-customer-service', conversationId: threadId });

    const status = host.querySelector('[role="status"]');
    expect(status?.getAttribute('aria-live')).toBe('polite');
    expect(status?.textContent).toContain('退貨');
  });

  it('deletes a conversation through the rail confirmation', async () => {
    const { fixture, host } = setup();
    ask(fixture, '收到商品後幾天內可以退貨？');

    host.querySelector<HTMLButtonElement>('button.thread-delete')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    host.querySelector<HTMLButtonElement>('button.confirm-delete')?.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('ul.thread-list > li')).toHaveLength(0);
    expect(host.querySelector('.rail-empty')).not.toBeNull();
  });

  it('explains the missing history for an assistant that does not save conversations', () => {
    const { host } = setup(
      { assistantId: 'assistant-created-1' },
      {
        seed: (storage) =>
          storage.setItem('sme-demo:created-assistants', JSON.stringify([EPHEMERAL])),
      },
    );

    expect(host.querySelector('[data-state="history-off"]')?.textContent).toContain(
      '不保存對話紀錄',
    );
    expect(host.querySelector('button.new-conversation')).toBeNull();
    expect(host.querySelector('ul.thread-list')).toBeNull();
    expect(host.querySelector('#chat-input')).not.toBeNull();
  });

  it('collapses the rail into a toggled sheet on a narrow screen', () => {
    const { host, fixture } = setup({ assistantId: 'assistant-customer-service' }, { mobile: true });

    const toggle = host.querySelector<HTMLButtonElement>('button.rail-toggle');
    expect(toggle).not.toBeNull();
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(toggle?.getAttribute('aria-controls')).toBe('conversation-rail');
    expect(host.querySelector('#conversation-rail')?.hasAttribute('hidden')).toBe(true);

    toggle?.click();
    fixture.detectChanges();
    expect(host.querySelector('button.rail-toggle')?.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('#conversation-rail')?.hasAttribute('hidden')).toBe(false);
  });

  it('shows the shared permission message for an assistant the account cannot use', () => {
    const { host } = setup({ assistantId: 'assistant-internal-onboarding' });

    expect(host.textContent).toContain('無法使用這個助理');
    expect(host.textContent).not.toContain('內部教育訓練助理');
    expect(host.querySelector('app-conversation-rail')).toBeNull();
  });
});
