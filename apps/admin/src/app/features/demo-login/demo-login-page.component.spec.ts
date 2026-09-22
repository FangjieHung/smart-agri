import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { DemoLoginPageComponent } from './demo-login-page.component';

describe('DemoLoginPageComponent', () => {
  it('states that account switching is a demo and enters the workspace with the selected persona', async () => {
    const navigateByUrl = vi.fn().mockResolvedValue(true);
    const switchAccount = vi.fn();

    await TestBed.configureTestingModule({
      imports: [DemoLoginPageComponent],
      providers: [
        { provide: Router, useValue: { navigateByUrl } },
        { provide: DemoSessionService, useValue: { switchAccount } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(DemoLoginPageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Demo 帳號切換，不是真實驗證');
    expect(page.textContent).toContain('SMB 管理者');
    expect(page.textContent).toContain('內部使用者');
    expect(page.textContent).toContain('外部客戶');

    (Array.from(page.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('內部使用者'),
    ) as HTMLButtonElement).click();

    expect(switchAccount).toHaveBeenCalledWith('account-internal-employee');
    expect(navigateByUrl).toHaveBeenCalledWith('/app/home');
  });
});
