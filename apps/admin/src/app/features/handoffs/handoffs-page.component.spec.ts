import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of, throwError, type Observable } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../core/domain/account.model';
import type { NotificationSettingsView, VisitorHandoffStatusFilter, VisitorHandoffView } from '../../core/domain/handoff.model';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { HandoffsRepository } from '../../core/repositories/handoffs.repository';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { NAV_COUNTS_CHANGED_EVENT } from '../../layout/workspace-layout/nav-counts';
import { HandoffsPageComponent } from './handoffs-page.component';

const line: VisitorHandoffView = {
  id: 'handoff-1', assistantId: 'assistant-1', assistantName: '客服助理', channel: 'line',
  createdAt: '2026-10-13T02:10:00Z', handledAt: null, handledByAccountId: null, handledByName: null,
};
const website: VisitorHandoffView = { ...line, id: 'handoff-2', channel: 'website', createdAt: '2026-10-12T09:30:00Z' };

type List = (status: VisitorHandoffStatusFilter) => Observable<RepositoryView<readonly VisitorHandoffView[]>>;

type Settings = () => Observable<RepositoryView<NotificationSettingsView>>;

/** 預設已填 Email：只有測提醒的案例才改成別的設定。 */
const withEmail: Settings = () => of({ status: 'ready', data: { email: 'owner@shop.example.org', handoffEmails: true, emailDeliveryConfigured: true } });

async function render(
  list: List,
  markHandled = vi.fn(() => of<RepositoryView<VisitorHandoffView>>({ status: 'ready', data: line })),
  settle = true,
  notificationSettings: Settings = withEmail,
  account: AccountId = 'account-smb-admin',
) {
  TestBed.resetTestingModule();
  const listSpy = vi.fn(list);
  const settingsSpy = vi.fn(notificationSettings);
  await TestBed.configureTestingModule({
    imports: [HandoffsPageComponent],
    providers: [
      provideRouter([]),
      { provide: HandoffsRepository, useValue: { list: listSpy, markHandled, notificationSettings: settingsSpy } },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(account) } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(HandoffsPageComponent);
  fixture.detectChanges();
  if (settle) await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, list: listSpy, markHandled, settings: settingsSpy };
}

const settingsView = (email: string | null, handoffEmails: boolean, emailDeliveryConfigured = true): Settings =>
  () => of({ status: 'ready', data: { email, handoffEmails, emailDeliveryConfigured } });

describe('HandoffsPageComponent (issue #355)', () => {
  afterEach(() => vi.restoreAllMocks());

  it('shows loading, denied, error and empty states', async () => {
    expect((await render(() => NEVER, undefined, false)).page.textContent).toContain('正在載入轉真人通知');
    expect((await render(() => of({ status: 'permission-denied', reason: 'handoff', message: '只給助理的擁有者' }))).page.textContent).toContain('只給助理的擁有者');
    expect((await render(() => throwError(() => new Error('offline')))).page.textContent).toContain('目前無法載入轉真人通知');
    expect((await render(() => of({ status: 'ready', data: [] }))).page.textContent).toContain('目前沒有待處理的轉真人');
  });

  it('lists the assistant, channel and time, with where to answer and a link to the channel settings', async () => {
    const { page, list } = await render(() => of({ status: 'ready', data: [line, website] }));

    expect(list).toHaveBeenCalledWith('pending');
    const items = Array.from(page.querySelectorAll('[data-handoff]'));
    expect(items).toHaveLength(2);
    expect(items[0].querySelector('[data-channel]')?.textContent?.trim()).toBe('LINE');
    expect(items[0].textContent).toContain('客服助理');
    expect(items[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-10-13T02:10:00Z');
    expect(items[0].textContent).toContain('LINE Official Account Manager');
    expect(items[0].querySelector('a[href="https://chat.line.biz/"]')?.getAttribute('rel')).toBe('noopener noreferrer');
    expect(items[0].querySelector('a[href^="/app/assistants/assistant-1/publishing"]')?.getAttribute('href')).toBe('/app/assistants/assistant-1/publishing?channel=line');
    expect(items[1].querySelector('a[href="https://chat.line.biz/"]')).toBeNull();
    expect(items[1].textContent).toContain('聯絡方式');
    expect(items[0].querySelector('button')?.getAttribute('aria-label')).toBe('將「客服助理」LINE 的轉真人標記為已處理');
  });

  it('marks one handled, says so, reloads the list and asks the shell to refresh its count', async () => {
    const dispatched = vi.spyOn(document, 'dispatchEvent');
    const { fixture, page, list, markHandled } = await render(() => of({ status: 'ready', data: [line] }));

    page.querySelector<HTMLButtonElement>('[data-handoff] button')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(markHandled).toHaveBeenCalledWith('handoff-1');
    expect(page.querySelector('[role="status"]')?.textContent).toContain('已將「客服助理」LINE 的轉真人標記為已處理。');
    expect(list).toHaveBeenCalledTimes(2);
    expect(dispatched.mock.calls.some(([event]) => event.type === NAV_COUNTS_CHANGED_EVENT)).toBe(true);
  });

  it('switches to the handled ones and shows who handled them', async () => {
    const handled: VisitorHandoffView = { ...website, handledAt: '2026-10-12T10:00:00Z', handledByAccountId: 'account-1', handledByName: '安心商行管理者' };
    const { fixture, page, list } = await render((status) => of({ status: 'ready', data: status === 'handled' ? [handled] : [] }));

    page.querySelector<HTMLButtonElement>('[data-handoff-filter="handled"]')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(list).toHaveBeenLastCalledWith('handled');
    expect(page.querySelector('[data-handoff-filter="handled"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(page.querySelector('[data-handoff-handled]')?.textContent).toContain('已處理：安心商行管理者');
    expect(page.querySelector('[data-handoff] button')).toBeNull();
  });

  it('shows the refusal when someone else\'s handoff cannot be marked', async () => {
    const refused = vi.fn(() => of<RepositoryView<VisitorHandoffView>>({ status: 'permission-denied', reason: 'handoff', message: '你沒有這則轉真人通知的存取權限' }));
    const { fixture, page } = await render(() => of({ status: 'ready', data: [line] }), refused);

    page.querySelector<HTMLButtonElement>('[data-handoff] button')?.click();
    fixture.detectChanges();

    expect(page.querySelector('[role="alert"]')?.textContent).toContain('你沒有這則轉真人通知的存取權限');
  });

  describe('notification Email reminder (issue #362)', () => {
    const empty: List = () => of({ status: 'ready', data: [] });

    it('reminds an account without an Email above the list, with a link to 我的通知', async () => {
      const { page, settings } = await render(empty, undefined, true, settingsView(null, true));

      expect(settings).toHaveBeenCalledTimes(1);
      const reminder = page.querySelector('[data-email-reminder]');
      expect(reminder?.textContent).toContain('你還沒有填通知 Email，訪客要找真人時只會在這裡通知你。');
      const link = reminder?.querySelector('a');
      expect(link?.textContent?.trim()).toBe('到「我的通知」填寫 Email');
      expect(link?.getAttribute('href')).toBe('/app/settings#notification-settings');
      // Above the filter and the list.
      const toolbar = page.querySelector('.handoffs-page__toolbar');
      expect(reminder && toolbar ? reminder.compareDocumentPosition(toolbar) & Node.DOCUMENT_POSITION_FOLLOWING : 0).toBeTruthy();
    });

    it('does not remind once the Email is filled, the 轉真人 Email is off, or the deployment cannot send', async () => {
      for (const settings of [settingsView('owner@shop.example.org', true), settingsView(null, false), settingsView(null, true, false)]) {
        const { page } = await render(empty, undefined, true, settings);
        expect(page.querySelector('[data-email-reminder]')).toBeNull();
        expect(page.textContent).toContain('目前沒有待處理的轉真人');
      }
    });

    it('stays quiet when the settings cannot be read, and never asks for an external customer', async () => {
      for (const settings of [
        () => throwError(() => new Error('offline')),
        () => of<RepositoryView<NotificationSettingsView>>({ status: 'permission-denied', reason: 'handoff', message: 'x' }),
      ] satisfies Settings[]) {
        const { page } = await render(empty, undefined, true, settings);
        expect(page.querySelector('[data-email-reminder]')).toBeNull();
        expect(page.textContent).toContain('目前沒有待處理的轉真人');
      }

      // Still loading (never answers): no reminder yet, the list is not held up. Not settled: it never is.
      const loading = await render(empty, undefined, false, () => NEVER);
      expect(loading.page.querySelector('[data-email-reminder]')).toBeNull();
      expect(loading.page.textContent).toContain('目前沒有待處理的轉真人');

      const external = await render(empty, undefined, true, settingsView(null, true), 'account-external-customer');
      expect(external.settings).not.toHaveBeenCalled();
      expect(external.page.querySelector('[data-email-reminder]')).toBeNull();
    });
  });
});
