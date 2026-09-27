import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED, type DemoSeed } from '../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantListPageComponent } from './assistant-list-page.component';

/** 「我建立的」讀取助理設定時一律拋出例外，用來驗證 `error` 狀態不需要真的有 API 才能觸發。 */
class ThrowingAssistantConfigurationsRepository extends MockDemoRepository {
  override listAssistantConfigurations(): never {
    throw new Error('boom: simulated unexpected failure');
  }
}

async function render(
  accountId: AccountId,
  options: { readonly repository?: MockDemoRepository; readonly seed?: DemoSeed } = {},
): Promise<{ readonly page: HTMLElement; readonly repository: MockDemoRepository }> {
  const repository = options.repository ?? new MockDemoRepository(options.seed ?? DEMO_SEED, { storage: createMemoryStorage() });

  await TestBed.configureTestingModule({
    imports: [AssistantListPageComponent],
    providers: [
      provideRouter([]),
      { provide: DemoSessionService, useValue: { activeAccountId: () => accountId } },
      { provide: DEMO_REPOSITORY, useValue: repository },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(AssistantListPageComponent);
  fixture.detectChanges();
  return { page: fixture.nativeElement as HTMLElement, repository };
}

describe('AssistantListPageComponent', () => {
  it('separates assistants owned by the account from company assistants it may use', async () => {
    const seed: DemoSeed = {
      ...DEMO_SEED,
      assistants: [
        ...DEMO_SEED.assistants,
        {
          id: 'assistant-internal-onboarding',
          ownerAccountId: 'account-internal-employee',
          name: '內部新手助理',
          purpose: '協助新同仁熟悉客服處理流程',
          status: 'draft',
          audience: 'account-members',
          sharedWithAccountIds: [],
          knowledgeBaseIds: [],
          databaseIds: [],
          keepOwnConversations: true,
        },
      ],
    };

    const { page } = await render('account-internal-employee', { seed });

    expect(page.textContent).toContain('內部新手助理');
    expect(page.textContent).toContain('我建立的');
    expect(page.textContent).toContain('組織建立的');
    expect(page.textContent).toContain('客服助理');
    expect(page.querySelector('a[href="/app/chat/assistant-customer-service"]')?.textContent).toContain('對話');
  });

  it('offers 建立新助理 to an account allowed to manage assistants', async () => {
    const { page } = await render('account-smb-admin');
    expect(page.querySelector('.page-header__actions a[href="/app/assistants/new/purpose"]')?.textContent).toContain('建立新助理');
  });

  it('hides 建立新助理 from an account without manage-assistants', async () => {
    const { page } = await render('account-internal-employee');
    expect(page.querySelector('a[href="/app/assistants/new/purpose"]')).toBeNull();
    expect(page.textContent).not.toContain('建立新助理');
  });

  it('shows a truly empty state when the account has no assistants and no drafts', async () => {
    const { page } = await render('account-external-customer');

    expect(page.textContent).toContain('你還沒有建立助理');
    expect(page.querySelector('[data-state]')).toBeNull();
  });

  it('shows a loading state instead of claiming the account has no assistants', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    repository.setScenario('loading');
    const { page } = await render('account-smb-admin', { repository });

    expect(page.textContent).toContain('正在載入你建立的助理');
    expect(page.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(page.textContent).not.toContain('你還沒有建立助理');
  });

  it('shows a permission-denied state instead of claiming the account has no assistants', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    repository.setScenario('permission-denied');
    const { page } = await render('account-smb-admin', { repository });

    expect(page.querySelector('[role="alert"]')?.textContent).toContain('無法查看你建立的助理');
    expect(page.textContent).not.toContain('你還沒有建立助理');
  });

  it('does not show permission-denied just because the account cannot manage drafts', async () => {
    // account-internal-employee 沒有 manage-assistants，草稿一律 permission-denied，
    // 但這是結構性限制而非需要中斷畫面的錯誤，仍應維持顯示「你還沒有建立助理」。
    const { page } = await render('account-internal-employee');

    expect(page.querySelector('[role="alert"]')).toBeNull();
    expect(page.textContent).toContain('你還沒有建立助理');
  });

  it('shows an error state (not the empty message) when reading assistants throws unexpectedly', async () => {
    const repository = new ThrowingAssistantConfigurationsRepository(DEMO_SEED, { storage: createMemoryStorage() });
    const { page } = await render('account-smb-admin', { repository });

    expect(page.querySelector('[role="alert"]')?.textContent).toContain('目前無法載入你建立的助理');
    expect(page.textContent).not.toContain('你還沒有建立助理');
  });

  it('keeps the assistants and drafts that did load when the repository reports a partial failure', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    repository.setScenario('partial-failure');
    const { page } = await render('account-smb-admin', { repository });

    expect(page.textContent).toContain('客服助理');
    expect(page.textContent).toContain('內部教育訓練助理');
    expect(page.querySelector('[role="status"].partial-notice')?.textContent).toContain('部分知識庫同步暫時無法讀取');
    expect(page.textContent).not.toContain('你還沒有建立助理');
  });
});
