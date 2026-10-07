import { InjectionToken } from '@angular/core';
import type { ChatRunner } from './chat-runner';

/**
 * API 模式的 ChatRunner（AG-UI），由 `provideApiMode()` 提供；mock 建置（含 GitHub Pages）是 null，
 * `CHAT_RUNNER` 就改用 Mock。放在獨立檔案（issue #308）：`chat-runner.ts` 為了預設值靜態匯入
 * `MockChatRunner`，`provideApiMode()` 若直接覆寫 `CHAT_RUNNER`，會把它帶進 API 模式的初始 bundle。
 */
export const API_CHAT_RUNNER = new InjectionToken<ChatRunner | null>('API_CHAT_RUNNER', {
  providedIn: 'root',
  factory: () => null,
});
