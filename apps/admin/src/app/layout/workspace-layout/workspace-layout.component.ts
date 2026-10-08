import { DOCUMENT } from '@angular/common';
import { Component, DestroyRef, ElementRef, inject, Injector, OnInit, signal, ViewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BreakpointObserver } from '@angular/cdk/layout';
import { ConnectedPosition } from '@angular/cdk/overlay';
import { MatSidenavContainer, MatSidenavModule } from '@angular/material/sidenav';
import { Router, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs/operators';
import { ZH_TW } from '../../core/i18n/zh-tw';
import { SideNavComponent } from '../side-nav/side-nav.component';
import { NavEntry, NavGroup, isNavGroup } from '../side-nav/nav-item.model';
import { HeaderComponent } from '../header/header.component';
import { FooterComponent } from '../footer/footer.component';

/**
 * 工作區（`/app/*`）的外框：側欄、頁首、頁尾與唯一一個放各頁的 outlet。它是 `/app` 的父路由元件，所以
 * App 只有一個 outlet，路由頁面只建立一次。以前 App 用 `@if (isWorkspace)` 在兩個 outlet 之間切換，從登入頁
 * 等非工作區頁面換頁進來（或反過來）時，頁面先建立在一個 outlet、接著又在另一個重建，多一次初始化與資料讀取，
 * 第一份還會在 DOM 裡停留一個畫格（issue #277、#322）。
 */
@Component({
  selector: 'app-workspace-layout',
  imports: [
    RouterOutlet,
    MatSidenavModule,
    SideNavComponent,
    HeaderComponent,
    FooterComponent,
  ],
  templateUrl: './workspace-layout.component.html',
  styleUrl: './workspace-layout.component.scss',
})
export class WorkspaceLayoutComponent implements OnInit {
  protected readonly t = ZH_TW;

  /** 側欄「案件」旁的逾期數字（issue #250）：shell 只有這個 signal，換頁時由動態載入的程式更新。 */
  protected readonly caseOverdueCount = signal(0);

  protected readonly navItems: NavEntry[] = [
    { route: '/app/home', label: '首頁', icon: 'home' },
    { route: '/app/assistants', label: '我的助理', icon: 'smart_toy' },
    { route: '/app/knowledge', label: '知識庫', icon: 'library_books' },
    { route: '/app/databases', label: '數據庫', icon: 'database' },
    { route: '/app/activity', label: '對話與回報紀錄', icon: 'forum' },
    { route: '/app/operations', label: '營運追蹤', icon: 'monitoring' },
    { route: '/app/issues', label: '處理事項', icon: 'task_alt' },
    { route: '/app/cases', label: '案件', icon: 'assignment', count: this.caseOverdueCount },
    { route: '/app/settings', label: '團隊與設定', icon: 'settings' },
  ];

  private readonly navLeaves = this.navItems.flatMap((entry) =>
    isNavGroup(entry) ? entry.children : [entry],
  );

  protected isMobile = false;
  protected isSidenavOpen = true;
  protected currentTitle = String(this.t.nav.dashboard);
  protected currentGroupLabel: string | null = null;
  protected openGroupLabel: string | null = null;
  protected collapsed = false;

  protected readonly flyoutPositions: ConnectedPosition[] = [
    { originX: 'end', originY: 'top', overlayX: 'start', overlayY: 'top', offsetX: 8 },
    { originX: 'end', originY: 'bottom', overlayX: 'start', overlayY: 'bottom', offsetX: 8 },
  ];

  @ViewChild(MatSidenavContainer) private sidenavContainer?: MatSidenavContainer;
  /**
   * `mat-sidenav-content` 是換頁時實際捲動的容器；Angular Router 的
   * scroll restoration 只作用在 window，管不到這個自訂捲動容器，所以要自己在
   * 每次導覽完成時把它捲回頂端（見 issue #66）。這裡不用瀏覽器原生的捲動還原，
   * 換頁一律回頂端，代價是放棄「上一頁」還原捲動位置，但目前這個容器本來就沒有
   * 任何還原機制，不算是 regression。
   */
  @ViewChild('contentArea', { read: ElementRef })
  private contentArea?: ElementRef<HTMLElement>;

  private readonly document = inject(DOCUMENT);
  private readonly breakpointObserver = inject(BreakpointObserver);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    // 外框是在導覽的啟用階段建立的，那次導覽的 NavigationEnd 會在建構之後、ngOnInit 之前送出；
    // 在這裡就訂閱，第一次進入工作區時標題與逾期數字才會跟著更新。離開工作區時外框被移除，要一起退訂。
    this.router.events
      .pipe(
        filter((event) => event.type === 1),
        map(() => this.router.url),
        takeUntilDestroyed(),
      )
      .subscribe((url) => {
        const active =
          this.navLeaves.find((item) => item.route === url) ??
          this.navLeaves
            .filter((item) => url.startsWith(`${item.route}/`))
            .sort((a, b) => b.route.length - a.route.length)[0];
        this.currentTitle = active?.label ?? this.t.nav.dashboard;

        const activeGroup = active
          ? this.navItems.find(
              (entry): entry is NavGroup => isNavGroup(entry) && entry.children.includes(active),
            )
          : undefined;
        this.currentGroupLabel = activeGroup?.label ?? null;

        if (activeGroup) {
          this.openGroupLabel = activeGroup.label;
        }

        this.resetContentScroll();
        this.refreshCaseOverdueCount(url);
      });
  }

  ngOnInit(): void {
    this.breakpointObserver.observe(['(max-width: 900px)']).pipe(takeUntilDestroyed(this.destroyRef)).subscribe((result) => {
      this.isMobile = result.matches;
      this.isSidenavOpen = !result.matches;
      if (this.isMobile) {
        this.collapsed = false;
      }
    });
  }

  /**
   * 換頁時更新逾期數字（決定 R：不設計時器）。讀取的程式以動態 `import()` 載入首頁的 lazy chunk：首頁為了
   * 「案件」卡片本來就帶著 `CasesRepository`，並轉出 `refreshCaseOverdueCount`；另開一個只用到 core／HTTP
   * 的新 chunk，會讓 esbuild 把初始 bundle 的共用 chunk 再切成兩塊（實測多出約 0.9 kB）。工作區以外不載入
   * （側欄也不顯示）；外部客戶與沒有身分時是 0、不發請求，由讀取的程式判斷。外框由 Router 建立、第一次
   * NavigationEnd 就會呼叫它，測試來不及替換執行個體上的欄位，所以它是 prototype 上的方法，由測試 spy 替換。
   */
  protected refreshCaseOverdueCount(url: string): void {
    if (url.startsWith('/app')) void import('../../features/home/home-page.component').then((m) => m.refreshCaseOverdueCount(this.injector, this.caseOverdueCount));
  }

  /**
   * 把主內容捲動容器捲回頂端。瀏覽器只會把舊的 `scrollTop` 夾在新內容的可捲動
   * 範圍內（新內容仍夠高就整段沿用舊位置），不會自動歸零，換頁後新頁面的標題
   * 因此可能被捲出可視範圍（issue #66）。
   */
  private resetContentScroll(): void {
    const content = this.contentArea?.nativeElement;
    if (content) {
      content.scrollTop = 0;
    }
  }

  /** 跳過導覽：直接把焦點移到主要內容，不依賴 fragment 連結的預設行為。 */
  protected skipToContent(event: Event): void {
    event.preventDefault();
    const main = this.document.getElementById('main-content');
    main?.focus();
    main?.scrollIntoView();
  }

  protected toggleSidenav(): void {
    this.isSidenavOpen = !this.isSidenavOpen;
  }

  protected toggleCollapsed(): void {
    this.collapsed = !this.collapsed;
    this.openGroupLabel = null;
  }

  protected onSidenavTransitionEnd(): void {
    this.sidenavContainer?.updateContentMargins();
  }

  protected onNavClick(): void {
    if (this.isMobile) {
      this.isSidenavOpen = false;
    }
  }

  protected toggleGroup(label: string): void {
    this.openGroupLabel = this.openGroupLabel === label ? null : label;
  }
}
