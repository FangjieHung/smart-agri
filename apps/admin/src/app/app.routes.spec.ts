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
});
