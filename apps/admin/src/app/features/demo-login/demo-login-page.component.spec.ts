import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { ApiSessionService, type ApiSessionNotice } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import {
  API_LOGIN_INVALID_MESSAGE,
  DemoLoginPageComponent,
  LAST_ORGANIZATION_CODE_KEY,
} from './demo-login-page.component';

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

describe('DemoLoginPageComponent (API mode)', () => {
  function createApiSession(organizationCodeRequired: boolean) {
    return {
      apiMode: true,
      notice: signal<ApiSessionNotice | null>(null),
      loginOptions: vi.fn().mockResolvedValue({ organizationCodeRequired }),
      signIn: vi.fn<ApiSessionService['signIn']>().mockResolvedValue('redirecting'),
    };
  }

  async function renderApiLogin(
    apiSession: ReturnType<typeof createApiSession>,
    session = new DemoSessionService({ storage: createMemoryStorage() }),
  ) {
    await TestBed.configureTestingModule({
      imports: [DemoLoginPageComponent],
      providers: [
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
        { provide: DemoSessionService, useValue: session },
        { provide: ApiSessionService, useValue: apiSession },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DemoLoginPageComponent);
    await settle(fixture);
    return fixture;
  }

  /** 登入流程是 Promise 鏈（不走 PendingTasks），等一個 macrotask 再更新畫面。 */
  async function settle(fixture: ComponentFixture<DemoLoginPageComponent>): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function type(page: HTMLElement, selector: string, value: string): void {
    const input = page.querySelector(selector) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function submit(page: HTMLElement): void {
    (page.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
  }

  beforeEach(() => localStorage.removeItem(LAST_ORGANIZATION_CODE_KEY));
  afterEach(() => localStorage.removeItem(LAST_ORGANIZATION_CODE_KEY));

  it('hides the demo accounts and the shared demo password', async () => {
    const fixture = await renderApiLogin(createApiSession(true));
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('h1')?.textContent).toBe('登入');
    expect(page.textContent).not.toContain('1234');
    expect(page.textContent).not.toContain('Demo 帳號');
    expect(page.textContent).not.toContain('SMB 管理者');
    expect(page.textContent).not.toContain('固定示範帳號');
  });

  it('explains an expired session without the demo-only wording', async () => {
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    session.switchAccount('account-smb-admin');
    session.expireSession();

    const fixture = await renderApiLogin(createApiSession(true), session);
    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');

    expect(alert?.textContent).toContain('登入已逾時');
    expect(alert?.textContent).not.toContain('Demo');
    expect(alert?.textContent).not.toContain('不是真實登入');
  });

  it('asks for the organization code when the deployment has several organizations', async () => {
    localStorage.setItem(LAST_ORGANIZATION_CODE_KEY, 'anxin');
    const apiSession = createApiSession(true);
    const fixture = await renderApiLogin(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    const organizationCode = page.querySelector<HTMLInputElement>('#login-organization-code');
    expect(organizationCode?.value).toBe('anxin');

    type(page, '#demo-username', 'admin');
    type(page, '#demo-password', 'correct horse');
    submit(page);
    await settle(fixture);

    expect(apiSession.signIn).toHaveBeenCalledWith(
      { organizationCode: 'anxin', loginName: 'admin', password: 'correct horse' },
      null,
    );
  });

  it('hides the organization code for a single-organization deployment and sends none', async () => {
    const apiSession = createApiSession(false);
    const fixture = await renderApiLogin(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('#login-organization-code')).toBeNull();

    type(page, '#demo-username', 'admin');
    type(page, '#demo-password', 'correct horse');
    submit(page);
    await settle(fixture);

    expect(apiSession.signIn).toHaveBeenCalledWith(
      { organizationCode: null, loginName: 'admin', password: 'correct horse' },
      null,
    );
  });

  it('remembers the organization code after a successful login', async () => {
    const fixture = await renderApiLogin(createApiSession(true));
    const page = fixture.nativeElement as HTMLElement;

    type(page, '#login-organization-code', ' anxin ');
    type(page, '#demo-username', 'admin');
    type(page, '#demo-password', 'correct horse');
    submit(page);
    await settle(fixture);

    expect(localStorage.getItem(LAST_ORGANIZATION_CODE_KEY)).toBe('anxin');
  });

  it('shows one message for any wrong credential and does not remember the code', async () => {
    const apiSession = createApiSession(true);
    apiSession.signIn.mockResolvedValue('invalid-credentials');
    const fixture = await renderApiLogin(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    type(page, '#login-organization-code', 'anxin');
    type(page, '#demo-username', 'admin');
    type(page, '#demo-password', 'wrong');
    submit(page);
    await settle(fixture);

    expect(page.querySelector('.demo-login__error')?.textContent).toBe(API_LOGIN_INVALID_MESSAGE);
    expect(page.textContent).not.toContain('密碼錯誤');
    expect(page.textContent).not.toContain('帳號不存在');
    expect(localStorage.getItem(LAST_ORGANIZATION_CODE_KEY)).toBeNull();
    expect(page.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
  });

  it('does not call the API with a blank field', async () => {
    const apiSession = createApiSession(true);
    const fixture = await renderApiLogin(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    type(page, '#demo-username', 'admin');
    type(page, '#demo-password', 'correct horse');
    submit(page);
    fixture.detectChanges();

    expect(apiSession.signIn).not.toHaveBeenCalled();
    expect(page.querySelector('.demo-login__error')?.textContent).toBe(API_LOGIN_INVALID_MESSAGE);
  });

  it('explains why an account that must change its password was sent back', async () => {
    const apiSession = createApiSession(false);
    apiSession.notice.set('password-change-required');
    const fixture = await renderApiLogin(apiSession);

    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain(
      '須先設定新密碼',
    );
  });
});
