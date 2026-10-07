import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { DatabaseListPageComponent } from './features/databases/database-list/database-list-page.component';
import { demoSessionGuard } from './core/session/demo-session.guard';
import { embeddedChatGuard } from './core/session/embedded-chat.guard';
import { newAssistantDraftGuard } from './features/assistants/assistant-wizard/new-assistant-draft.guard';
import { routes } from './app.routes';

describe('app routes', () => {
  it('keeps the public landing, demo login, and workspace home at predictable paths', () => {
    expect(routes.find((route) => route.path === '')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'login')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/home')?.loadComponent).toBeDefined();
  });

  it('keeps each planned navigation destination addressable while it reuses the workspace placeholder', () => {
    expect(routes.find((route) => route.path === 'app/assistants')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/knowledge')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/databases')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/activity')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/channels')?.loadComponent).toBeDefined();
  });

  it('opens knowledge base details on a tab route, defaulting to the content tab', () => {
    expect(routes.find((route) => route.path === 'app/knowledge/:id/:tab')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/knowledge/:id')?.redirectTo).toBe(
      'app/knowledge/:id/content',
    );
  });

  it('opens database details on a tab route, defaulting to the form design tab', async () => {
    const databases = routes.find((route) => route.path === 'app/databases');
    expect(await databases?.loadComponent?.()).toBe(DatabaseListPageComponent);
    expect(routes.find((route) => route.path === 'app/databases/:id/:tab')?.loadComponent).toBeDefined();
    expect(routes.find((route) => route.path === 'app/databases/:id')?.redirectTo).toBe(
      'app/databases/:id/form',
    );
  });

  it('opens the in-workspace chat with its conversation history behind the demo session guard', async () => {
    const { WorkspaceChatPageComponent } = await import(
      './features/assistant-use/workspace-chat/workspace-chat-page.component'
    );
    for (const path of ['app/chat', 'app/chat/:assistantId', 'app/chat/:assistantId/:conversationId']) {
      const route = routes.find((candidate) => candidate.path === path);
      expect(route?.canActivate?.length).toBe(1);
      expect(await route?.loadComponent?.()).toBe(WorkspaceChatPageComponent);
    }
  });

  it('opens the end-user chat for an assistant without requiring a demo persona', async () => {
    const chat = routes.find((route) => route.path === 'chat/:assistantId');
    expect(chat?.canActivate).toEqual([embeddedChatGuard]);
    const { ChatShellPageComponent } = await import(
      './features/assistant-use/chat-shell/chat-shell-page.component'
    );
    expect(await chat?.loadComponent?.()).toBe(ChatShellPageComponent);
  });

  // issue #305：正式環境的 `/use/*` 屬於 API 的訪客對話頁，admin 的舊網址只剩轉址，不再載入任何畫面。
  it('redirects the legacy /use/:assistantId address to /chat/:assistantId, keeping the id and query', async () => {
    const legacy = routes.find((route) => route.path === 'use/:assistantId');
    expect(legacy?.redirectTo).toBe('chat/:assistantId');
    expect(legacy?.loadComponent).toBeUndefined();

    @Component({ template: '' })
    class ChatStubComponent {}
    TestBed.configureTestingModule({
      providers: [provideRouter([legacy as Route, { path: 'chat/:assistantId', component: ChatStubComponent }])],
    });
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/use/assistant-customer-service?embed=1');

    expect(router.url).toBe('/chat/assistant-customer-service?embed=1');
  });

  it('keeps every workspace route behind the demo session guard', () => {
    const workspaceRoutes = routes.filter(
      (route) => route.path?.startsWith('app/') === true && route.loadComponent !== undefined,
    );

    expect(workspaceRoutes.length).toBeGreaterThan(0);
    for (const route of workspaceRoutes) {
      expect(route.canActivate).toContain(demoSessionGuard);
    }
  });

  it('makes the legacy new-assistant step route redirect-only through its guard', () => {
    const newAssistantRoute = routes.find((route) => route.path === 'app/assistants/new/:step') as Route | undefined;
    expect(newAssistantRoute).toBeDefined();
    expect(newAssistantRoute?.loadComponent).toBeUndefined();
    expect(newAssistantRoute?.component).toBeUndefined();
    expect(newAssistantRoute?.canActivate).toContain(demoSessionGuard);
    expect(newAssistantRoute?.canActivate).toContain(newAssistantDraftGuard);
  });
});
