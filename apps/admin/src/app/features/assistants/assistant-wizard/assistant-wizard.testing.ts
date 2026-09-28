import { signal, type EnvironmentProviders, type Provider } from '@angular/core';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import type { AssistantDraft } from '../../../core/domain/assistant-draft.model';
import type { DemoKeyValueStorage } from '../../../core/repositories/demo-repository';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/**
 * 單元測試用：以記憶體儲存的 mock repository 與固定帳號組裝精靈依賴，並先建立一份具名草稿
 * （精靈只編輯具名草稿，網址一定帶 `draftId`）。`draft` 有值時把它存進這份草稿。
 * mock 的 Observable 是 `defer(of)`，訂閱當下就同步完成，所以這裡可以同步準備資料。
 */
export function provideWizardTesting(
  options: {
    readonly storage?: DemoKeyValueStorage;
    readonly accountId?: AccountId;
    readonly draft?: AssistantDraft;
  } = {},
): {
  readonly providers: (Provider | EnvironmentProviders)[];
  readonly repository: MockDemoRepository;
  readonly draftId: string;
} {
  const activeAccountId = signal<AccountId | null>(
    options.accountId ?? 'account-smb-admin',
  );
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: options.storage ?? createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    // 非同步契約的方法由 repository 的 `viewer` 選項推導目前帳號，
    // 要與上面 `DemoSessionService` 的假身分一致。
    viewer: () => activeAccountId(),
  });

  let draftId = 'draft-missing';
  repository.createNamedAssistantDraft().subscribe((result) => {
    if (result.status === 'ready') draftId = result.data.id;
  });
  if (options.draft !== undefined) {
    repository.saveNamedAssistantDraft(draftId, options.draft, 1).subscribe();
  }
  const paramMap = convertToParamMap({ draftId });

  return {
    repository,
    draftId,
    providers: [
      provideRouter([]),
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId } },
      // 步驟元件的單元測試直接提供 store（不經過路由），這裡補上網址參數。
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap }, paramMap: of(paramMap) } },
    ],
  };
}
