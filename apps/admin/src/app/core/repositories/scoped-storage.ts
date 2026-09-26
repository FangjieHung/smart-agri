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
 * 沒有身分時（尚未登入、token 已過期）一律不加前綴：這些呼叫理論上不應該發生
 * （所有會寫入的方法都先檢查權限），保底行為與改版前的單一 storage 相同。
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
      return storage.getItem(current === null ? key : scopedKey(current, key));
    },
    setItem(key: string, value: string): void {
      const current = identity();
      storage.setItem(current === null ? key : scopedKey(current, key), value);
    },
    removeItem(key: string): void {
      const current = identity();
      storage.removeItem(current === null ? key : scopedKey(current, key));
    },
  };
}
