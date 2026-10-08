import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { ApiSessionService } from './core/session/api-session.service';
import { changePasswordGuard } from './core/session/change-password.guard';
import { demoSessionGuard } from './core/session/demo-session.guard';
import { embeddedChatGuard } from './core/session/embedded-chat.guard';
import { existingAssistantDraftGuard, newAssistantDraftGuard } from './features/assistants/assistant-wizard/new-assistant-draft.guard';
import { WorkspaceLayoutComponent } from './layout/workspace-layout/workspace-layout.component';

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
  // 工作區：外框是父路由元件，各頁是它的子路由，App 只有一個 outlet，換頁進出工作區時頁面只建立一次（issue #322）。
  {
    path: 'app',
    component: WorkspaceLayoutComponent,
    children: [
      {
        path: 'home',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/home/home-page.component').then((m) => m.HomePageComponent),
      },
      {
        path: 'assistants',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/assistants/assistant-list/assistant-list-page.component').then(
            (m) => m.AssistantListPageComponent,
          ),
      },
      { path: 'assistants/new', redirectTo: 'assistants/new/purpose', pathMatch: 'full' },
      {
        path: 'assistants/new/:step',
        canActivate: [demoSessionGuard, newAssistantDraftGuard],
        children: [],
      },
      {
        path: 'assistants/drafts/:draftId/:step',
        canActivate: [demoSessionGuard, existingAssistantDraftGuard],
        loadComponent: () =>
          import('./features/assistants/assistant-wizard/assistant-wizard-page.component').then(
            (m) => m.AssistantWizardPageComponent,
          ),
      },
      {
        path: 'assistants/:id/:tab',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/assistants/assistant-detail/assistant-detail-page.component').then(
            (m) => m.AssistantDetailPageComponent,
          ),
      },
      {
        path: 'knowledge',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/knowledge/knowledge-list/knowledge-list-page.component').then(
            (m) => m.KnowledgeListPageComponent,
          ),
      },
      { path: 'knowledge/:id', redirectTo: 'knowledge/:id/content', pathMatch: 'full' },
      {
        path: 'knowledge/:id/:tab',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/knowledge/knowledge-detail/knowledge-detail-page.component').then(
            (m) => m.KnowledgeDetailPageComponent,
          ),
      },
      {
        path: 'databases',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/databases/database-list/database-list-page.component').then(
            (m) => m.DatabaseListPageComponent,
          ),
      },
      { path: 'databases/:id', redirectTo: 'databases/:id/form', pathMatch: 'full' },
      {
        path: 'databases/:id/:tab',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/databases/database-detail/database-detail-page.component').then(
            (m) => m.DatabaseDetailPageComponent,
          ),
      },
      // 表單連結（issue #145）：有 `submit-authorized-forms` 的帳號填寫、明確同意後送出並取得回執。
      {
        path: 'forms/:databaseId',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/databases/database-submission/database-submission-page.component').then(
            (m) => m.DatabaseSubmissionPageComponent,
          ),
      },
      {
        path: 'activity',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/activity/activity-page.component').then((m) => m.ActivityPageComponent),
      },
      {
        path: 'operations',
        canActivate: [demoSessionGuard],
        loadComponent: () => import('./features/operations/operations-summary/operations-summary-page.component').then((m) => m.OperationsSummaryPageComponent),
      },
      {
        path: 'issues',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/issues/issues-page.component').then((m) => m.IssuesPageComponent),
      },
      {
        path: 'cases',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/cases/cases-page.component').then((m) => m.CasesPageComponent),
      },
      {
        path: 'channels',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/publishing/channel-overview/channel-overview-page.component').then(
            (m) => m.ChannelOverviewPageComponent,
          ),
      },
      {
        path: 'chat',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
            (m) => m.WorkspaceChatPageComponent,
          ),
      },
      {
        path: 'chat/:assistantId',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
            (m) => m.WorkspaceChatPageComponent,
          ),
      },
      {
        path: 'chat/:assistantId/:conversationId',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/assistant-use/workspace-chat/workspace-chat-page.component').then(
            (m) => m.WorkspaceChatPageComponent,
          ),
      },
      {
        path: 'settings',
        canActivate: [demoSessionGuard],
        loadComponent: () =>
          import('./features/settings/pages/settings-page.component').then(
            (m) => m.SettingsPageComponent,
          ),
      },
      // `/app` 本身與打錯的工作區網址：有外框的父路由在子路由都對不上時仍會成立（只剩空白的外框），
      // 所以在這裡轉回首頁，跟以前一樣由最後的 `**` 落到 `''`。
      { path: '**', redirectTo: '/' },
    ],
  },
  // 使用助理的單欄對話頁（mock 模式也是嵌入官網與 LINE 的 Demo 入口）：未登入訪客也要能直接開啟，
  // 所以不掛工作區守衛。正式環境的 `/use/*` 屬於 API 提供的訪客對話頁（issue #305），admin 的舊網址只轉址。
  {
    path: 'chat/:assistantId',
    canActivate: [embeddedChatGuard],
    loadComponent: () =>
      import('./features/assistant-use/chat-shell/chat-shell-page.component').then(
        (m) => m.ChatShellPageComponent,
      ),
  },
  { path: 'use/:assistantId', redirectTo: 'chat/:assistantId', pathMatch: 'full' },
  { path: 'settings', redirectTo: 'app/settings', pathMatch: 'full' },
  { path: 'dashboard', redirectTo: 'app/home', pathMatch: 'full' },
  { path: '**', redirectTo: '' },
];
