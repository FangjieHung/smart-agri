import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom, throwError } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockChatProposedCases, resetMockChatProposedCasesForTest } from '../../../core/repositories/mock-chat-cases';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { ChatConversationComponent } from './chat-conversation.component';

/*
 * 對話中的開案提議卡片（issue #254）：類型、承辦組與時限；標題與說明可以修改；「建立案件」先開共用的
 * 確認視窗，確認後顯示案件連結；「不用了」不建立案件；類型移除後顯示「無法建立」。
 */
const ASSISTANT = 'assistant-customer-service';
const VIEWER = 'account-internal-employee';
const QUESTION = '二號冷藏庫溫度降不下來，需要報修';

async function setup(options: { readonly proposals?: number; readonly addType?: boolean } = {}) {
  const testing = provideAssistantUseTesting(VIEWER);
  if (options.addType !== false) {
    // 擁有者（管理者）加入可提議的類型。
    testing.activeAccountId.set('account-smb-admin');
    await firstValueFrom(testing.repository.setAssistantCaseType(ASSISTANT, 'case-type-equipment-repair', true));
    testing.activeAccountId.set(VIEWER);
  }
  let threadId: string | undefined;
  for (let index = 0; index < (options.proposals ?? 1); index += 1) {
    const asked = testing.repository.sendChatMessage(VIEWER, ASSISTANT, index === 0 ? QUESTION : `溫室風扇停了，需要報修 ${index}`, threadId);
    if (asked.status !== 'ready') throw new Error('expected a reply');
    threadId = asked.data.threadId ?? undefined;
  }
  TestBed.configureTestingModule({ imports: [ChatConversationComponent], providers: [provideRouter([]), ...testing.providers] });
  const fixture: ComponentFixture<ChatConversationComponent> = TestBed.createComponent(ChatConversationComponent);
  fixture.componentRef.setInput('assistantId', ASSISTANT);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository, testing };
}

function required<T extends Element>(element: T | null, what: string): T {
  if (element === null) throw new Error(`missing ${what}`);
  return element;
}

function card(host: HTMLElement): HTMLElement {
  return required(host.querySelector<HTMLElement>('app-case-proposal-card'), 'the proposal card');
}

function buttonByText(host: ParentNode, text: string): HTMLButtonElement {
  const button = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((candidate) => candidate.textContent?.trim() === text);
  if (button === undefined) throw new Error(`missing button ${text}`);
  return button;
}

function type(element: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

async function settle(fixture: ComponentFixture<ChatConversationComponent>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

afterEach(() => {
  document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove());
  resetMockChatProposedCasesForTest();
});

describe('ChatConversationComponent case proposals (#254)', () => {
  it('shows the type, group and handling time with an editable title and description', async () => {
    const { host } = await setup();

    const shown = card(host);
    expect(shown.textContent).toContain('設備故障報修');
    expect(shown.textContent).toContain('設備組');
    expect(shown.textContent).toContain('3 天');
    expect(host.querySelector('[data-kind="case-proposal"] .kind')?.textContent).toContain('建議開案');
    expect(shown.querySelector<HTMLInputElement>('input[type="text"]')?.value).toBe(QUESTION);
    expect(shown.querySelector('textarea')?.value).toBe('');
    // 提議不是回答，不提供轉給專人。
    expect(host.querySelector('.handoff-trigger')).toBeNull();
  });

  it('confirms the edited title and description in the shared dialog and then links the case', async () => {
    const { fixture, host, repository } = await setup();
    const confirm = vi.spyOn(repository, 'confirmChatCaseProposal');
    const proposal = card(host);
    type(required(proposal.querySelector<HTMLInputElement>('input[type="text"]'), 'title'), '  二號冷藏庫溫度異常 ');
    type(required(proposal.querySelector('textarea'), 'description'), '請今天派人檢查壓縮機。');
    buttonByText(proposal, '建立案件').click();
    await settle(fixture);

    const dialog = host.querySelector<HTMLElement>('[role="dialog"], dialog');
    expect(dialog?.textContent).toContain('建立這件案件？');
    expect(dialog?.textContent).toContain('二號冷藏庫溫度異常');
    expect(dialog?.textContent).toContain('請今天派人檢查壓縮機。');
    expect(dialog?.textContent).toContain('設備組');
    expect(confirm).not.toHaveBeenCalled();

    required(host.querySelector<HTMLButtonElement>('.confirm-case-proposal'), 'the dialog confirm').click();
    await settle(fixture);

    expect(confirm).toHaveBeenCalledWith(VIEWER, ASSISTANT, expect.any(String), {
      title: '二號冷藏庫溫度異常', description: '請今天派人檢查壓縮機。',
    });
    const created = mockChatProposedCases()[0];
    expect(created).toMatchObject({ title: '二號冷藏庫溫度異常', description: '請今天派人檢查壓縮機。' });
    const link = host.querySelector<HTMLAnchorElement>('app-case-proposal-card a');
    expect(link?.textContent).toContain('查看案件');
    expect(link?.getAttribute('href')).toBe(`/app/cases?case=${created?.id}`);
    expect(host.querySelector('app-case-proposal-card')?.textContent).toContain('已建立案件「二號冷藏庫溫度異常」');
    expect(host.querySelector('[role="dialog"], dialog')).toBeNull();
  });

  it('cancelling the dialog creates nothing; a blank title is shown on its field', async () => {
    const { fixture, host } = await setup();
    const proposal = card(host);
    buttonByText(proposal, '建立案件').click();
    await settle(fixture);
    buttonByText(host, '取消').click();
    await settle(fixture);
    expect(mockChatProposedCases()).toHaveLength(0);

    type(required(proposal.querySelector<HTMLInputElement>('input[type="text"]'), 'title'), '   ');
    buttonByText(proposal, '建立案件').click();
    await settle(fixture);
    required(host.querySelector<HTMLButtonElement>('.confirm-case-proposal'), 'the dialog confirm').click();
    await settle(fixture);
    const title = host.querySelector<HTMLInputElement>('app-case-proposal-card input[type="text"]');
    expect(title?.getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector('app-case-proposal-card .field-error')?.textContent).toContain('請輸入案件標題。');
    expect(mockChatProposedCases()).toHaveLength(0);
  });

  it('「不用了」 records it without a case', async () => {
    const { fixture, host, repository } = await setup();
    const dismiss = vi.spyOn(repository, 'dismissChatCaseProposal');
    buttonByText(card(host), '不用了').click();
    await settle(fixture);

    expect(dismiss).toHaveBeenCalledOnce();
    expect(host.querySelector('app-case-proposal-card')?.textContent).toContain('已選擇不用了，沒有建立案件。');
    expect(host.querySelector('app-case-proposal-card button')).toBeNull();
    expect(mockChatProposedCases()).toHaveLength(0);
  });

  it('shows 「無法建立」 once the type is no longer proposable', async () => {
    const testing = provideAssistantUseTesting('account-smb-admin');
    await firstValueFrom(testing.repository.setAssistantCaseType(ASSISTANT, 'case-type-equipment-repair', true));
    testing.activeAccountId.set(VIEWER);
    if (testing.repository.sendChatMessage(VIEWER, ASSISTANT, QUESTION).status !== 'ready') throw new Error('expected a reply');
    testing.activeAccountId.set('account-smb-admin');
    await firstValueFrom(testing.repository.setAssistantCaseType(ASSISTANT, 'case-type-equipment-repair', false));
    testing.activeAccountId.set(VIEWER);

    TestBed.configureTestingModule({ imports: [ChatConversationComponent], providers: [provideRouter([]), ...testing.providers] });
    const fixture = TestBed.createComponent(ChatConversationComponent);
    fixture.componentRef.setInput('assistantId', ASSISTANT);
    await settle(fixture);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('app-case-proposal-card')?.textContent).toContain('無法建立');
    expect(host.querySelector('app-case-proposal-card button')).toBeNull();
  });

  it('keeps a failed confirmation retryable', async () => {
    const { fixture, host, repository } = await setup();
    vi.spyOn(repository, 'confirmChatCaseProposal').mockReturnValue(throwError(() => new Error('offline')));
    buttonByText(card(host), '建立案件').click();
    await settle(fixture);
    required(host.querySelector<HTMLButtonElement>('.confirm-case-proposal'), 'the dialog confirm').click();
    await settle(fixture);
    expect(host.querySelector('app-case-proposal-card [role="alert"]')?.textContent).toContain('案件還沒有建立');
    expect(buttonByText(card(host), '建立案件').disabled).toBe(false);
    expect(mockChatProposedCases()).toHaveLength(0);
  });
});
