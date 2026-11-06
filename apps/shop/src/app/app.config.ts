import { provideZonelessChangeDetection, type ApplicationConfig } from '@angular/core';

// 不用 Angular Router 與 HttpClient：頁面只有五個（state/nav.ts 自己處理 history），API 用 fetch（api/ordering-api.ts），
// 初始 bundle 因此保持在 200 kB 以內。
export const appConfig: ApplicationConfig = {
  providers: [provideZonelessChangeDetection()],
};
