import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { ResponsivePanelComponent } from './responsive-panel.component';
import { of, BehaviorSubject } from 'rxjs';
import { BreakpointObserver } from '@angular/cdk/layout';

@Component({
  imports: [ResponsivePanelComponent],
  template: `
    <button type="button" class="trigger" (click)="open.set(true)">開啟</button>
    <lib-responsive-panel
      [open]="open()"
      [heading]="heading()"
      closeLabel="關閉面板"
      [showHeaderDivider]="showHeaderDivider()"
      (closed)="onClosed()"
    >
      <p class="content">內容</p>
      <button type="button" class="content-action">動作</button>
    </lib-responsive-panel>
  `,
})
class HostComponent {
  readonly open = signal(false);
  readonly heading = signal('標題');
  readonly showHeaderDivider = signal(true);
  closedCount = 0;
  onClosed(): void {
    this.closedCount++;
  }
}

@Component({
  imports: [ResponsivePanelComponent],
  template: `
    <lib-responsive-panel [open]="true" heading="標題" closeLabel="關閉面板">
      @if (withTabs()) {
        <span panelHeaderTabs class="header-tab">分頁</span>
      }
      <p class="content">內容</p>
    </lib-responsive-panel>
  `,
})
class HeaderTabsHostComponent {
  readonly withTabs = signal(false);
}

describe('ResponsivePanelComponent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  it('open 為 false 時不渲染投影內容', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.content')).toBeNull();
  });

  it('open 為 true 時渲染投影內容與標題', () => {
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.content')?.textContent).toContain('內容');
    expect(fixture.nativeElement.textContent).toContain('標題');
  });

  it('點關閉鈕發出 closed', () => {
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    const closeButton: HTMLButtonElement = fixture.nativeElement.querySelector(
      '.responsive-panel__close',
    );
    closeButton.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.closedCount).toBe(1);
  });

  it('關閉鈕的 aria-label 來自 closeLabel input', () => {
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    const closeButton: HTMLButtonElement = fixture.nativeElement.querySelector(
      '.responsive-panel__close',
    );
    expect(closeButton.getAttribute('aria-label')).toBe('關閉面板');
  });

  it('heading 有值時，panel 的 aria-labelledby 對應到標題元素的 id', () => {
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    const panel: HTMLElement = fixture.nativeElement.querySelector('.responsive-panel');
    const headingEl: HTMLElement = fixture.nativeElement.querySelector(
      '.responsive-panel__heading',
    );
    expect(panel.getAttribute('aria-labelledby')).toBe(headingEl.id);
    expect(headingEl.id).toBeTruthy();
    expect(panel.hasAttribute('aria-label')).toBe(false);
  });

  it('heading 為空時，panel 的 aria-label 使用 closeLabel', () => {
    fixture.componentInstance.heading.set('');
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    const panel: HTMLElement = fixture.nativeElement.querySelector('.responsive-panel');
    expect(panel.getAttribute('aria-label')).toBe('關閉面板');
    expect(panel.hasAttribute('aria-labelledby')).toBe(false);
  });
});

describe('ResponsivePanelComponent 表頭分隔線', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.open.set(true);
  });

  it('showHeaderDivider 預設為 true，header 不帶 borderless class', () => {
    fixture.detectChanges();

    const header: HTMLElement = fixture.nativeElement.querySelector('.responsive-panel__header');
    expect(header.classList.contains('responsive-panel__header--borderless')).toBe(false);
  });

  it('showHeaderDivider 為 false 時，header 帶上 borderless class', () => {
    fixture.componentInstance.showHeaderDivider.set(false);
    fixture.detectChanges();

    const header: HTMLElement = fixture.nativeElement.querySelector('.responsive-panel__header');
    expect(header.classList.contains('responsive-panel__header--borderless')).toBe(true);
  });
});

describe('ResponsivePanelComponent header-tabs 插槽', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<HeaderTabsHostComponent>>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HeaderTabsHostComponent] });
    fixture = TestBed.createComponent(HeaderTabsHostComponent);
  });

  it('未投影 panelHeaderTabs 內容時，wrapper 符合 :empty（會被 CSS 隱藏）', () => {
    fixture.detectChanges();

    const wrapper: HTMLElement = fixture.nativeElement.querySelector(
      '.responsive-panel__header-tabs',
    );
    expect(wrapper.matches(':empty')).toBe(true);
  });

  it('有投影 panelHeaderTabs 內容時，wrapper 不符合 :empty 且內容被渲染', () => {
    fixture.componentInstance.withTabs.set(true);
    fixture.detectChanges();

    const wrapper: HTMLElement = fixture.nativeElement.querySelector(
      '.responsive-panel__header-tabs',
    );
    expect(wrapper.matches(':empty')).toBe(false);
    expect(wrapper.querySelector('.header-tab')?.textContent).toContain('分頁');
  });
});

function provideBreakpoint(matches: boolean) {
  return {
    provide: BreakpointObserver,
    useValue: { observe: () => of({ matches, breakpoints: {} }) },
  };
}

describe('ResponsivePanelComponent 響應式行為', () => {
  let fixture: ComponentFixture<HostComponent> | undefined;

  function createFixture(narrow: boolean) {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideBreakpoint(narrow)],
    });
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => {
    // Narrow-mode content is relocated into a CDK overlay appended to document.body via
    // DomPortal — destroying the fixture disposes that overlay so it doesn't leak into the
    // next test's document.body queries.
    fixture?.destroy();
    fixture = undefined;
  });

  it('窄螢幕：具 role=dialog 與 aria-modal，且有遮罩', () => {
    createFixture(true);
    const panel: HTMLElement = document.body.querySelector('.responsive-panel')!;

    expect(panel.getAttribute('role')).toBe('dialog');
    expect(panel.getAttribute('aria-modal')).toBe('true');
    expect(document.body.querySelector('.responsive-panel__backdrop')).not.toBeNull();
  });

  it('窄螢幕：點遮罩發出 closed', () => {
    const fx = createFixture(true);
    const backdrop: HTMLElement = document.body.querySelector('.responsive-panel__backdrop')!;

    backdrop.click();
    fx.detectChanges();

    expect(fx.componentInstance.closedCount).toBe(1);
  });

  it('寬螢幕：無 role/aria-modal，無遮罩', () => {
    const fx = createFixture(false);
    const panel: HTMLElement = fx.nativeElement.querySelector('.responsive-panel');

    expect(panel.hasAttribute('role')).toBe(false);
    expect(panel.hasAttribute('aria-modal')).toBe(false);
    expect(fx.nativeElement.querySelector('.responsive-panel__backdrop')).toBeNull();
  });

  it('開啟時按 Esc 發出 closed', () => {
    const fx = createFixture(false);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fx.detectChanges();

    expect(fx.componentInstance.closedCount).toBe(1);
  });

  it('關閉時按 Esc 不動作', () => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideBreakpoint(false)],
    });
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.closedCount).toBe(0);
  });
});

describe('ResponsivePanelComponent 焦點與捲動', () => {
  // 每個 it() 只呼叫一次 configureTestingModule（透過這個 helper），避免在同一個
  // test 內於 fixture 建立後重新設定 TestBed（Angular 會丟錯）。
  let fixture: ComponentFixture<HostComponent> | undefined;

  function createFixture(narrow: boolean) {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideBreakpoint(narrow)],
    });
    fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  afterEach(() => {
    fixture?.destroy();
    fixture = undefined;
  });

  it('開啟時焦點移入面板內第一個可聚焦元素', () => {
    const fx = createFixture(false);
    fx.componentInstance.open.set(true);
    fx.detectChanges();

    const closeButton = fx.nativeElement.querySelector('.responsive-panel__close');
    expect(document.activeElement).toBe(closeButton);
  });

  it('關閉後焦點還原到觸發元素', () => {
    const fx = createFixture(false);
    const trigger: HTMLButtonElement = fx.nativeElement.querySelector('.trigger');
    trigger.focus();

    fx.componentInstance.open.set(true);
    fx.detectChanges();
    fx.componentInstance.open.set(false);
    fx.detectChanges();

    expect(document.activeElement).toBe(trigger);
  });

  it('窄螢幕下 Tab 在最後一個可聚焦元素時循環回第一個', () => {
    const fx = createFixture(true);
    fx.componentInstance.open.set(true);
    fx.detectChanges();

    const closeButton: HTMLElement = document.body.querySelector('.responsive-panel__close')!;
    const actionButton: HTMLElement = document.body.querySelector('.content-action')!;
    actionButton.focus();

    document.body
      .querySelector('.responsive-panel')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));

    expect(document.activeElement).toBe(closeButton);
  });

  it('寬螢幕下 Tab 不被攔截（無循環）', () => {
    const fx = createFixture(false);
    fx.componentInstance.open.set(true);
    fx.detectChanges();

    const actionButton: HTMLElement = fx.nativeElement.querySelector('.content-action');
    actionButton.focus();

    fx.nativeElement
      .querySelector('.responsive-panel')
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));

    expect(document.activeElement).toBe(actionButton);
  });

  it('窄螢幕開啟時鎖住 body 捲動，關閉後還原', () => {
    const fx = createFixture(true);
    fx.componentInstance.open.set(true);
    fx.detectChanges();

    expect(document.body.style.overflow).toBe('hidden');

    fx.componentInstance.open.set(false);
    fx.detectChanges();

    expect(document.body.style.overflow).toBe('');
  });

  it('寬螢幕開啟時不鎖住 body 捲動', () => {
    const fx = createFixture(false);
    fx.componentInstance.open.set(true);
    fx.detectChanges();

    expect(document.body.style.overflow).toBe('');
  });

  it('isNarrow 在 open 期間改變不會重新搶焦點或維持捲動鎖定，且不重建 DOM', () => {
    const breakpoint$ = new BehaviorSubject({ matches: true, breakpoints: {} });
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [{ provide: BreakpointObserver, useValue: { observe: () => breakpoint$ } }],
    });
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();

    const actionButton: HTMLElement = document.body.querySelector('.content-action')!;
    actionButton.focus();

    breakpoint$.next({ matches: false, breakpoints: {} }); // narrow -> wide while still open
    fixture.detectChanges();

    expect(document.activeElement).toBe(actionButton); // same DOM node, focus not stolen back
    expect(fixture.nativeElement.contains(actionButton)).toBe(true); // moved back inline, not recreated
    expect(document.body.style.overflow).toBe(''); // scroll lock released now that it's wide
  });
});
