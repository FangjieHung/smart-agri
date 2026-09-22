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
  it('states that account switching is a demo and enters the workspace with the selected persona', async () => {
    const navigateByUrl = vi.fn().mockResolvedValue(true);
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    const switchAccount = vi.spyOn(session, 'switchAccount');
    const fixture = await renderLogin(session, navigateByUrl);

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Demo 帳號切換，不是真實驗證');
    expect(page.textContent).toContain('不會輸入密碼');
    expect(page.textContent).toContain('SMB 管理者');
    expect(page.textContent).toContain('內部使用者');
    expect(page.textContent).toContain('外部客戶');

    (Array.from(page.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('內部使用者'),
    ) as HTMLButtonElement).click();

    expect(switchAccount).toHaveBeenCalledWith('account-internal-employee');
    expect(navigateByUrl).toHaveBeenCalledWith('/app/home');
  });

  it('does not show a timeout message for a first visit', async () => {
    const session = new DemoSessionService({ storage: createMemoryStorage() });
    const fixture = await renderLogin(session);

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Demo 登入已逾時');
  });

  it('explains an expired demo session and still offers the personas', async () => {
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
    expect(page.querySelectorAll('.demo-login__persona')).toHaveLength(3);
  });
});
