import { computed, Injectable, inject, type Signal } from '@angular/core';
import { ApiSessionService } from './api-session.service';

/**
 * 「是否可以建立新助理」的單一判斷來源；首頁與助理清單共用，避免兩處各自維護、
 * 行為不一致（見 `docs/reviews/2026-09-26-project-review-and-backlog.md`）。
 *
 * 與 repository 的 `createNamedAssistantDraft` 同一條權限：`ApiSessionService.permissions()`
 * 在 API 模式讀 `/me`、mock 模式讀 `listAccounts()`，這裡不用分別處理兩種模式。
 */
@Injectable({ providedIn: 'root' })
export class AssistantPermissionsService {
  private readonly apiSession = inject(ApiSessionService);

  readonly canCreateAssistant: Signal<boolean> = computed(() =>
    this.apiSession.permissions().includes('manage-assistants'),
  );
}
