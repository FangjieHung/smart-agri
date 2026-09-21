import { Component } from '@angular/core';
import { PageToolbarComponent } from '../../../shared/ui/page-toolbar.component';
import { HeaderToolbarDirective } from '../../../layout/header/header-toolbar-slot';

@Component({
  selector: 'app-dashboard-page',
  imports: [PageToolbarComponent, HeaderToolbarDirective],
  templateUrl: './dashboard-page.component.html',
  styleUrls: ['../../../app.scss'],
})
export class DashboardPageComponent {}
