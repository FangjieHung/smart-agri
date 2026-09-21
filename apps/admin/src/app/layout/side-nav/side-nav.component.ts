import { Component, inject, input, output } from '@angular/core';
import { ConnectedPosition, OverlayModule } from '@angular/cdk/overlay';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ZH_TW } from '../../core/i18n/zh-tw';
import { NavEntry, NavGroup, isNavGroup } from './nav-item.model';

@Component({
  selector: 'app-side-nav',
  imports: [
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatMenuModule,
    MatTooltipModule,
    OverlayModule,
  ],
  templateUrl: './side-nav.component.html',
  styleUrl: './side-nav.component.scss',
})
export class SideNavComponent {
  protected readonly t = ZH_TW;
  protected readonly isNavGroup = isNavGroup;
  protected readonly auth = inject(AuthService);

  readonly navItems = input.required<NavEntry[]>();
  readonly collapsed = input.required<boolean>();
  readonly isMobile = input.required<boolean>();
  readonly openGroupLabel = input.required<string | null>();
  readonly currentGroupLabel = input.required<string | null>();
  readonly flyoutPositions = input.required<ConnectedPosition[]>();

  readonly toggleCollapse = output<void>();
  readonly toggleGroup = output<string>();
  readonly navClick = output<void>();
  readonly groupDetach = output<void>();

  protected isGroupActive(group: NavGroup): boolean {
    return this.currentGroupLabel() === group.label;
  }
}
