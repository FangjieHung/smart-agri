import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { ApiSessionService } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { SideNavComponent } from './side-nav.component';

describe('SideNavComponent', () => {
  it('logs out through the session service', async () => {
    const logout = vi.fn().mockResolvedValue(undefined);
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [
        provideRouter([]),
        { provide: DemoSessionService, useValue: {} },
        { provide: ApiSessionService, useValue: { logout } },
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

    expect(logout).toHaveBeenCalledOnce();
  });

  it('labels controls whose visible text is hidden in the collapsed sidebar', async () => {
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [provideRouter([]), { provide: DemoSessionService, useValue: {} }, { provide: ApiSessionService, useValue: {} }],
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
