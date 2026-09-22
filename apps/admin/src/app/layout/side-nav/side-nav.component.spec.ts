import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../../core/auth/auth.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { SideNavComponent } from './side-nav.component';

describe('SideNavComponent', () => {
  it('clears the demo persona before completing logout', async () => {
    const clearSession = vi.fn();
    const logout = vi.fn();
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [
        provideRouter([]),
        { provide: DemoSessionService, useValue: { clearSession } },
        { provide: AuthService, useValue: { logout } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(SideNavComponent);
    fixture.componentRef.setInput('navItems', []);
    fixture.componentRef.setInput('collapsed', true);
    fixture.componentRef.setInput('isMobile', false);
    fixture.componentRef.setInput('openGroupLabel', null);
    fixture.componentRef.setInput('currentGroupLabel', null);
    fixture.componentRef.setInput('flyoutPositions', []);
    fixture.detectChanges();

    (fixture.componentInstance as unknown as { logout(): void }).logout();

    expect(clearSession).toHaveBeenCalledBefore(logout);
  });

  it('labels controls whose visible text is hidden in the collapsed sidebar', async () => {
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [provideRouter([]), { provide: DemoSessionService, useValue: {} }, { provide: AuthService, useValue: {} }],
    }).compileComponents();
    const fixture = TestBed.createComponent(SideNavComponent);
    fixture.componentRef.setInput('navItems', [{ route: '/app/home', label: '首頁', icon: 'home' }]);
    fixture.componentRef.setInput('collapsed', true);
    fixture.componentRef.setInput('isMobile', false);
    fixture.componentRef.setInput('openGroupLabel', null);
    fixture.componentRef.setInput('currentGroupLabel', null);
    fixture.componentRef.setInput('flyoutPositions', []);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.collapse-toggle')?.getAttribute('aria-label')).toBe('展開側邊導覽');
    expect(page.querySelector('.nav-link')?.getAttribute('aria-label')).toBe('首頁');
    expect(page.querySelector('.user-card')?.getAttribute('aria-label')).toBe('開啟帳號選單');
  });
});
