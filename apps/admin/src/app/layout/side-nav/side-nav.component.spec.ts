import { signal } from '@angular/core';
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
        { provide: ApiSessionService, useValue: { logout, displayName: signal(null) } },
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
      providers: [provideRouter([]), { provide: DemoSessionService, useValue: {} }, { provide: ApiSessionService, useValue: { displayName: signal(null) } }],
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

  async function renderExpanded(displayName: string | null) {
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [
        provideRouter([]),
        { provide: DemoSessionService, useValue: { activeAccountId: signal(null) } },
        { provide: ApiSessionService, useValue: { displayName: signal(displayName) } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(SideNavComponent);
    fixture.componentRef.setInput('navItems', []);
    fixture.componentRef.setInput('collapsed', false);
    fixture.componentRef.setInput('isMobile', false);
    fixture.componentRef.setInput('openGroupLabel', null);
    fixture.componentRef.setInput('currentGroupLabel', null);
    fixture.componentRef.setInput('flyoutPositions', []);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the signed-in account’s display name in API mode', async () => {
    const page = await renderExpanded('安心商行管理者');
    expect(page.querySelector('.user-card strong')?.textContent).toBe('安心商行管理者');
  });

  async function renderCases(count: number | undefined, collapsed: boolean) {
    await TestBed.configureTestingModule({
      imports: [SideNavComponent],
      providers: [
        provideRouter([]),
        { provide: DemoSessionService, useValue: { activeAccountId: signal(null) } },
        { provide: ApiSessionService, useValue: { displayName: signal(null) } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(SideNavComponent);
    fixture.componentRef.setInput('navItems', [
      { route: '/app/home', label: '首頁', icon: 'home' },
      { route: '/app/cases', label: '案件', icon: 'assignment', ...(count === undefined ? {} : { count: signal(count) }) },
    ]);
    fixture.componentRef.setInput('collapsed', collapsed);
    fixture.componentRef.setInput('isMobile', false);
    fixture.componentRef.setInput('openGroupLabel', null);
    fixture.componentRef.setInput('currentGroupLabel', null);
    fixture.componentRef.setInput('flyoutPositions', []);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    const links = Array.from(page.querySelectorAll<HTMLAnchorElement>('a.nav-link'));
    return { home: links[0], cases: links[1] };
  }

  it('shows the overdue number beside 案件 with 「N 件逾期」 for assistive technology (issue #250)', async () => {
    const { home, cases } = await renderCases(3, false);

    expect(cases.querySelector('.nav-count')?.textContent?.trim()).toBe('3');
    expect(cases.querySelector('.nav-count')?.getAttribute('aria-hidden')).toBe('true');
    expect(cases.getAttribute('aria-label')).toBe('案件，3 件逾期');
    expect(home.querySelector('.nav-count')).toBeNull();
    expect(home.getAttribute('aria-label')).toBeNull();
  });

  it('keeps the number in the collapsed sidebar, inside the link\'s label', async () => {
    const { cases } = await renderCases(12, true);

    expect(cases.querySelector('.nav-count')?.textContent?.trim()).toBe('12');
    expect(cases.getAttribute('aria-label')).toBe('案件，12 件逾期');
  });

  it('shows no number for 0 (or none)', async () => {
    for (const count of [0, undefined]) {
      TestBed.resetTestingModule();
      const { cases } = await renderCases(count, false);
      expect(cases.querySelector('.nav-count')).toBeNull();
      expect(cases.getAttribute('aria-label')).toBeNull();
      expect(cases.textContent?.trim()).toContain('案件');
    }
  });

  it('keeps the fixed label when there is no API display name (mock mode)', async () => {
    const page = await renderExpanded(null);
    expect(page.querySelector('.user-card strong')?.textContent).toBe('農場管理員');
  });
});
