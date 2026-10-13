import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { NOTIFICATION_EMAIL_INVALID_MESSAGE, type NotificationSettingsView } from '../../../../core/domain/handoff.model';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { HandoffsRepository, type UpdateNotificationSettingsResult } from '../../../../core/repositories/handoffs.repository';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { NotificationSettingsPanelComponent } from './notification-settings-panel.component';

const defaults: NotificationSettingsView = { email: null, handoffEmails: true, emailDeliveryConfigured: true };

async function render(options: {
  accountId?: AccountId | null;
  settings?: NotificationSettingsView;
  update?: (request: { email: string | null; handoffEmails: boolean }) => Observable<UpdateNotificationSettingsResult>;
} = {}) {
  TestBed.resetTestingModule();
  let current = options.settings ?? defaults;
  const notificationSettings = vi.fn(() => of<RepositoryView<NotificationSettingsView>>({ status: 'ready', data: current }));
  const updateNotificationSettings = vi.fn(options.update ?? ((request: { email: string | null; handoffEmails: boolean }) => {
    current = { ...current, ...request };
    return of<UpdateNotificationSettingsResult>({ status: 'ready', data: current });
  }));
  await TestBed.configureTestingModule({
    imports: [NotificationSettingsPanelComponent],
    providers: [
      { provide: HandoffsRepository, useValue: { notificationSettings, updateNotificationSettings } },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(options.accountId === undefined ? 'account-smb-admin' : options.accountId) } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(NotificationSettingsPanelComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const page = fixture.nativeElement as HTMLElement;
  const settle = async () => {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return { fixture, page, settle, notificationSettings, updateNotificationSettings };
}

function required<T extends Element>(page: HTMLElement, selector: string): T {
  const found = page.querySelector<T>(selector);
  if (!found) throw new Error(`missing ${selector}`);
  return found;
}

function type(page: HTMLElement, value: string): void {
  const input = required<HTMLInputElement>(page, '#notification-email');
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('NotificationSettingsPanelComponent (issue #355)', () => {
  it('shows the account\'s address and choice, and saves an address with the Email turned off', async () => {
    const { page, settle, updateNotificationSettings } = await render();

    expect(page.querySelector('h2')?.textContent).toBe('我的通知');
    expect(page.textContent).toContain('不含訪客的對話內容');
    expect(page.querySelector('[data-email-delivery-off]')).toBeNull();
    expect(page.querySelector<HTMLInputElement>('#notification-email')?.value).toBe('');
    const checkbox = required<HTMLInputElement>(page, '[data-handoff-emails]');
    expect(checkbox.checked).toBe(true);

    type(page, ' owner@shop.example.org ');
    checkbox.click();
    page.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await settle();

    expect(updateNotificationSettings).toHaveBeenCalledWith({ email: 'owner@shop.example.org', handoffEmails: false });
    expect(page.querySelector('[role="status"]')?.textContent).toContain('不會寄轉真人的 Email 給你');
  });

  it('refuses an invalid address before sending, and shows the server\'s refusal on the field', async () => {
    const update = vi.fn(() => of<UpdateNotificationSettingsResult>({ status: 'validation-failed', message: NOTIFICATION_EMAIL_INVALID_MESSAGE }));
    const { page, settle, updateNotificationSettings } = await render({ update });

    type(page, 'a@b');
    page.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await settle();
    expect(updateNotificationSettings).not.toHaveBeenCalled();
    expect(page.querySelector('#notification-email-error')?.textContent).toBe(NOTIFICATION_EMAIL_INVALID_MESSAGE);
    expect(page.querySelector('#notification-email')?.getAttribute('aria-invalid')).toBe('true');
    expect(page.querySelector('#notification-email')?.getAttribute('aria-describedby')).toBe('notification-email-hint notification-email-error');

    type(page, 'owner@shop.example.org');
    await settle();
    expect(page.querySelector('#notification-email-error')).toBeNull();
    page.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await settle();
    expect(updateNotificationSettings).toHaveBeenCalledTimes(1);
    expect(page.querySelector('#notification-email-error')?.textContent).toBe(NOTIFICATION_EMAIL_INVALID_MESSAGE);
  });

  it('says when the deployment has no mail server', async () => {
    const { page } = await render({ settings: { email: 'owner@shop.example.org', handoffEmails: true, emailDeliveryConfigured: false } });

    expect(page.querySelector('[data-email-delivery-off]')?.textContent).toContain('還沒有設定寄信伺服器');
    expect(page.querySelector<HTMLInputElement>('#notification-email')?.value).toBe('owner@shop.example.org');
  });

  it('is not shown to an external customer and asks for nothing', async () => {
    const { page, notificationSettings } = await render({ accountId: 'account-external-customer' });

    expect(page.querySelector('[data-notification-settings]')).toBeNull();
    expect(notificationSettings).not.toHaveBeenCalled();
  });
});
