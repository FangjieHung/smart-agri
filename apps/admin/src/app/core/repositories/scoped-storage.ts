import type { DemoKeyValueStorage } from './demo-repository';

/**
 * API 模式下「目前身分」：組織與帳號都是 `/me` 回傳的真實 GUID，不是 mock 用的
 * 角色對應 Demo id（同角色在不同組織會對應到同一個 Demo id，見 `demo-identity-bridge.ts`）。
 */
export interface StorageIdentity {
  readonly organizationId: string;
  readonly accountId: string;
}

const SCOPE_PREFIX = 'api-scope';

function scopedKey(identity: StorageIdentity, key: string): string {
  return `${SCOPE_PREFIX}:${identity.organizationId}:${identity.accountId}:${key}`;
}

/**
 * 在每次存取時依「目前身分」加前綴的 storage 裝飾器。API 模式用它包住 `MockDemoRepository`
 * 原本綁定的 `localStorage`，讓不同組織、以及同組織中不同帳號（即使同角色）的模擬草稿、
 * 對話、知識庫等資料不會互相碰撞。
 *
 * `HybridDemoRepository` 由 DI 在登入前就已建立，所以身分不能在建構時讀一次就固定；
 * `identity` 在每次 `getItem`／`setItem`／`removeItem` 呼叫時才求值，才能反映登入、
 * 重新整理、或（將來）多分頁各自登入不同帳號的最新狀態。
 *
 * 沒有身分時（尚未登入、`/me` 還沒回來、token 已過期）一律拒絕：讀不到任何資料、
 * 寫入與刪除直接忽略。不帶前綴的鍵值是改版前 API 模式所有帳號共用的位置，
 * 裡面可能留有其他帳號的資料，所以不能拿來當保底。
 *
 * mock 建置（含 GitHub Pages）不使用這個裝飾器，`MockDemoRepository` 的鍵值維持不變。
 */
export function createScopedStorage(
  storage: DemoKeyValueStorage,
  identity: () => StorageIdentity | null,
): DemoKeyValueStorage {
  return {
    getItem(key: string): string | null {
      const current = identity();
      return current === null ? null : storage.getItem(scopedKey(current, key));
    },
    setItem(key: string, value: string): void {
      const current = identity();
      if (current !== null) storage.setItem(scopedKey(current, key), value);
    },
    removeItem(key: string): void {
      const current = identity();
      if (current !== null) storage.removeItem(scopedKey(current, key));
    },
  };
}
