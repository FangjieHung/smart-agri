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
});
