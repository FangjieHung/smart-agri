import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { DemoLoginPageComponent } from './demo-login-page.component';

async function renderLogin(session: DemoSessionService, navigateByUrl = vi.fn()) {
  await TestBed.configureTestingModule({
    imports: [DemoLoginPageComponent],
    providers: [
      { provide: Router, useValue: { navigateByUrl } },
      { provide: DemoSessionService, useValue: session },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(DemoLoginPageComponent);
  fixture.detectChanges();
  return fixture;
}

describe('DemoLoginPageComponent', () => {
  it('accepts a demo username and password and enters with the matching account', async () => {
    const navigateByUrl = vi.fn().mockResolvedValue(true);
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    const switchAccount = vi.spyOn(session, 'switchAccount');
    const fixture = await renderLogin(session, navigateByUrl);

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Demo 登入');
    expect(page.textContent).toContain('密碼都是 1234');
    expect(page.textContent).toContain('SMB 管理者');
    expect(page.textContent).toContain('內部使用者');
    expect(page.textContent).toContain('外部客戶');

    const username = page.querySelector<HTMLInputElement>('#demo-username')!;
    username.value = 'internal';
    username.dispatchEvent(new Event('input'));
    const password = page.querySelector<HTMLInputElement>('#demo-password')!;
    password.value = '1234';
    password.dispatchEvent(new Event('input'));
    page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit', { cancelable: true }));

    expect(switchAccount).toHaveBeenCalledWith('account-internal-employee');
    expect(navigateByUrl).toHaveBeenCalledWith('/app/home');
  });

  it('does not show a timeout message for a first visit', async () => {
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    const fixture = await renderLogin(session);

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Demo 登入已逾時');
  });

  it('rejects incorrect credentials without starting a session', async () => {
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    const fixture = await renderLogin(session);
    const page = fixture.nativeElement as HTMLElement;
    page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
    expect(page.querySelector('[role="alert"]')?.textContent).toContain('帳號或密碼');
    expect(session.activeAccountId()).toBeNull();
  });

  it('explains an expired demo session and still lists the accounts', async () => {
    let clock = 1_000_000;
    const session = new DemoSessionService({
      storage: createMemoryStorage(),
      timeoutMs: 1000,
      now: () => clock,
    });
    session.switchAccount('account-smb-admin');
    clock += 1001;
    session.refreshActivity();

    const fixture = await renderLogin(session);
    const page = fixture.nativeElement as HTMLElement;
    const alert = page.querySelector('[role="alert"]');

    expect(alert?.textContent).toContain('Demo 登入已逾時');
    expect(alert?.textContent).toContain('不是真實登入');
    expect(page.textContent).toContain('customer');
  });
});
