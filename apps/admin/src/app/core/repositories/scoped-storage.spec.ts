import { firstValueFrom } from 'rxjs';
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

  it('fails closed when there is no identity: never reads or writes the shared unprefixed keys', () => {
    const raw = createMemoryStorage();
    // 改版前 API 模式所有帳號共用的鍵值：不能在「尚未取得身分」的空檔被讀出來。
    raw.setItem('k', 'left over from another account');
    const scoped = createScopedStorage(raw, () => null);

    expect(scoped.getItem('k')).toBeNull();
    scoped.setItem('k', 'v');
    scoped.removeItem('k');
    expect(raw.getItem('k')).toBe('left over from another account');
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
  it('keeps one org/account’s draft invisible to another org and to a same-role account in the same org', async () => {
    const raw = createMemoryStorage();
    let current: StorageIdentity | null = ORG_A_ADMIN;
    const storage = createScopedStorage(raw, () => current);
    // 命名草稿的擁有者由 `viewer` 選項推導（不再由呼叫端傳入），所以這裡固定用同一個
    // Demo 身分 id；不同組織／帳號的隔離改由 `storage` 的 scoping 驗證。
    const repository = new MockDemoRepository(DEMO_SEED, { storage, viewer: () => 'account-smb-admin' });

    const created = await firstValueFrom(repository.createNamedAssistantDraft());
    if (created.status !== 'ready') throw new Error(`expected draft to be created, got ${created.status}`);
    const draftId = created.data.id;
    const saved = await firstValueFrom(
      repository.saveNamedAssistantDraft(draftId, { ...created.data.draft, name: '組織 A 的草稿' }, created.data.revision),
    );
    if (saved.status !== 'ready') throw new Error(`expected draft to save, got ${saved.status}`);

    expect(await firstValueFrom(repository.getNamedAssistantDraft(draftId))).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });

    // 組織 B 的 admin：同一個 Demo 身分 id（`account-smb-admin`），但真實身分不同，
    // 底層 storage 看不到組織 A 寫入的草稿清單，所以這個 id 就像不存在一樣。
    current = ORG_B_ADMIN;
    expect(await firstValueFrom(repository.getNamedAssistantDraft(draftId))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });

    // 同組織中另一位同角色帳號：真實 accountId 不同，即使 organizationId 相同。
    current = ORG_A_ADMIN_2;
    expect(await firstValueFrom(repository.getNamedAssistantDraft(draftId))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });

    // 切回組織 A 的原帳號：草稿仍然看得到。
    current = ORG_A_ADMIN;
    expect(await firstValueFrom(repository.getNamedAssistantDraft(draftId))).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });
  });
});
