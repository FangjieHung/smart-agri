import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../core/domain/account.model';
import { API_HANDOFFS_PATH, API_NOTIFICATION_SETTINGS_PATH, HandoffsRepository } from '../../core/repositories/handoffs.repository';
import { API_DEMO_REPOSITORY_FACTORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { HandoffsPageComponent } from './handoffs-page.component';

// Recorded 2026-10-13 from the real API (issue #355), the same recording as
// `core/repositories/handoffs.repository-hybrid.spec.ts`: this branch's Api in its integration test host
// (`HandoffHostFixture`, PostgreSQL 18 via Testcontainers) through a temporary probe test that is not
// committed. Pasted unchanged; never edit them to match the types.
/** admin-list-pending: HTTP 200. */
const ADMIN_LIST_PENDING_JSON = `[{"id":"01a1346d-ced6-7b7a-9ce2-813bcab2a8f5","assistantId":"01a1346d-ce7e-7229-91de-e00c6cde352a","assistantName":"退貨小幫手","channel":"line","createdAt":"2026-10-13T08:42:54.038849+00:00","handledAt":null,"handledByAccountId":null,"handledByName":null},{"id":"01a1346d-ce46-7cce-9e97-941311c15e72","assistantId":"01a1346d-cd2f-7e26-a5ce-13fa6937d2f2","assistantName":"退貨小幫手","channel":"website","createdAt":"2026-10-13T08:42:53.894403+00:00","handledAt":null,"handledByAccountId":null,"handledByName":null}]`;
/** admin-settings-default: HTTP 200. */
const ADMIN_SETTINGS_DEFAULT_JSON = `{"email":null,"handoffEmails":true,"emailDeliveryConfigured":true}`;
/** admin-settings-put: HTTP 200. */
const ADMIN_SETTINGS_PUT_JSON = `{"email":"owner@shop.example.org","handoffEmails":false,"emailDeliveryConfigured":true}`;

const REMINDER = '[data-email-reminder]';

async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

/** 「轉真人」頁的 Email 提醒（issue #362），用真正的 repository：mock 與 API 模式的行為一致。 */
describe('HandoffsPageComponent notification Email reminder, mock and API mode (issue #362)', () => {
  it('mock mode: reminds until the account fills its Email, and not after it turns the Email off', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [HandoffsPageComponent],
      providers: [provideRouter([]), { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } }],
    }).compileComponents();
    const repository = TestBed.inject(HandoffsRepository);

    const first = TestBed.createComponent(HandoffsPageComponent);
    await settle(first);
    expect((first.nativeElement as HTMLElement).querySelector(REMINDER)).not.toBeNull();
    first.destroy();

    await firstValueFrom(repository.updateNotificationSettings({ email: 'owner@shop.example.org', handoffEmails: true }));
    const filled = TestBed.createComponent(HandoffsPageComponent);
    await settle(filled);
    expect((filled.nativeElement as HTMLElement).querySelector(REMINDER)).toBeNull();
    expect((filled.nativeElement as HTMLElement).querySelectorAll('[data-handoff]')).toHaveLength(2);
    filled.destroy();

    await firstValueFrom(repository.updateNotificationSettings({ email: null, handoffEmails: false }));
    const off = TestBed.createComponent(HandoffsPageComponent);
    await settle(off);
    expect((off.nativeElement as HTMLElement).querySelector(REMINDER)).toBeNull();
  });

  it('API mode: the recorded default settings remind, the recorded saved settings do not', async () => {
    for (const [settings, reminds] of [[ADMIN_SETTINGS_DEFAULT_JSON, true], [ADMIN_SETTINGS_PUT_JSON, false]] as const) {
      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [HandoffsPageComponent],
        providers: [
          provideRouter([]),
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
          { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } },
        ],
      }).compileComponents();
      const http = TestBed.inject(HttpTestingController);
      const fixture = TestBed.createComponent(HandoffsPageComponent);
      // Not whenStable: the pending requests keep the app unstable until they are flushed.
      fixture.detectChanges();
      TestBed.tick();

      http.expectOne((request) => request.url === API_HANDOFFS_PATH).flush(JSON.parse(ADMIN_LIST_PENDING_JSON));
      http.expectOne(API_NOTIFICATION_SETTINGS_PATH).flush(JSON.parse(settings));
      await settle(fixture);

      const page = fixture.nativeElement as HTMLElement;
      expect(page.querySelector(REMINDER) !== null).toBe(reminds);
      expect(page.querySelectorAll('[data-handoff]')).toHaveLength(2);
      http.verify();
    }
  });
});
