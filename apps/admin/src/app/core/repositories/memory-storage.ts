import type { DemoKeyValueStorage } from './demo-repository';

/** 不落地的 key-value 儲存，供測試或無 localStorage 的環境使用。 */
export function createMemoryStorage(): DemoKeyValueStorage {
  const values = new Map<string, string>();

  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => {
      values.set(key, value);
    },
    removeItem: (key) => {
      values.delete(key);
    },
  };
}
