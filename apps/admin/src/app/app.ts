import { DOCUMENT } from '@angular/common';
import { Component, inject, OnInit, ViewChild } from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { ConnectedPosition } from '@angular/cdk/overlay';
import { MatSidenavContainer, MatSidenavModule } from '@angular/material/sidenav';
import { Router, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs/operators';
import { ZH_TW } from './core/i18n/zh-tw';
import { SideNavComponent } from './layout/side-nav/side-nav.component';
import { NavEntry, NavGroup, isNavGroup } from './layout/side-nav/nav-item.model';
import { HeaderComponent } from './layout/header/header.component';
import { FooterComponent } from './layout/footer/footer.component';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    MatSidenavModule,
    SideNavComponent,
    HeaderComponent,
    FooterComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly t = ZH_TW;

  protected readonly navItems: NavEntry[] = [
    { route: '/app/home', label: '首頁', icon: 'home' },
    { route: '/app/assistants', label: '我的助理', icon: 'smart_toy' },
    { route: '/app/knowledge', label: '知識庫', icon: 'library_books' },
    { route: '/app/databases', label: '數據庫', icon: 'database' },
    { route: '/app/activity', label: '對話與回報紀錄', icon: 'forum' },
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
  protected isWorkspace = false;

  protected readonly flyoutPositions: ConnectedPosition[] = [
    { originX: 'end', originY: 'top', overlayX: 'start', overlayY: 'top', offsetX: 8 },
    { originX: 'end', originY: 'bottom', overlayX: 'start', overlayY: 'bottom', offsetX: 8 },
  ];

  @ViewChild(MatSidenavContainer) private sidenavContainer?: MatSidenavContainer;

  private readonly document = inject(DOCUMENT);
  private readonly breakpointObserver = inject(BreakpointObserver);
  private readonly router = inject(Router);

  ngOnInit(): void {
    this.isWorkspace = this.router.url.startsWith('/app');
    this.breakpointObserver.observe(['(max-width: 900px)']).subscribe((result) => {
      this.isMobile = result.matches;
      this.isSidenavOpen = !result.matches;
      if (this.isMobile) {
        this.collapsed = false;
      }
    });

    this.router.events
      .pipe(
        filter((event) => event.type === 1),
        map(() => this.router.url),
      )
      .subscribe((url) => {
        this.isWorkspace = url.startsWith('/app');
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
      });
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
