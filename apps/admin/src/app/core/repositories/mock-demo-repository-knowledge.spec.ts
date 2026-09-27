import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { KnowledgeBaseDetailView } from '../domain/knowledge-base.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import {
  MOCK_KNOWLEDGE_PROCESSING_MS,
  MOCK_KNOWLEDGE_QUEUED_MS,
  MockDemoRepository,
} from './mock-demo-repository';

const ADMIN = 'account-smb-admin';
const EMPLOYEE = 'account-internal-employee';
const CUSTOMER = 'account-external-customer';
const START = new Date('2026-09-22T02:00:00.000Z').getTime();

interface Options {
  readonly storage?: ReturnType<typeof createMemoryStorage>;
  readonly viewer?: AccountId | null;
  /** 測試可推進的時鐘（毫秒）。 */
  readonly clock?: { now: number };
}

function createRepository({ storage = createMemoryStorage(), viewer = ADMIN, clock = { now: START } }: Options = {}) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date(clock.now),
    viewer: () => viewer,
  });
}

async function detailOf(repository: MockDemoRepository, id: string): Promise<KnowledgeBaseDetailView> {
  const result = await firstValueFrom(repository.getKnowledgeBaseDetail(id));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

async function summaryIds(repository: MockDemoRepository): Promise<string[]> {
  const result = await firstValueFrom(repository.listKnowledgeBaseSummaries());
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.map((item) => item.id);
}

describe('MockDemoRepository knowledge bases', () => {
  it('is a cold Observable: nothing is read or written until subscribed', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository({ storage });

    const create = repository.createKnowledgeBase({ name: '未訂閱', purpose: '' });
    expect((await summaryIds(repository)).some((id) => id.startsWith('knowledge-created-'))).toBe(false);

    await firstValueFrom(create);
    expect((await summaryIds(repository)).some((id) => id.startsWith('knowledge-created-'))).toBe(true);
  });

  it('summarises the owner’s knowledge bases with item counts, status counts, sharing and connected assistants', async () => {
    const result = await firstValueFrom(createRepository().listKnowledgeBaseSummaries());

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.map((item) => item.id)).toEqual([
      'knowledge-product-guide',
      'knowledge-refund-policy',
      'knowledge-shipping-faq',
    ]);
    const guide = result.data[0];
    expect(guide).toMatchObject({
      name: '商品使用指南',
      documentCount: 4,
      faqCount: 2,
      sharingScope: 'specific-accounts',
      connectedAssistantNames: ['客服助理', '內部教育訓練助理'],
      viewerCanManage: true,
    });
    expect(guide.statusCounts).toEqual({
      queued: 0,
      processing: 0,
      ready: 4,
      'partially-readable': 1,
      failed: 1,
    });
    expect(Object.isFrozen(result.data)).toBe(true);
  });

  it('reads nothing for a signed-out session', async () => {
    const repository = createRepository({ viewer: null });

    expect(await firstValueFrom(repository.listKnowledgeBaseSummaries())).toMatchObject({
      status: 'permission-denied',
      reason: 'knowledge-base',
    });
    expect(await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-product-guide'))).toMatchObject({
      status: 'permission-denied',
      reason: 'knowledge-base',
    });
  });

  it('covers all five document statuses across the seeded knowledge bases', async () => {
    const repository = createRepository();
    const details = await Promise.all(
      ['knowledge-product-guide', 'knowledge-refund-policy', 'knowledge-shipping-faq'].map((id) =>
        detailOf(repository, id),
      ),
    );
    const statuses = new Set(details.flatMap((detail) => detail.documents).map((document) => document.status));

    expect(statuses).toEqual(new Set(['queued', 'processing', 'ready', 'partially-readable', 'failed']));
  });

  it('gives every document a version id to retry against', async () => {
    const detail = await detailOf(createRepository(), 'knowledge-product-guide');

    detail.documents.forEach((document) => expect(document.latestVersionId).toBe(`${document.id}:v1`));
  });

  it('keeps other documents usable when one document fails, and explains the failure', async () => {
    const detail = await detailOf(createRepository(), 'knowledge-product-guide');
    const failed = detail.documents.filter((document) => document.status === 'failed');
    const ready = detail.documents.filter((document) => document.status === 'ready');

    expect(failed).toHaveLength(1);
    expect(failed[0].issue).toEqual(expect.any(String));
    expect(ready.length).toBeGreaterThan(0);
    ready.forEach((document) => expect(document.issue).toBeNull());
  });

  it('lists every assistant owned by the viewer that connects the knowledge base', async () => {
    const detail = await detailOf(createRepository(), 'knowledge-product-guide');

    expect(detail.connectedAssistants.map((assistant) => assistant.id)).toEqual([
      'assistant-customer-service',
      'assistant-internal-onboarding',
    ]);
  });

  it.each([
    ['another account’s knowledge base', 'knowledge-staff-notes'],
    ['an unknown id', 'knowledge-does-not-exist'],
  ])('denies %s without revealing its name', async (_label, id) => {
    const result = await firstValueFrom(createRepository().getKnowledgeBaseDetail(id));

    expect(result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    expect(JSON.stringify(result)).not.toContain('同仁個人筆記');
  });

  describe('processing by elapsed time (no timers, no advance method)', () => {
    it('retries a failed document: queued, then processing, then ready as time passes', async () => {
      const clock = { now: START };
      const repository = createRepository({ clock });
      const failed = (await detailOf(repository, 'knowledge-product-guide')).documents.find(
        (document) => document.status === 'failed',
      );
      if (!failed) throw new Error('seed needs a failed document');

      const retried = await firstValueFrom(
        repository.retryKnowledgeDocument('knowledge-product-guide', failed.id, failed.latestVersionId),
      );
      expect(retried).toMatchObject({ status: 'ready', data: { status: 'queued', issue: null } });

      const statusAfter = async (ms: number) => {
        clock.now = START + ms;
        const detail = await detailOf(repository, 'knowledge-product-guide');
        return detail.documents.find((document) => document.id === failed.id)?.status;
      };
      expect(await statusAfter(MOCK_KNOWLEDGE_QUEUED_MS - 1)).toBe('queued');
      expect(await statusAfter(MOCK_KNOWLEDGE_QUEUED_MS)).toBe('processing');
      expect(await statusAfter(MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS - 1)).toBe('processing');
      expect(await statusAfter(MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS)).toBe('ready');

      const summary = (await detailOf(repository, 'knowledge-product-guide')).summary;
      expect(summary.statusCounts).toMatchObject({ failed: 0, ready: 5 });
      expect(summary.updatedAt).toBe(
        new Date(START + MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS).toISOString(),
      );
    });

    it('keeps the progress after a reload, because only the start time is stored', async () => {
      const storage = createMemoryStorage();
      const clock = { now: START };
      const repository = createRepository({ storage, clock });
      const failed = (await detailOf(repository, 'knowledge-product-guide')).documents.find(
        (document) => document.status === 'failed',
      );
      if (!failed) throw new Error('seed needs a failed document');
      await firstValueFrom(repository.retryKnowledgeDocument('knowledge-product-guide', failed.id, failed.latestVersionId));

      clock.now = START + MOCK_KNOWLEDGE_QUEUED_MS + 1;
      const reloaded = await detailOf(createRepository({ storage, clock }), 'knowledge-product-guide');
      expect(reloaded.documents.find((document) => document.id === failed.id)?.status).toBe('processing');
    });

    it('leaves the seeded in-progress examples as they are', async () => {
      const clock = { now: START + 24 * 60 * 60 * 1000 };
      const detail = await detailOf(createRepository({ clock }), 'knowledge-shipping-faq');

      expect(detail.documents.map((document) => document.status)).toEqual(['ready', 'processing', 'queued']);
    });
  });

  it.each([
    ['partially-readable', 'document-guide-scan', '只有處理失敗的版本可以重試。'],
    ['ready', 'document-guide-specs', '只有處理失敗的版本可以重試。'],
  ])('refuses to retry a %s document, like the API’s 409', async (_status, documentId, message) => {
    const repository = createRepository();
    const result = await firstValueFrom(
      repository.retryKnowledgeDocument('knowledge-product-guide', documentId, `${documentId}:v1`),
    );

    expect(result).toEqual({ status: 'validation-failed', message });
  });

  it('refuses to retry a document that is still processing', async () => {
    const result = await firstValueFrom(
      createRepository().retryKnowledgeDocument(
        'knowledge-shipping-faq',
        'document-shipping-islands',
        'document-shipping-islands:v1',
      ),
    );

    expect(result).toEqual({
      status: 'validation-failed',
      message: '這個版本還在等待或處理中，處理完成後才能重試。',
    });
  });

  it('refuses document changes from a viewer who does not own the knowledge base', async () => {
    const repository = createRepository({ viewer: EMPLOYEE });

    expect(
      await firstValueFrom(
        repository.retryKnowledgeDocument('knowledge-product-guide', 'document-guide-locked', 'document-guide-locked:v1'),
      ),
    ).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    expect(
      await firstValueFrom(repository.deleteKnowledgeDocument('knowledge-product-guide', 'document-guide-locked')),
    ).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    expect(await firstValueFrom(repository.deleteKnowledgeBase('knowledge-product-guide'))).toMatchObject({
      status: 'permission-denied',
      reason: 'knowledge-base',
    });
  });

  describe('create', () => {
    it('creates a private, empty knowledge base that appears in the owner’s list only', async () => {
      const storage = createMemoryStorage();
      const repository = createRepository({ storage });

      const created = await firstValueFrom(
        repository.createKnowledgeBase({ name: '  門市作業手冊  ', purpose: ' 開店與結帳流程 ' }),
      );

      expect(created).toMatchObject({
        status: 'ready',
        data: {
          name: '門市作業手冊',
          purpose: '開店與結帳流程',
          documentCount: 0,
          faqCount: 0,
          sharingScope: 'private',
          connectedAssistantNames: [],
          viewerCanManage: true,
        },
      });
      if (created.status !== 'ready') return;
      expect(await summaryIds(repository)).toContain(created.data.id);
      expect((await detailOf(repository, created.data.id)).sharing).toEqual({
        scope: 'private',
        sharedWithAccountIds: [],
        allowOriginalDownload: false,
      });
      expect(await summaryIds(createRepository({ storage, viewer: EMPLOYEE }))).not.toContain(created.data.id);
    });

    it('requires manage-data-sources, with the API’s message', async () => {
      const result = await firstValueFrom(
        createRepository({ viewer: CUSTOMER }).createKnowledgeBase({ name: '客戶的知識庫', purpose: '' }),
      );

      expect(result).toEqual({
        status: 'permission-denied',
        reason: 'knowledge-base',
        message: '只有可管理資料來源的帳號可以建立知識庫。',
      });
    });

    it.each([
      [{ name: '   ', purpose: '' }, '請輸入知識庫名稱。'],
      [{ name: '名'.repeat(101), purpose: '' }, '知識庫名稱最多 100 個字。'],
      [{ name: '合法名稱', purpose: '字'.repeat(501) }, '用途說明最多 500 個字。'],
    ])('rejects invalid input without writing anything', async (input, message) => {
      const repository = createRepository();
      const before = await summaryIds(repository);

      expect(await firstValueFrom(repository.createKnowledgeBase(input))).toEqual({
        status: 'validation-failed',
        message,
      });
      expect(await summaryIds(repository)).toEqual(before);
    });
  });

  describe('delete', () => {
    it('deletes a seeded knowledge base for good, and the id then reads like any unknown id', async () => {
      const storage = createMemoryStorage();
      const repository = createRepository({ storage });

      expect(await firstValueFrom(repository.deleteKnowledgeBase('knowledge-refund-policy'))).toEqual({
        status: 'ready',
        data: null,
      });
      expect(await summaryIds(createRepository({ storage }))).not.toContain('knowledge-refund-policy');
      expect(await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-refund-policy'))).toMatchObject({
        status: 'permission-denied',
        reason: 'knowledge-base',
      });
      expect(await firstValueFrom(repository.deleteKnowledgeBase('knowledge-refund-policy'))).toMatchObject({
        status: 'permission-denied',
      });
    });

    it('deletes a created knowledge base', async () => {
      const repository = createRepository();
      const created = await firstValueFrom(repository.createKnowledgeBase({ name: '暫時的', purpose: '' }));
      if (created.status !== 'ready') throw new Error('expected ready');

      await firstValueFrom(repository.deleteKnowledgeBase(created.data.id));

      expect(await summaryIds(repository)).not.toContain(created.data.id);
    });

    it('deletes one document and leaves the others untouched', async () => {
      const repository = createRepository();
      const before = (await detailOf(repository, 'knowledge-product-guide')).documents;

      expect(
        await firstValueFrom(repository.deleteKnowledgeDocument('knowledge-product-guide', 'document-guide-scan')),
      ).toEqual({ status: 'ready', data: null });

      const after = (await detailOf(repository, 'knowledge-product-guide')).documents;
      expect(after).toEqual(before.filter((document) => document.id !== 'document-guide-scan'));
      expect(
        await firstValueFrom(repository.deleteKnowledgeDocument('knowledge-product-guide', 'document-guide-scan')),
      ).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    });
  });

  describe('sharing', () => {
    it('saves each of the three sharing scopes explicitly', async () => {
      const repository = createRepository();

      for (const sharing of [
        { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
        { scope: 'specific-accounts', sharedWithAccountIds: [EMPLOYEE], allowOriginalDownload: false },
        { scope: 'public', sharedWithAccountIds: [], allowOriginalDownload: true },
      ] as const) {
        expect(await firstValueFrom(repository.updateKnowledgeSharing('knowledge-refund-policy', sharing))).toEqual({
          status: 'ready',
          data: sharing,
        });
        expect((await detailOf(repository, 'knowledge-refund-policy')).sharing).toEqual(sharing);
      }
    });

    it('rejects account-specific sharing without any selected account', async () => {
      const result = await firstValueFrom(
        createRepository().updateKnowledgeSharing('knowledge-refund-policy', {
          scope: 'specific-accounts',
          sharedWithAccountIds: [],
          allowOriginalDownload: false,
        }),
      );

      expect(result).toEqual({ status: 'validation-failed', message: '請至少選擇一個帳號或團隊。' });
    });

    it('offers share targets other than the owner', async () => {
      const detail = await detailOf(createRepository(), 'knowledge-refund-policy');

      expect(detail.shareTargets.map((target) => target.id)).toEqual([EMPLOYEE, CUSTOMER]);
    });
  });

  describe('uploads (issue #46)', () => {
    function file(name: string, size: number, type = 'application/pdf'): File {
      return new File([new Uint8Array(size)], name, { type });
    }

    it('is a cold Observable: nothing is written until subscribed', async () => {
      const repository = createRepository();
      const upload = repository.uploadKnowledgeDocument('knowledge-product-guide', file('未訂閱.pdf', 10));

      expect((await detailOf(repository, 'knowledge-product-guide')).documents.some((d) => d.name === '未訂閱.pdf')).toBe(
        false,
      );

      await firstValueFrom(upload);
      expect((await detailOf(repository, 'knowledge-product-guide')).documents.some((d) => d.name === '未訂閱.pdf')).toBe(
        true,
      );
    });

    it('accepts a new file, queues it, and lets it become ready after the mock’s processing time', async () => {
      const clock = { now: START };
      const repository = createRepository({ clock });

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('新品錄影腳本.pdf', 1024)),
      );
      expect(result).toMatchObject({ status: 'ready', data: { name: '新品錄影腳本.pdf', status: 'queued' } });

      clock.now = START + MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS + 1;
      const detail = await detailOf(repository, 'knowledge-product-guide');
      expect(detail.documents.find((d) => d.name === '新品錄影腳本.pdf')).toMatchObject({ status: 'ready' });
    });

    it('rejects an unsupported extension without writing anything', async () => {
      const repository = createRepository();

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('病毒.exe', 10, 'application/octet-stream')),
      );

      expect(result).toMatchObject({ status: 'rejected', reason: 'unsupported-file-type' });
      expect((await detailOf(repository, 'knowledge-product-guide')).documents.some((d) => d.name === '病毒.exe')).toBe(
        false,
      );
    });

    it('rejects a file larger than the limit', async () => {
      const repository = createRepository();

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('太大.pdf', 21 * 1024 * 1024)),
      );

      expect(result).toMatchObject({ status: 'rejected', reason: 'file-too-large' });
    });

    it('rejects a duplicate name, pointing at uploading a new version instead', async () => {
      const repository = createRepository();

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('商品規格總表.pdf', 10)),
      );

      expect(result).toEqual({
        status: 'rejected',
        reason: 'duplicate-name',
        message: '這個知識庫已經有名為「商品規格總表.pdf」的文件。要更新它的內容，請改用「上傳新版本」。',
      });
    });

    it('rejects content identical (by size) to a file already uploaded in this browser, naming the existing document', async () => {
      const repository = createRepository();
      await firstValueFrom(repository.uploadKnowledgeDocument('knowledge-product-guide', file('第一次.pdf', 4321)));

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('第二次.pdf', 4321)),
      );

      expect(result).toEqual({
        status: 'rejected',
        reason: 'duplicate-content',
        message: '這份檔案的內容與「第一次.pdf」完全相同，不需要重複上傳。',
        existingDocumentName: '第一次.pdf',
      });
    });

    it('returns the same permission-denied as other knowledge methods for someone else’s knowledge base', async () => {
      const repository = createRepository({ viewer: EMPLOYEE });

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocument('knowledge-product-guide', file('x.pdf', 10)),
      );

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    });

    it('uploads a new version of an existing document without touching its name', async () => {
      const repository = createRepository();

      const result = await firstValueFrom(
        repository.uploadKnowledgeDocumentVersion(
          'knowledge-product-guide',
          'document-guide-specs',
          file('商品規格總表-v2.pdf', 10),
        ),
      );

      expect(result).toMatchObject({ status: 'ready', data: { id: 'document-guide-specs', name: '商品規格總表.pdf', status: 'queued' } });
    });
  });
});

/**
 * 版本確認、抽取預覽與緊急停用（issue #47，M2 Slice 13）。與上面同一個 mock，
 * 但這批測試專門涵蓋生效日期、封存與停用時的行為。
 */
describe('MockDemoRepository knowledge review (issue #47)', () => {
  const KB_ID = 'knowledge-refund-policy';
  const DOC_ID = 'document-refund-policy';

  function file(name: string, size: number, type = 'application/pdf'): File {
    return new File([new Uint8Array(size)], name, { type });
  }

  it('treats a seeded document as already effective (version 1), so existing demo data keeps working', async () => {
    const document = (await detailOf(createRepository(), KB_ID)).documents.find((item) => item.id === DOC_ID);

    expect(document).toMatchObject({
      latestVersionNumber: 1,
      latestVersionState: 'effective',
      effectiveVersionNumber: 1,
      disabled: false,
      inEffect: true,
    });
  });

  it('requires confirmation for a brand-new document before it counts as in effect', async () => {
    const repository = createRepository();
    const uploaded = await firstValueFrom(repository.uploadKnowledgeDocument(KB_ID, file('新退貨辦法.pdf', 10)));
    if (uploaded.status !== 'ready') throw new Error(`expected ready, got ${uploaded.status}`);

    expect(uploaded.data).toMatchObject({
      latestVersionState: 'pending-review',
      effectiveVersionNumber: null,
      inEffect: false,
    });
  });

  it('keeps the previous version in effect while a newly uploaded version is still pending review', async () => {
    const repository = createRepository();
    const result = await firstValueFrom(
      repository.uploadKnowledgeDocumentVersion(KB_ID, DOC_ID, file('退換貨辦法 2027 版.pdf', 20)),
    );
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);

    expect(result.data).toMatchObject({
      latestVersionNumber: 2,
      latestVersionState: 'pending-review',
      // v1 仍然生效中，直到 v2 被確認生效為止。
      effectiveVersionNumber: 1,
      inEffect: true,
    });
  });

  it('confirms a version effective (optionally in the future), and archives the version it replaces', async () => {
    const clock = { now: START };
    const repository = createRepository({ clock });
    await firstValueFrom(repository.uploadKnowledgeDocumentVersion(KB_ID, DOC_ID, file('退換貨辦法 2027 版.pdf', 20)));
    const document = (await detailOf(repository, KB_ID)).documents.find((item) => item.id === DOC_ID);
    if (!document) throw new Error('document not found');

    const approved = await firstValueFrom(
      repository.approveKnowledgeVersions(KB_ID, [document.latestVersionId]),
    );
    if (approved.status !== 'ready') throw new Error(`expected ready, got ${approved.status}`);
    expect(approved.data).toHaveLength(1);
    expect(approved.data[0]).toMatchObject({ versionNumber: 2, state: 'effective' });

    const detail = await detailOf(repository, KB_ID);
    const refreshed = detail.documents.find((item) => item.id === DOC_ID);
    expect(refreshed).toMatchObject({ latestVersionNumber: 2, effectiveVersionNumber: 2, inEffect: true });

    // getKnowledgeBaseDetail 不回傳版本歷程；改用 getKnowledgeDocumentDetail 驗證封存狀態。
    const documentDetail = await firstValueFrom(repository.getKnowledgeDocumentDetail(KB_ID, DOC_ID));
    if (documentDetail.status !== 'ready') throw new Error(`expected ready, got ${documentDetail.status}`);
    expect(documentDetail.data.versions.map((v) => ({ versionNumber: v.versionNumber, state: v.state }))).toEqual([
      { versionNumber: 2, state: 'effective' },
      { versionNumber: 1, state: 'archived' },
    ]);
    expect(documentDetail.data.activities.map((a) => a.action)).toContain('version-approved');
  });

  it('rejects the whole batch when any one version is not approvable, and writes nothing', async () => {
    const repository = createRepository();
    const document = (await detailOf(repository, KB_ID)).documents.find((item) => item.id === DOC_ID);
    if (!document) throw new Error('document not found');
    // document-refund-policy（seed）已經是 approved，不是待確認，所以整批應該被拒絕。

    const result = await firstValueFrom(
      repository.approveKnowledgeVersions(KB_ID, [document.latestVersionId]),
    );

    expect(result).toMatchObject({ status: 'validation-failed' });
  });

  it('requires a reason to disable a document', async () => {
    const repository = createRepository();

    const result = await firstValueFrom(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '   '));

    expect(result).toMatchObject({ status: 'validation-failed', message: '請說明緊急停用的原因。' });
  });

  it('disables a document immediately regardless of its approval state, keeping the reason on the activity log', async () => {
    const repository = createRepository();

    const disabled = await firstValueFrom(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '疑似內容錯誤，暫停使用'));
    if (disabled.status !== 'ready') throw new Error(`expected ready, got ${disabled.status}`);
    expect(disabled.data).toMatchObject({ disabled: true, inEffect: false });

    const detail = await firstValueFrom(repository.getKnowledgeDocumentDetail(KB_ID, DOC_ID));
    if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
    expect(detail.data.disabledReason).toBe('疑似內容錯誤，暫停使用');
    expect(detail.data.activities[0]).toMatchObject({ action: 'document-disabled', reason: '疑似內容錯誤，暫停使用' });

    // 已停用文件的清單，即使有生效版本也不再視為生效。
    const documentInList = (await detailOf(repository, KB_ID)).documents.find((item) => item.id === DOC_ID);
    expect(documentInList).toMatchObject({ disabled: true, inEffect: false });
  });

  it('refuses to disable an already-disabled document, and to enable one that is not disabled', async () => {
    const repository = createRepository();
    await firstValueFrom(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '原因'));

    const secondDisable = await firstValueFrom(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '另一個原因'));
    expect(secondDisable).toMatchObject({ status: 'validation-failed' });

    const otherDocument = (await detailOf(createRepository(), KB_ID)).documents.find(
      (item) => item.id !== DOC_ID,
    );
    if (!otherDocument) throw new Error('need a second document in the seed');
    const enableNotDisabled = await firstValueFrom(
      repository.enableKnowledgeDocument(KB_ID, otherDocument.id),
    );
    expect(enableNotDisabled).toMatchObject({ status: 'validation-failed' });
  });

  it('restores the prior state on enable', async () => {
    const repository = createRepository();
    await firstValueFrom(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '原因'));

    const enabled = await firstValueFrom(repository.enableKnowledgeDocument(KB_ID, DOC_ID));
    if (enabled.status !== 'ready') throw new Error(`expected ready, got ${enabled.status}`);
    expect(enabled.data).toMatchObject({ disabled: false, inEffect: true, effectiveVersionNumber: 1 });
  });

  it('previews extracted units and chunks for a version, and lets a manager toggle exclusion', async () => {
    const repository = createRepository();
    const document = (await detailOf(repository, KB_ID)).documents.find((item) => item.id === DOC_ID);
    if (!document) throw new Error('document not found');

    const preview = await firstValueFrom(
      repository.previewKnowledgeVersion(KB_ID, DOC_ID, document.latestVersionId),
    );
    if (preview.status !== 'ready') throw new Error(`expected ready, got ${preview.status}`);
    expect(preview.data.units.length).toBeGreaterThan(0);
    const chunk = preview.data.units[0].chunks[0];
    expect(chunk.excluded).toBe(false);

    const excluded = await firstValueFrom(
      repository.updateKnowledgeChunkExclusion(KB_ID, DOC_ID, document.latestVersionId, chunk.id, true),
    );
    if (excluded.status !== 'ready') throw new Error(`expected ready, got ${excluded.status}`);
    expect(excluded.data.excluded).toBe(true);

    const rePreview = await firstValueFrom(
      repository.previewKnowledgeVersion(KB_ID, DOC_ID, document.latestVersionId),
    );
    if (rePreview.status !== 'ready') throw new Error(`expected ready, got ${rePreview.status}`);
    expect(rePreview.data.units[0].chunks[0].excluded).toBe(true);
  });
});
