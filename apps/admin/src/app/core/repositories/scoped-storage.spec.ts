import { createEmptyAssistantDraft } from '../domain/assistant-draft.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { createScopedStorage, type StorageIdentity } from './scoped-storage';

const ORG_A_ADMIN: StorageIdentity = { organizationId: 'org-a', accountId: 'account-a-admin' };
const ORG_A_ADMIN_2: StorageIdentity = { organizationId: 'org-a', accountId: 'account-a-admin-2' };
const ORG_B_ADMIN: StorageIdentity = { organizationId: 'org-b', accountId: 'account-b-admin' };

describe('createScopedStorage', () => {
  it('prefixes every key with the identity read at call time, not at wrap time', () => {
    const raw = createMemoryStorage();
    let current: StorageIdentity | null = ORG_A_ADMIN;
    const scoped = createScopedStorage(raw, () => current);

    scoped.setItem('k', 'from-a');
    // 換身分後再讀寫同一把 key：因為身分是每次呼叫時才讀，看到的是新身分的資料。
    current = ORG_B_ADMIN;
    expect(scoped.getItem('k')).toBeNull();
    scoped.setItem('k', 'from-b');
    expect(scoped.getItem('k')).toBe('from-b');

    current = ORG_A_ADMIN;
    expect(scoped.getItem('k')).toBe('from-a');
  });

  it('does not collide across organizations or across same-role accounts in the same organization', () => {
    const raw = createMemoryStorage();
    let current: StorageIdentity | null = ORG_A_ADMIN;
    const scoped = createScopedStorage(raw, () => current);

    scoped.setItem('draft', 'org-a-admin-draft');

    current = ORG_B_ADMIN;
    expect(scoped.getItem('draft')).toBeNull();

    current = ORG_A_ADMIN_2;
    expect(scoped.getItem('draft')).toBeNull();

    current = ORG_A_ADMIN;
    expect(scoped.getItem('draft')).toBe('org-a-admin-draft');
  });

  it('removes only the current identity’s copy of a key', () => {
    const raw = createMemoryStorage();
    let current: StorageIdentity | null = ORG_A_ADMIN;
    const scoped = createScopedStorage(raw, () => current);

    scoped.setItem('k', 'a');
    current = ORG_B_ADMIN;
    scoped.setItem('k', 'b');

    scoped.removeItem('k');
    expect(scoped.getItem('k')).toBeNull();
    current = ORG_A_ADMIN;
    expect(scoped.getItem('k')).toBe('a');
  });

  it('falls back to the unprefixed key when there is no identity (not logged in yet)', () => {
    const raw = createMemoryStorage();
    const scoped = createScopedStorage(raw, () => null);

    scoped.setItem('k', 'v');
    expect(raw.getItem('k')).toBe('v');
  });
});

describe('MockDemoRepository storage isolation (via a scoped storage double, API mode)', () => {
  /**
   * 驗收條件：同一個 storage 替身中，組織 A 的 admin 建立的草稿，對組織 B 的 admin，
   * 以及同組織中另一位同角色的帳號都不可見；切回 A 的 admin 仍然看得到。
   *
   * 這裡直接把 `createScopedStorage` 包住的 storage 傳給 `MockDemoRepository`
   * （而不是透過 `HybridDemoRepository`），因為 `MockDemoRepository` 完全不知道
   * 自己拿到的 `options.storage` 是不是被包過；`HybridDemoRepository` 的走線在
   * hybrid-demo-repository.spec.ts 另外驗證。
   */
  it('keeps one org/account’s draft invisible to another org and to a same-role account in the same org', () => {
    const raw = createMemoryStorage();
    let current: StorageIdentity | null = ORG_A_ADMIN;
    const storage = createScopedStorage(raw, () => current);
    const repository = new MockDemoRepository(DEMO_SEED, { storage });

    const draft = { ...createEmptyAssistantDraft(), name: '組織 A 的草稿' };
    expect(repository.saveAssistantDraft('account-smb-admin', draft)).toMatchObject({
      status: 'ready',
    });
    expect(repository.getAssistantDraft('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });

    // 組織 B 的 admin：同一個 Demo 身分 id（`account-smb-admin`），但真實身分不同。
    current = ORG_B_ADMIN;
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });

    // 同組織中另一位同角色帳號：真實 accountId 不同，即使 organizationId 相同。
    current = ORG_A_ADMIN_2;
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });

    // 切回組織 A 的原帳號：草稿仍然看得到。
    current = ORG_A_ADMIN;
    expect(repository.getAssistantDraft('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });
  });
});
