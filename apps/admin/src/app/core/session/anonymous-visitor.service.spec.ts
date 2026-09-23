import { createMemoryStorage } from '../repositories/memory-storage';
import {
  ANONYMOUS_VISITOR_STORAGE_KEY,
  AnonymousVisitorService,
} from './anonymous-visitor.service';

/** 每個測試都用自己的記憶體儲存，模擬一個獨立的瀏覽器分頁。 */
function createTab(storage = createMemoryStorage()) {
  return { storage, visitor: new AnonymousVisitorService({ storage }) };
}

describe('AnonymousVisitorService', () => {
  it('has no visitor until the embedded chat asks for one', () => {
    const { visitor, storage } = createTab();

    expect(visitor.visitorId()).toBeNull();
    expect(storage.getItem(ANONYMOUS_VISITOR_STORAGE_KEY)).toBeNull();
  });

  it('mints a visitor id that is not an account id and keeps it across reloads', () => {
    const { visitor, storage } = createTab();

    const minted = visitor.ensureVisitor();

    expect(minted.startsWith('visitor-')).toBe(true);
    expect(minted.length).toBeGreaterThan('visitor-'.length);
    expect(visitor.ensureVisitor()).toBe(minted);
    // 重新整理＝同一個分頁重新建立 service，訪客身分要還在。
    expect(new AnonymousVisitorService({ storage }).visitorId()).toBe(minted);
  });

  it('gives two browser tabs different visitors', () => {
    const first = createTab().visitor.ensureVisitor();
    const second = createTab().visitor.ensureVisitor();

    expect(first).not.toBe(second);
  });

  it('ignores a stored value that is not a visitor id and mints a fresh one', () => {
    const storage = createMemoryStorage();
    storage.setItem(ANONYMOUS_VISITOR_STORAGE_KEY, 'account-smb-admin');

    const visitor = new AnonymousVisitorService({ storage });

    expect(visitor.visitorId()).toBeNull();
    expect(visitor.ensureVisitor().startsWith('visitor-')).toBe(true);
  });

  it('ends the visitor session and forgets the id', () => {
    const { visitor, storage } = createTab();
    visitor.ensureVisitor();

    visitor.endVisit();

    expect(visitor.visitorId()).toBeNull();
    expect(storage.getItem(ANONYMOUS_VISITOR_STORAGE_KEY)).toBeNull();
  });
});
