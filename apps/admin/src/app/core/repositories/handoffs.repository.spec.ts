import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { NOTIFICATION_EMAIL_INVALID_MESSAGE, normalizeNotificationEmail } from '../domain/handoff.model';
import { DemoSessionService } from '../session/demo-session.service';
import { HandoffsRepository } from './handoffs.repository';

function mockRepository(accountId: AccountId | null = 'account-smb-admin') {
  TestBed.resetTestingModule();
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({ providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }] });
  return { repository: TestBed.inject(HandoffsRepository), activeAccountId };
}

/** 轉真人通知的 mock 模式（issue #355）：規則與後端相同。 */
describe('HandoffsRepository (mock mode)', () => {
  it('lists the owner\'s pending handoffs newest first, and the handled ones on request', async () => {
    const { repository } = mockRepository();

    const pending = await firstValueFrom(repository.list());
    expect(pending.status).toBe('ready');
    const rows = pending.status === 'ready' ? pending.data : [];
    expect(rows.map((row) => [row.channel, row.assistantName, row.handledAt])).toEqual([
      ['line', '客服助理', null],
      ['website', '客服助理', null],
    ]);
    const handled = await firstValueFrom(repository.list('handled'));
    expect(handled.status === 'ready' ? handled.data.map((row) => row.handledByName) : null).toEqual(['安心商行管理者']);
    const all = await firstValueFrom(repository.list('all'));
    expect(all.status === 'ready' ? all.data.length : null).toBe(3);
    expect(await firstValueFrom(repository.attention())).toEqual({ status: 'ready', data: { pendingCount: 2 } });
  });

  it('shows another member nothing and refuses to let them mark the owner\'s handoff', async () => {
    const { repository } = mockRepository('account-internal-employee');

    expect(await firstValueFrom(repository.list('all'))).toEqual({ status: 'ready', data: [] });
    expect(await firstValueFrom(repository.attention())).toEqual({ status: 'ready', data: { pendingCount: 0 } });
    const refused = await firstValueFrom(repository.markHandled('handoff-line-pending'));
    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'handoff' });
  });

  it('marks a handoff handled once: the count drops and marking again keeps the first time', async () => {
    const { repository } = mockRepository();

    const first = await firstValueFrom(repository.markHandled('handoff-line-pending'));
    expect(first.status).toBe('ready');
    const handledAt = first.status === 'ready' ? first.data.handledAt : null;
    expect(handledAt).not.toBeNull();
    expect(first.status === 'ready' ? first.data.handledByName : null).toBe('安心商行管理者');
    expect(await firstValueFrom(repository.attention())).toEqual({ status: 'ready', data: { pendingCount: 1 } });

    const again = await firstValueFrom(repository.markHandled('handoff-line-pending'));
    expect(again.status === 'ready' ? again.data.handledAt : null).toBe(handledAt);
  });

  it('keeps each account\'s own notification settings and refuses an invalid address', async () => {
    const { repository, activeAccountId } = mockRepository();

    expect(await firstValueFrom(repository.notificationSettings())).toEqual({
      status: 'ready', data: { email: null, handoffEmails: true, emailDeliveryConfigured: true },
    });
    expect(await firstValueFrom(repository.updateNotificationSettings({ email: 'a@b', handoffEmails: true }))).toEqual({
      status: 'validation-failed', message: NOTIFICATION_EMAIL_INVALID_MESSAGE,
    });
    const saved = await firstValueFrom(repository.updateNotificationSettings({ email: '  owner@shop.example.org ', handoffEmails: false }));
    expect(saved).toEqual({ status: 'ready', data: { email: 'owner@shop.example.org', handoffEmails: false, emailDeliveryConfigured: true } });

    activeAccountId.set('account-internal-employee');
    expect(await firstValueFrom(repository.notificationSettings())).toMatchObject({ data: { email: null, handoffEmails: true } });
  });
});

/** 與後端 `AccountNotificationSettings.TryNormalizeEmail` 相同的判斷（後端的測試案例）。 */
describe('normalizeNotificationEmail', () => {
  it.each([
    ['owner@shop.example.org', 'owner@shop.example.org'],
    ['  owner.name+tag@shop.example.org  ', 'owner.name+tag@shop.example.org'],
    ['', null],
    ['   ', null],
  ])('accepts %j as %j', (input, email) => {
    expect(normalizeNotificationEmail(input)).toEqual({ valid: true, email });
  });

  it.each([
    'not-an-address', 'a@b', 'a@.example.org', 'Owner <owner@example.org>', '"Owner" owner@example.org',
    'a@example.org,b@example.org', 'a@example.org; b@example.org', 'a@example.org\r\nBcc: x@example.org', 'a b@example.org',
    `${'a'.repeat(250)}@example.org`,
  ])('refuses %j', (input) => {
    expect(normalizeNotificationEmail(input).valid).toBe(false);
  });
});
