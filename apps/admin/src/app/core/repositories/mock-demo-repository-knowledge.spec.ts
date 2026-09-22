import type { KnowledgeDocumentView } from '../domain/knowledge-base.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

function createRepository(storage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function detailOf(repository: MockDemoRepository, id: string) {
  const result = repository.getKnowledgeBaseDetail('account-smb-admin', id);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

describe('MockDemoRepository knowledge bases', () => {
  it('summarises the owner’s knowledge bases with item counts, status counts, sharing and connected assistants', () => {
    const result = createRepository().listKnowledgeBaseSummaries('account-smb-admin');

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

  it('covers all five document statuses across the seeded knowledge bases', () => {
    const repository = createRepository();
    const statuses = new Set(
      (['knowledge-product-guide', 'knowledge-refund-policy', 'knowledge-shipping-faq'] as const)
        .flatMap((id) => detailOf(repository, id).documents)
        .map((document) => document.status),
    );

    expect(statuses).toEqual(
      new Set(['queued', 'processing', 'ready', 'partially-readable', 'failed']),
    );
  });

  it('keeps other documents usable when one document fails, and explains the failure', () => {
    const detail = detailOf(createRepository(), 'knowledge-product-guide');
    const failed = detail.documents.filter((document) => document.status === 'failed');
    const ready = detail.documents.filter((document) => document.status === 'ready');

    expect(failed).toHaveLength(1);
    expect(failed[0].issue).toEqual(expect.any(String));
    expect(ready.length).toBeGreaterThan(0);
    ready.forEach((document) => expect(document.issue).toBeNull());
  });

  it('lists every assistant owned by the viewer that connects the knowledge base', () => {
    const detail = detailOf(createRepository(), 'knowledge-product-guide');

    expect(detail.connectedAssistants.map((assistant) => assistant.id)).toEqual([
      'assistant-customer-service',
      'assistant-internal-onboarding',
    ]);
  });

  it.each([
    ['another account’s knowledge base', 'knowledge-staff-notes'],
    ['an unknown id', 'knowledge-does-not-exist'],
  ])('denies %s without revealing its name', (_label, id) => {
    const result = createRepository().getKnowledgeBaseDetail('account-smb-admin', id);

    expect(result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
    expect(JSON.stringify(result)).not.toContain('同仁個人筆記');
  });

  it('adds a simulated document that steps through queued → processing → ready without touching others', () => {
    const repository = createRepository();
    const before = detailOf(repository, 'knowledge-refund-policy').documents;

    const added = repository.addDemoKnowledgeDocument('account-smb-admin', 'knowledge-refund-policy');
    expect(added).toMatchObject({ status: 'ready', data: { status: 'queued', kind: 'document' } });
    if (added.status !== 'ready') return;
    const id = added.data.id;

    const steps: KnowledgeDocumentView['status'][] = [];
    for (let i = 0; i < 3; i += 1) {
      const next = repository.advanceKnowledgeDocument('account-smb-admin', 'knowledge-refund-policy', id);
      if (next.status === 'ready') steps.push(next.data.status);
    }
    expect(steps).toEqual(['processing', 'ready', 'ready']);

    const after = detailOf(repository, 'knowledge-refund-policy').documents;
    expect(after).toHaveLength(before.length + 1);
    expect(after.slice(0, before.length)).toEqual(before);
  });

  it('persists simulated documents to the injected storage', () => {
    const storage = createMemoryStorage();
    const added = createRepository(storage).addDemoKnowledgeDocument(
      'account-smb-admin',
      'knowledge-shipping-faq',
    );
    if (added.status !== 'ready') throw new Error('expected ready');

    const reloaded = detailOf(createRepository(storage), 'knowledge-shipping-faq');
    expect(reloaded.documents.map((document) => document.id)).toContain(added.data.id);
  });

  it('retries a failed document by queueing it again and clearing the issue', () => {
    const repository = createRepository();
    const failed = detailOf(repository, 'knowledge-product-guide').documents.find(
      (document) => document.status === 'failed',
    );
    if (!failed) throw new Error('seed needs a failed document');

    const retried = repository.retryKnowledgeDocument(
      'account-smb-admin',
      'knowledge-product-guide',
      failed.id,
    );

    expect(retried).toMatchObject({ status: 'ready', data: { status: 'queued', issue: null } });
  });

  it('refuses document changes from a viewer who does not own the knowledge base', () => {
    const repository = createRepository();

    expect(
      repository.addDemoKnowledgeDocument('account-internal-employee', 'knowledge-product-guide'),
    ).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
  });

  it('saves each of the three sharing scopes explicitly', () => {
    const repository = createRepository();

    for (const sharing of [
      { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
      {
        scope: 'specific-accounts',
        sharedWithAccountIds: ['account-internal-employee'],
        allowOriginalDownload: false,
      },
      { scope: 'public', sharedWithAccountIds: [], allowOriginalDownload: true },
    ] as const) {
      expect(
        repository.updateKnowledgeSharing('account-smb-admin', 'knowledge-refund-policy', sharing),
      ).toEqual({ status: 'ready', data: sharing });
      expect(detailOf(repository, 'knowledge-refund-policy').sharing).toEqual(sharing);
    }
  });

  it('rejects account-specific sharing without any selected account', () => {
    const result = createRepository().updateKnowledgeSharing(
      'account-smb-admin',
      'knowledge-refund-policy',
      { scope: 'specific-accounts', sharedWithAccountIds: [], allowOriginalDownload: false },
    );

    expect(result).toMatchObject({ status: 'validation-failed' });
  });

  it('offers share targets other than the owner', () => {
    const detail = detailOf(createRepository(), 'knowledge-refund-policy');

    expect(detail.shareTargets.map((target) => target.id)).toEqual([
      'account-internal-employee',
      'account-external-customer',
    ]);
  });
});
