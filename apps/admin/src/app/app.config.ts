import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { ThemeService } from '@smart-agri/theme-pack';
import { loadMockRepositoryModules } from './core/repositories/tokens';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideAppInitializer(() => inject(ThemeService).init()),
    // 必須排在 environment.providers 之前：API 模式的 initializer 會注入 repository。
    provideAppInitializer(loadMockRepositoryModules),
    // API 模式（`--configuration=api`）才有 HttpClient、bearer／401 攔截器與真實登入。
    ...environment.providers,
  ],
};
