import { DatabaseListPageComponent } from './features/databases/database-list/database-list-page.component';
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
});
