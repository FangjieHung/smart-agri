import { Component, EventEmitter, Input, Output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { vi } from 'vitest';
import { BreakpointObserver } from '@angular/cdk/layout';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { RouterOutlet, Router, provideRouter } from '@angular/router';
import { MatSidenavModule } from '@angular/material/sidenav';
import { of } from 'rxjs';
import { App } from './app';
import { WorkspaceLayoutComponent } from './layout/workspace-layout/workspace-layout.component';

/** 外框的側欄／頁首／頁尾換成替身：它們各自拉了一整套 session 與 repository 依賴，這裡只看路由頁面建立幾次。 */
@Component({ selector: 'app-side-nav', standalone: true, template: '' })
class StubSideNav {
  @Input() navItems: unknown;
  @Input() collapsed: unknown;
  @Input() isMobile: unknown;
  @Input() openGroupLabel: unknown;
  @Input() currentGroupLabel: unknown;
  @Input() flyoutPositions: unknown;
  @Output() toggleCollapse = new EventEmitter<void>();
  @Output() toggleGroup = new EventEmitter<string>();
  @Output() navClick = new EventEmitter<void>();
  @Output() groupDetach = new EventEmitter<void>();
}

@Component({ selector: 'app-header', standalone: true, template: '' })
class StubHeader {
  @Input() currentTitle: unknown;
  @Input() currentGroupLabel: unknown;
  @Output() menuToggle = new EventEmitter<void>();
}

@Component({ selector: 'app-footer', standalone: true, template: '' })
class StubFooter {}

describe('App', () => {
  /** 每建立一次就記一筆，看得出路由頁面是不是被建立了兩次。 */
  const created: string[] = [];

  @Component({ selector: 'app-counting-home', standalone: true, template: '<section data-page="home">首頁</section>' })
  class CountingHome {
    constructor() {
      created.push('home');
    }
  }

  @Component({ selector: 'app-counting-settings', standalone: true, template: '<section data-page="settings">設定</section>' })
  class CountingSettings {
    constructor() {
      created.push('settings');
    }
  }

  @Component({ selector: 'app-counting-login', standalone: true, template: '<p data-page="login">登入</p>' })
  class CountingLogin {
    constructor() {
      created.push('login');
    }
  }

  /**
   * 每個掛進 DOM 的外框與路由頁面（依宿主標籤）。外框本身也是路由元件；以前多出來的那一份可能在建構子之後、
   * 第一次變更偵測之前就被移除，只數建構子會漏掉，所以也記錄 DOM 裡出現過幾份（#277 的 e2e 就是抓到它）。
   */
  const mounted: string[] = [];
  let observer: MutationObserver | undefined;
  let refresh: ReturnType<typeof vi.fn<(url: string) => void>>;
  afterEach(() => {
    observer?.disconnect();
    vi.restoreAllMocks();
  });

  /**
   * 用真 Router 與跟 `app.routes.ts` 相同的形狀：工作區是 `/app` 的外框父路由，各頁是子路由。與瀏覽器整頁載入
   * 相同：網址已經是 `path`，Router 還沒做第一次導覽（`router.url` 仍是 `/`），App 先渲染，之後才由
   * `initialNavigation()` 啟用路由頁面。
   */
  async function loadAt(path: string) {
    created.length = 0;
    // 換頁時的逾期數字（#250）會動態 import 首頁的 chunk；外框由 Router 建立，所以在 prototype 上替換。
    refresh = vi.fn<(url: string) => void>();
    vi.spyOn(WorkspaceLayoutComponent.prototype as unknown as { refreshCaseOverdueCount: (url: string) => void }, 'refreshCaseOverdueCount').mockImplementation(refresh);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([
          { path: 'login', component: CountingLogin },
          {
            path: 'app',
            component: WorkspaceLayoutComponent,
            children: [
              { path: 'home', component: CountingHome },
              { path: 'settings', component: CountingSettings },
            ],
          },
        ]),
        provideLocationMocks(),
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: false, breakpoints: {} }) },
        },
      ],
    })
      .overrideComponent(WorkspaceLayoutComponent, {
        set: { imports: [RouterOutlet, MatSidenavModule, StubSideNav, StubHeader, StubFooter] },
      })
      .compileComponents();
    TestBed.inject(Location).go(path);
    const router = TestBed.inject(Router);
    expect(router.url).toBe('/');

    const fixture = TestBed.createComponent(App);
    mounted.length = 0;
    observer = new MutationObserver((records) => {
      for (const node of records.flatMap((record) => [...record.addedNodes])) {
        if (node instanceof Element && /^app-(workspace-layout|counting-)/.test(node.localName)) mounted.push(node.localName);
      }
    });
    observer.observe(fixture.nativeElement, { childList: true, subtree: true });
    fixture.detectChanges();
    router.initialNavigation();
    await fixture.whenStable();
    fixture.detectChanges();
    await Promise.resolve();

    const host = fixture.nativeElement as HTMLElement;
    const navigate = async (url: string) => {
      created.length = 0;
      mounted.length = 0;
      await router.navigateByUrl(url);
      await fixture.whenStable();
      fixture.detectChanges();
      // MutationObserver 的回呼排在 microtask，先讓它把這次換頁的紀錄送完。
      await Promise.resolve();
    };
    const title = () => fixture.debugElement.query(By.directive(StubHeader))?.componentInstance.currentTitle;
    return { host, navigate, title };
  }

  it('creates a workspace page once on a full page load, inside the shell (issue #277)', async () => {
    const { host, title } = await loadAt('/app/settings');

    expect(created).toEqual(['settings']);
    expect(mounted).toEqual(['app-workspace-layout', 'app-counting-settings']);
    expect(host.querySelectorAll('.app-shell')).toHaveLength(1);
    expect(host.querySelectorAll('[data-page]')).toHaveLength(1);
    expect(host.querySelector('.app-shell #main-content [data-page="settings"]')).not.toBeNull();
    // 外框在第一次導覽中建立，那次導覽的 NavigationEnd 也要更新標題與逾期數字。
    expect(title()).toBe('團隊與設定');
    expect(refresh.mock.calls).toEqual([['/app/settings']]);
  });

  it('keeps pages outside the workspace without the shell', async () => {
    const { host } = await loadAt('/login');

    expect(created).toEqual(['login']);
    expect(mounted).toEqual(['app-counting-login']);
    expect(host.querySelector('.app-shell')).toBeNull();
    expect(host.querySelector('[data-page="login"]')).not.toBeNull();
    expect(refresh).not.toHaveBeenCalled();
  });

  // issue #322：以前 App 用 `@if (isWorkspace)` 在兩個 outlet 之間切換，NavigationEnd 之後才換邊，
  // 進出工作區時頁面先建立在舊的 outlet、再到新的 outlet 重建一次。
  it('creates the workspace page once when navigating in from /login', async () => {
    const { host, navigate, title } = await loadAt('/login');

    await navigate('/app/home');

    expect(created).toEqual(['home']);
    expect(mounted).toEqual(['app-workspace-layout', 'app-counting-home']);
    expect(host.querySelectorAll('.app-shell')).toHaveLength(1);
    expect(host.querySelectorAll('[data-page]')).toHaveLength(1);
    expect(host.querySelector('.app-shell #main-content [data-page="home"]')).not.toBeNull();
    expect(title()).toBe('首頁');
    expect(refresh.mock.calls).toEqual([['/app/home']]);
  });

  it('creates the login page once when leaving the workspace', async () => {
    const { host, navigate } = await loadAt('/app/settings');
    refresh.mockClear();

    await navigate('/login');

    expect(created).toEqual(['login']);
    expect(mounted).toEqual(['app-counting-login']);
    expect(host.querySelector('.app-shell')).toBeNull();
    expect(host.querySelectorAll('[data-page]')).toHaveLength(1);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('keeps the same shell while moving between workspace pages', async () => {
    const { host, navigate, title } = await loadAt('/app/home');
    const shell = host.querySelector('.app-shell');

    await navigate('/app/settings');

    expect(created).toEqual(['settings']);
    expect(mounted).toEqual(['app-counting-settings']);
    expect(host.querySelector('.app-shell')).toBe(shell);
    expect(host.querySelectorAll('[data-page]')).toHaveLength(1);
    expect(title()).toBe('團隊與設定');
    expect(refresh.mock.calls).toEqual([['/app/home'], ['/app/settings']]);
  });
});
