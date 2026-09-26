import { firstValueFrom } from 'rxjs';
import { createEmptyAssistantDraft, type AssistantDraft } from '../domain/assistant-draft.model';
import type { DemoKeyValueStorage } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN = 'account-smb-admin';
const EMPLOYEE = 'account-internal-employee';
const CUSTOMER = 'account-external-customer';
const CUSTOMER_SERVICE = 'assistant-customer-service';

function completeDraft(): AssistantDraft {
  return {
    ...createEmptyAssistantDraft(),
    templateId: 'answer-customer-questions',
    name: '客戶問答助理',
    purpose: '回答客戶關於商品、退換貨與配送的問題',
    audience: 'members-and-external-customers',
    sources: [
      { id: 'knowledge-product-guide', type: 'knowledge-base' },
      { id: 'database-orders', type: 'database' },
    ],
    testedQuestionIds: ['trial-refund-window'],
    currentStep: 'test',
  };
}

/** 記錄每一次 `setItem`／`removeItem` 用過的 key，底層仍是真正的記憶體 storage。 */
function recordingStorage(): { storage: DemoKeyValueStorage; keys: Set<string> } {
  const raw = createMemoryStorage();
  const keys = new Set<string>();
  return {
    keys,
    storage: {
      getItem: (key) => raw.getItem(key),
      setItem: (key, value) => {
        keys.add(key);
        raw.setItem(key, value);
      },
      removeItem: (key) => {
        keys.add(key);
        raw.removeItem(key);
      },
    },
  };
}

/**
 * 驗收條件（mock 模式）：既有 Demo 資料不需遷移，`localStorage` 鍵名與改版前相同。
 * 這裡直接鎖定 `MockDemoRepository`（未被任何 API 模式的 storage 裝飾器包住）
 * 走過每一種會寫入 storage 的功能區後，實際用過的鍵名清單；改了任何一個鍵名的字串
 * 拼法都會讓這個測試炸掉，而不必等到跑 Cypress 或肉眼比對 localStorage 面板。
 */
describe('MockDemoRepository mock-mode storage keys (locked)', () => {
  it('writes exactly the same key names as before this change', async () => {
    const { storage, keys } = recordingStorage();
    const repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-26T00:00:00.000Z'),
      viewer: () => ADMIN,
    });

    // 草稿：先存成單一草稿（legacy key），再建立命名草稿觸發遷移。
    repository.saveAssistantDraft(ADMIN, { ...createEmptyAssistantDraft(), name: '草稿' });
    repository.createNamedAssistantDraft(ADMIN);

    // 建立助理：寫入 created-assistants、assistant-settings，並清掉草稿（removeItem）。
    const created = repository.createAssistantFromDraft(ADMIN, completeDraft());
    if (created.status !== 'ready') throw new Error(`expected assistant to be created, got ${created.status}`);

    // 團隊權限（mock 模式仍走 storage；API 模式由 HybridDemoRepository 整個覆寫掉）。
    const teamResult = await firstValueFrom(
      repository.updateMemberPermissions(EMPLOYEE, ['use-shared-assistants']),
    );
    if (teamResult.status !== 'ready') throw new Error(`expected team update to succeed, got ${teamResult.status}`);

    // 知識庫分享設定。
    const sharing = repository.updateKnowledgeSharing(ADMIN, 'knowledge-product-guide', {
      scope: 'private',
      sharedWithAccountIds: [],
      allowOriginalDownload: false,
    });
    if (sharing.status !== 'ready') throw new Error(`expected knowledge sharing to save, got ${sharing.status}`);

    // 資料庫：建立、改欄位、改資料管理者。
    const database = repository.createDatabaseFromTemplate(ADMIN, {
      templateId: 'template-blank',
      name: '測試資料庫',
    });
    if (database.status !== 'ready') throw new Error(`expected database to be created, got ${database.status}`);
    const detail = repository.getDatabaseDetail(ADMIN, database.data.id);
    if (detail.status !== 'ready') throw new Error(`expected database detail, got ${detail.status}`);
    repository.updateDatabaseFields(ADMIN, database.data.id, detail.data.fields);
    repository.updateDatabaseAccess(ADMIN, database.data.id, [ADMIN]);

    // 對話：帳號對話（寫進 storage）與表單提交（寫收集紀錄）。
    repository.sendChatMessage(ADMIN, CUSTOMER_SERVICE, '你好');
    repository.submitChatForm(CUSTOMER, CUSTOMER_SERVICE, {
      formId: 'database-orders',
      answers: {
        'field-order-number': 'DEMO-9001',
        'field-issue-type': '配送延遲',
        'field-reported-on': '2026-09-21',
      },
      consent: true,
    });

    // 官網嵌入設定（publishing）。
    const publishing = repository.getAssistantPublishing(ADMIN, CUSTOMER_SERVICE);
    if (publishing.status !== 'ready') throw new Error(`expected publishing view, got ${publishing.status}`);
    repository.updateWebsiteEmbed(ADMIN, CUSTOMER_SERVICE, publishing.data.website);

    expect(Array.from(keys).sort()).toEqual(
      [
        `sme-demo:assistant-draft:${ADMIN}`,
        `sme-demo:assistant-drafts:${ADMIN}`,
        'sme-demo:created-assistants',
        `sme-demo:assistant-settings:${created.data.id}`,
        'sme-demo:team-permissions',
        'sme-demo:knowledge:knowledge-product-guide',
        'sme-demo:created-databases',
        `sme-demo:database-fields:${database.data.id}`,
        `sme-demo:database-access:${database.data.id}`,
        `sme-demo:chat:${ADMIN}:${CUSTOMER_SERVICE}`,
        `sme-demo:chat:${CUSTOMER}:${CUSTOMER_SERVICE}`,
        'sme-demo:chat-records',
        `sme-demo:publishing:${CUSTOMER_SERVICE}`,
      ].sort(),
    );
  });
});
