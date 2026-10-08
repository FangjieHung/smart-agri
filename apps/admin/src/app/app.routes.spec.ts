import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { DatabaseListPageComponent } from './features/databases/database-list/database-list-page.component';
import { demoSessionGuard } from './core/session/demo-session.guard';
import { embeddedChatGuard } from './core/session/embedded-chat.guard';
import { newAssistantDraftGuard } from './features/assistants/assistant-wizard/new-assistant-draft.guard';
import { WorkspaceLayoutComponent } from './layout/workspace-layout/workspace-layout.component';
import { routes } from './app.routes';

/** 工作區的頁面是 `/app` 外框的子路由（issue #322）。 */
const workspace = routes.find((route) => route.path === 'app') as Route;
const workspaceRoutes = workspace.children ?? [];

/** 用完整網址樣式找路由：`app/...` 到外框的子路由裡找，其餘找最上層。 */
function routeAt(path: string): Route | undefined {
  return path.startsWith('app/')
    ? workspaceRoutes.find((route) => route.path === path.slice('app/'.length))
    : routes.find((route) => route.path === path);
}

describe('app routes', () => {
  it('keeps the public landing, demo login, and workspace home at predictable paths', () => {
    expect(routeAt('')?.loadComponent).toBeDefined();
    expect(routeAt('login')?.loadComponent).toBeDefined();
    expect(routeAt('app/home')?.loadComponent).toBeDefined();
  });

  it('keeps each planned navigation destination addressable while it reuses the workspace placeholder', () => {
    expect(routeAt('app/assistants')?.loadComponent).toBeDefined();
    expect(routeAt('app/knowledge')?.loadComponent).toBeDefined();
    expect(routeAt('app/databases')?.loadComponent).toBeDefined();
    expect(routeAt('app/activity')?.loadComponent).toBeDefined();
    expect(routeAt('app/channels')?.loadComponent).toBeDefined();
  });

  it('opens knowledge base details on a tab route, defaulting to the content tab', () => {
    expect(routeAt('app/knowledge/:id/:tab')?.loadComponent).toBeDefined();
    // 子路由的轉址相對於 `/app`：實際網址見下面用真 Router 的「轉址」測試。
    expect(routeAt('app/knowledge/:id')?.redirectTo).toBe('knowledge/:id/content');
  });

  it('opens database details on a tab route, defaulting to the form design tab', async () => {
    const databases = routeAt('app/databases');
    expect(await databases?.loadComponent?.()).toBe(DatabaseListPageComponent);
    expect(routeAt('app/databases/:id/:tab')?.loadComponent).toBeDefined();
    expect(routeAt('app/databases/:id')?.redirectTo).toBe('databases/:id/form');
  });

  it('opens the in-workspace chat with its conversation history behind the demo session guard', async () => {
    const { WorkspaceChatPageComponent } = await import(
      './features/assistant-use/workspace-chat/workspace-chat-page.component'
    );
    for (const path of ['app/chat', 'app/chat/:assistantId', 'app/chat/:assistantId/:conversationId']) {
      const route = routeAt(path);
      expect(route?.canActivate?.length).toBe(1);
      expect(await route?.loadComponent?.()).toBe(WorkspaceChatPageComponent);
    }
  });

  it('opens the end-user chat for an assistant without requiring a demo persona', async () => {
    const chat = routeAt('chat/:assistantId');
    expect(chat?.canActivate).toEqual([embeddedChatGuard]);
    const { ChatShellPageComponent } = await import(
      './features/assistant-use/chat-shell/chat-shell-page.component'
    );
    expect(await chat?.loadComponent?.()).toBe(ChatShellPageComponent);
  });

  // issue #305：正式環境的 `/use/*` 屬於 API 的訪客對話頁，admin 的舊網址只剩轉址，不再載入任何畫面。
  it('redirects the legacy /use/:assistantId address to /chat/:assistantId, keeping the id and query', async () => {
    const legacy = routeAt('use/:assistantId');
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
    const pages = workspaceRoutes.filter((route) => route.loadComponent !== undefined);

    expect(pages.length).toBeGreaterThan(0);
    for (const route of pages) {
      expect(route.canActivate).toContain(demoSessionGuard);
    }
  });

  // issue #322：工作區的外框是 `/app` 的父路由元件，各頁都是它的子路由，App 只需要一個 outlet。
  it('renders every workspace page inside the workspace layout route', () => {
    expect(workspace.component).toBe(WorkspaceLayoutComponent);
    // 守衛留在各頁：外框本身不擋，只有頁面通過守衛後才會建立外框。
    expect(workspace.canActivate).toBeUndefined();
    expect(routes.filter((route) => route.path?.startsWith('app') === true)).toEqual([workspace]);
    expect(workspaceRoutes.map((route) => route.path)).toContain('home');
    expect(workspaceRoutes.map((route) => route.path)).toContain('settings');
  });

  it('keeps the workspace redirects and the fallback for unknown workspace addresses (issue #322)', async () => {
    @Component({ template: '' })
    class StubComponent {}
    const redirects = workspaceRoutes.filter((route) => route.redirectTo !== undefined);
    expect(redirects.length).toBe(4);
    expect(workspaceRoutes.at(-1)).toEqual({ path: '**', redirectTo: '/' });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: StubComponent },
          {
            path: 'app',
            component: StubComponent,
            children: [
              { path: 'home', component: StubComponent },
              { path: 'settings', component: StubComponent },
              { path: 'assistants/new/:step', component: StubComponent },
              { path: 'knowledge/:id/:tab', component: StubComponent },
              { path: 'databases/:id/:tab', component: StubComponent },
              ...redirects,
            ],
          },
          ...routes.filter((route) => ['settings', 'dashboard', '**'].includes(route.path ?? '')),
        ]),
      ],
    });
    const router = TestBed.inject(Router);

    const landsOn = async (url: string) => {
      await router.navigateByUrl(url);
      return router.url;
    };
    expect(await landsOn('/app/assistants/new')).toBe('/app/assistants/new/purpose');
    expect(await landsOn('/app/knowledge/kb-1')).toBe('/app/knowledge/kb-1/content');
    expect(await landsOn('/app/databases/db-1')).toBe('/app/databases/db-1/form');
    // `/app` 本身與打錯的工作區網址跟以前一樣回到首頁，不會只顯示空白的外框。
    expect(await landsOn('/app')).toBe('/');
    expect(await landsOn('/app/no-such-page')).toBe('/');
    expect(await landsOn('/dashboard')).toBe('/app/home');
    expect(await landsOn('/settings')).toBe('/app/settings');
  });

  it('makes the legacy new-assistant step route redirect-only through its guard', () => {
    const newAssistantRoute = routeAt('app/assistants/new/:step') as Route | undefined;
    expect(newAssistantRoute).toBeDefined();
    expect(newAssistantRoute?.loadComponent).toBeUndefined();
    expect(newAssistantRoute?.component).toBeUndefined();
    expect(newAssistantRoute?.canActivate).toContain(demoSessionGuard);
    expect(newAssistantRoute?.canActivate).toContain(newAssistantDraftGuard);
  });
});
