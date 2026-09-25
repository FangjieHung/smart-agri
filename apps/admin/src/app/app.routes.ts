import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { ApiSessionService } from './core/session/api-session.service';
import { changePasswordGuard } from './core/session/change-password.guard';
import { demoSessionGuard } from './core/session/demo-session.guard';
import { embeddedChatGuard } from './core/session/embedded-chat.guard';
import { existingAssistantDraftGuard, newAssistantDraftGuard } from './features/assistants/assistant-wizard/new-assistant-draft.guard';

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
  // OIDC 回呼只存在於 API 模式；mock 模式照舊落到最後的 `**`。
  {
    path: 'auth/callback',
    canMatch: [() => inject(ApiSessionService).apiMode],
    loadComponent: () =>
      import('./features/auth-callback/auth-callback-page.component').then(
        (m) => m.AuthCallbackPageComponent,
      ),
  },
  // 設定新密碼只存在於 API 模式；mock 模式沒有這個狀態，落到最後的 `**`。
  {
    path: 'change-password',
    canMatch: [() => inject(ApiSessionService).apiMode],
    canActivate: [changePasswordGuard],
    loadComponent: () =>
      import('./features/change-password/change-password-page.component').then(
        (m) => m.ChangePasswordPageComponent,
      ),
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
    canActivate: [demoSessionGuard, newAssistantDraftGuard],
    children: [],
  },
  {
    path: 'app/assistants/drafts/:draftId/:step',
    canActivate: [demoSessionGuard, existingAssistantDraftGuard],
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
    loadComponent: () =>
      import('./features/knowledge/knowledge-list/knowledge-list-page.component').then(
        (m) => m.KnowledgeListPageComponent,
      ),
  },
  { path: 'app/knowledge/:id', redirectTo: 'app/knowledge/:id/content', pathMatch: 'full' },
  {
    path: 'app/knowledge/:id/:tab',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/knowledge/knowledge-detail/knowledge-detail-page.component').then(
        (m) => m.KnowledgeDetailPageComponent,
      ),
  },
  {
    path: 'app/databases',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/databases/database-list/database-list-page.component').then(
        (m) => m.DatabaseListPageComponent,
      ),
  },
  { path: 'app/databases/:id', redirectTo: 'app/databases/:id/form', pathMatch: 'full' },
  {
    path: 'app/databases/:id/:tab',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/databases/database-detail/database-detail-page.component').then(
        (m) => m.DatabaseDetailPageComponent,
      ),
  },
  {
    path: 'app/activity',
    canActivate: [demoSessionGuard],
    loadComponent: workspacePlaceholder,
  },
  {
    path: 'app/channels',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/publishing/channel-overview/channel-overview-page.component').then(
        (m) => m.ChannelOverviewPageComponent,
      ),
  },
  {
    path: 'app/chat',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
        (m) => m.WorkspaceChatPageComponent,
      ),
  },
  {
    path: 'app/chat/:assistantId',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
        (m) => m.WorkspaceChatPageComponent,
      ),
  },
  {
    path: 'app/chat/:assistantId/:conversationId',
    canActivate: [demoSessionGuard],
    loadComponent: () =>
      import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
        (m) => m.WorkspaceChatPageComponent,
      ),
  },
  // 嵌入客戶官網與 LINE 的入口：未登入訪客也要能直接開啟，所以不掛工作區守衛。
  {
    path: 'use/:assistantId',
    canActivate: [embeddedChatGuard],
    loadComponent: () =>
      import('./features/assistant-use/chat-shell/chat-shell-page.component').then(
        (m) => m.ChatShellPageComponent,
      ),
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
