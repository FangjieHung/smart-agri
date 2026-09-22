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
    loadComponent: () =>
      import('./features/home/home-page.component').then((m) => m.HomePageComponent),
  },
  {
    path: 'app/assistants',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistants/assistant-list/assistant-list-page.component').then(
        (m) => m.AssistantListPageComponent,
      ),
  },
  { path: 'app/assistants/new', redirectTo: 'app/assistants/new/purpose', pathMatch: 'full' },
  {
    path: 'app/assistants/new/:step',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistants/assistant-wizard/assistant-wizard-page.component').then(
        (m) => m.AssistantWizardPageComponent,
      ),
  },
  {
    path: 'app/assistants/:id/:tab',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistants/assistant-detail/assistant-detail-page.component').then(
        (m) => m.AssistantDetailPageComponent,
      ),
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
