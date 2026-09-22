import { Routes } from '@angular/router';
import { demoSessionGuard } from './core/session/demo-session.guard';

const workspacePlaceholder = () =>
  import('./features/dashboard/pages/dashboard-page.component').then(
    (m) => m.DashboardPageComponent,
  );

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./features/landing/landing-page.component').then((m) => m.LandingPageComponent),
  },
  {
    path: 'login',
    loadComponent: () =>
      import('./features/demo-login/demo-login-page.component').then((m) => m.DemoLoginPageComponent),
  },
  {
    path: 'app/home',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/assistants',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/knowledge',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/databases',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/activity',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/channels',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/settings',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/settings/pages/settings-page.component').then(
        (m) => m.SettingsPageComponent,
      ),
  },
  { path: 'settings', redirectTo: 'app/settings', pathMatch: 'full' },
  { path: 'dashboard', redirectTo: 'app/home', pathMatch: 'full' },
  { path: '**', redirectTo: '' },
];
