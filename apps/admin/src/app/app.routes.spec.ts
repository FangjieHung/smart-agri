import { DatabaseListPageComponent } from './features/databases/database-list/database-list-page.component';
import { demoSessionGuard } from './core/session/demo-session.guard';
import { embeddedChatGuard } from './core/session/embedded-chat.guard';
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
    const use = routes.find((route) => route.path === 'use/:assistantId');
    expect(use?.canActivate).toEqual([embeddedChatGuard]);
    const { ChatShellPageComponent } = await import(
      './features/assistant-use/chat-shell/chat-shell-page.component'
    );
    expect(await use?.loadComponent?.()).toBe(ChatShellPageComponent);
  });

  it('keeps every workspace route behind the demo session guard', () => {
    const workspaceRoutes = routes.filter(
      (route) => route.path?.startsWith('app/') === true && route.loadComponent !== undefined,
    );

    expect(workspaceRoutes.length).toBeGreaterThan(0);
    for (const route of workspaceRoutes) {
      expect(route.canActivate).toEqual([demoSessionGuard]);
    }
  });
});
