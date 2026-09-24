import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { SettingsPageComponent } from './settings-page.component';

function render(accountId: AccountId = 'account-smb-admin') {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-23T02:00:00.000Z'),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(accountId) } },
  ];
  TestBed.configureTestingModule({ imports: [SettingsPageComponent], providers });
  const fixture = TestBed.createComponent(SettingsPageComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SettingsPageComponent', () => {
  it('renders the appearance rows through the shared setting row', () => {
    const { host } = render();
    const rows = Array.from(host.querySelectorAll('lib-setting-row'));

    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('質地');
    expect(rows[1].textContent).toContain('配色');
  });
});
