import { Component, EventEmitter, Input, Output, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { vi } from 'vitest';
import { BreakpointObserver } from '@angular/cdk/layout';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { RouterOutlet, Router, provideRouter } from '@angular/router';
import { MatSidenavModule } from '@angular/material/sidenav';
import { of, Subject } from 'rxjs';
import { App } from './app';
import type { NavEntry, NavLeaf } from './layout/side-nav/nav-item.model';

/**
 * 用最小的替身元件取代 SideNavComponent／HeaderComponent／FooterComponent：
 * 它們各自拉了 DemoSessionService、ApiSessionService、DEMO_REPOSITORY 等一整套
 * 依賴，App 這裡只在乎「捲動容器換頁後有沒有回頂端」，跟它們的實際內容無關。
 */
@Component({
  selector: 'app-side-nav',
  standalone: true,
  template: '',
})
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

@Component({
  selector: 'app-header',
  standalone: true,
  template: '',
})
class StubHeader {
  @Input() currentTitle: unknown;
  @Input() currentGroupLabel: unknown;
  @Output() menuToggle = new EventEmitter<void>();
}

@Component({
  selector: 'app-footer',
  standalone: true,
  template: '',
})
class StubFooter {}

describe('App', () => {
  /** 一個假的 Router：只提供 App 用到的 `url` 與 `events`，讓測試自己丟 NavigationEnd。 */
  function createFakeRouter(initialUrl: string) {
    return {
      url: initialUrl,
      events: new Subject<{ type: number }>(),
    };
  }

  async function render(
    fakeRouter: { url: string; events: Subject<{ type: number }> },
    extraProviders: unknown[] = [],
  ) {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        ...(extraProviders as never[]),
        { provide: Router, useValue: fakeRouter },
        // 整頁載入時 App 看網址列決定要不要 shell（issue #277）；這裡的網址列就是假 Router 一開始的網址。
        { provide: Location, useValue: { path: () => fakeRouter.url } },
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: false, breakpoints: {} }) },
        },
      ],
    })
      .overrideComponent(App, {
        set: {
          imports: [RouterOutlet, MatSidenavModule, StubSideNav, StubHeader, StubFooter],
        },
      })
      .compileComponents();

    const fixture = TestBed.createComponent(App);
    // 換頁時的逾期數字（#250）會動態 import 首頁的 chunk；這裡的測試不看它，換成不做事，免得 import 在
    // 測試環境拆掉之後才完成（vitest 的 EnvironmentTeardownError）。要看它的測試自己再換成 spy。
    (fixture.componentInstance as unknown as { refreshCaseOverdueCount: (url: string) => void }).refreshCaseOverdueCount = () => undefined;
    fixture.detectChanges();
    return fixture;
  }

  it('resets the scrollable content area back to the top when navigation completes (issue #66)', async () => {
    const fakeRouter = createFakeRouter('/app/home');
    const fixture = await render(fakeRouter);

    const content = fixture.nativeElement.querySelector('mat-sidenav-content.content-area') as HTMLElement;
    expect(content).toBeTruthy();

    // 模擬使用者在首頁往下捲動，再從清單連結換到另一頁。
    content.scrollTop = 240;
    expect(content.scrollTop).toBe(240);

    fakeRouter.url = '/app/channels';
    fakeRouter.events.next({ type: 1 }); // NavigationEnd
    fixture.detectChanges();

    expect(content.scrollTop).toBe(0);
  });

  it('does not touch the scroll container before the first navigation event fires', async () => {
    const fakeRouter = createFakeRouter('/app/home');
    const fixture = await render(fakeRouter);

    const content = fixture.nativeElement.querySelector('mat-sidenav-content.content-area') as HTMLElement;
    content.scrollTop = 88;

    // 非 NavigationEnd 的事件（例如 NavigationStart）不應該提前把捲動位置清掉。
    fakeRouter.events.next({ type: 0 });
    fixture.detectChanges();

    expect(content.scrollTop).toBe(88);
  });

  describe('a full page load of a workspace URL (issue #277)', () => {
    /** 每建立一次就記一筆，看得出路由頁面是不是被建立了兩次。 */
    const created: string[] = [];

    @Component({ selector: 'app-counting-page', standalone: true, template: '<section data-counting-page>頁面</section>' })
    class CountingPage {
      constructor() {
        created.push('page');
      }
    }

    @Component({ selector: 'app-counting-login', standalone: true, template: '<p data-counting-login>登入</p>' })
    class CountingLogin {
      constructor() {
        created.push('login');
      }
    }

    /**
     * 與瀏覽器整頁載入相同：網址已經是 `path`，Router 還沒做第一次導覽（`router.url` 仍是 `/`），
     * App 先渲染，之後才由 `initialNavigation()` 啟用路由頁面。
     */
    async function loadAt(path: string) {
      created.length = 0;
      await TestBed.configureTestingModule({
        imports: [App],
        providers: [
          provideRouter([
            { path: 'app/settings', component: CountingPage },
            { path: 'login', component: CountingLogin },
          ]),
          provideLocationMocks(),
          {
            provide: BreakpointObserver,
            useValue: { observe: () => of({ matches: false, breakpoints: {} }) },
          },
        ],
      })
        .overrideComponent(App, {
          set: { imports: [RouterOutlet, MatSidenavModule, StubSideNav, StubHeader, StubFooter] },
        })
        .compileComponents();
      TestBed.inject(Location).go(path);
      const router = TestBed.inject(Router);
      expect(router.url).toBe('/');

      const fixture = TestBed.createComponent(App);
      (fixture.componentInstance as unknown as { refreshCaseOverdueCount: (url: string) => void }).refreshCaseOverdueCount = () => undefined;
      fixture.detectChanges();
      const shellBeforeNavigation = fixture.nativeElement.querySelector('.app-shell') !== null;

      router.initialNavigation();
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, host: fixture.nativeElement as HTMLElement, shellBeforeNavigation };
    }

    it('renders the shell before the first navigation, so the routed page is created only once inside it', async () => {
      const { host, shellBeforeNavigation } = await loadAt('/app/settings');

      // 以前 App 用 `router.url`（整頁載入時還是 `/`）決定要不要有 shell：頁面先建立在 shell 外面的
      // outlet，NavigationEnd 之後才換到 shell 裡的 outlet，又建立一次；第一份在 DOM 裡停留一個畫格就被
      // 移除，e2e 抓到它就一直等不到內容（CI 偶發逾時）。
      expect(created).toEqual(['page']);
      expect(shellBeforeNavigation).toBe(true);
      expect(host.querySelectorAll('[data-counting-page]')).toHaveLength(1);
      expect(host.querySelector('.app-shell [data-counting-page]')).not.toBeNull();
    });

    it('keeps pages outside the workspace without the shell', async () => {
      const { host, shellBeforeNavigation } = await loadAt('/login');

      expect(shellBeforeNavigation).toBe(false);
      expect(created).toEqual(['login']);
      expect(host.querySelector('.app-shell')).toBeNull();
      expect(host.querySelector('[data-counting-login]')).not.toBeNull();
    });
  });

  describe('the overdue number beside 案件 (issue #250)', () => {
    it('hands the 案件 entry one count signal and refreshes it on every navigation', async () => {
      const fakeRouter = createFakeRouter('/app/home');
      const fixture = await render(fakeRouter);
      const app = fixture.componentInstance as unknown as { refreshCaseOverdueCount: (url: string) => void; caseOverdueCount: WritableSignal<number> };
      const refresh = vi.fn<(url: string) => void>();
      app.refreshCaseOverdueCount = refresh;
      const navItems = fixture.debugElement.query(By.directive(StubSideNav)).componentInstance.navItems as NavEntry[];
      const cases = navItems.find((entry): entry is NavLeaf => 'route' in entry && entry.route === '/app/cases') as NavLeaf;
      expect(cases.count).toBe(app.caseOverdueCount);
      expect(cases.count?.()).toBe(0);
      expect(navItems.filter((entry) => 'count' in entry)).toEqual([cases]);

      for (const url of ['/app/chat', '/app/forms/a']) {
        fakeRouter.url = url;
        fakeRouter.events.next({ type: 1 });
      }
      fakeRouter.events.next({ type: 0 }); // not a NavigationEnd
      expect(refresh.mock.calls).toEqual([['/app/chat'], ['/app/forms/a']]);

      app.caseOverdueCount.set(3);
      expect(cases.count?.()).toBe(3);
    });
  });
});
