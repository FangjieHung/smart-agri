import { Component, EventEmitter, Input, Output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BreakpointObserver } from '@angular/cdk/layout';
import { RouterOutlet, Router } from '@angular/router';
import { MatSidenavModule } from '@angular/material/sidenav';
import { of, Subject } from 'rxjs';
import { App } from './app';

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

  async function render(fakeRouter: { url: string; events: Subject<{ type: number }> }) {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: Router, useValue: fakeRouter },
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
});
