import {
  createEmptyAssistantDraft,
  type AssistantDraft,
} from '../domain/assistant-draft.model';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { DEMO_SEED } from './demo-seed';

function completeDraft(): AssistantDraft {
  return {
    ...createEmptyAssistantDraft(),
    templateId: 'answer-customer-questions',
    name: '客戶問答助理',
    purpose: '回答客戶關於商品、退換貨與配送的問題',
    audience: 'members-and-external-customers',
    sources: [
      { id: 'knowledge-product-guide', type: 'knowledge-base' },
      { id: 'knowledge-refund-policy', type: 'knowledge-base' },
      { id: 'database-orders', type: 'database' },
      { id: 'database-customer-records', type: 'database' },
    ],
    testedQuestionIds: ['trial-refund-window'],
    currentStep: 'test',
  };
}

describe('MockDemoRepository assistant creation', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-22T02:00:00.000Z'),
    });
  });

  it('offers the purpose templates starting with answering customer questions', () => {
    const result = repository.listAssistantTemplates();

    expect(result.status).toBe('ready');
    if (result.status === 'ready') {
      expect(result.data[0]).toMatchObject({
        id: 'answer-customer-questions',
        title: '回答客戶問題',
      });
      expect(result.data.map((template) => template.id)).toContain('blank');
    }
  });

  it('lists knowledge bases and databases in one mixed list with type, permission, status and update time', () => {
    const result = repository.listConnectableSources('account-smb-admin');

    expect(result.status).toBe('ready');
    if (result.status === 'ready') {
      expect(new Set(result.data.map((source) => source.type))).toEqual(
        new Set(['knowledge-base', 'database']),
      );
      expect(result.data).toContainEqual(
        expect.objectContaining({
          id: 'database-orders',
          type: 'database',
          permission: 'read-only',
          status: 'ready',
          updatedAt: '2026-09-18T07:30:00.000Z',
        }),
      );
      expect(result.data).toContainEqual(
        expect.objectContaining({
          id: 'knowledge-product-guide',
          type: 'knowledge-base',
          permission: 'owner',
          summary: '4 份文件、2 則 FAQ',
          status: 'needs-attention',
        }),
      );
    }
    expect(repository.listConnectableSources('account-internal-employee')).toEqual({
      status: 'ready',
      data: [],
    });
  });

  it('persists a draft per account so another account never sees it', () => {
    const draft = { ...createEmptyAssistantDraft(), name: '草稿助理' };

    const saved = repository.saveAssistantDraft('account-smb-admin', draft);
    const reloaded = new MockDemoRepository(DEMO_SEED, { storage });

    expect(saved).toEqual({
      status: 'ready',
      data: { draft, savedAt: '2026-09-22T02:00:00.000Z' },
    });
    expect(reloaded.getAssistantDraft('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: { draft: { name: '草稿助理' } },
    });
    expect(reloaded.getAssistantDraft('account-external-customer')).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });
  });

  it('returns no draft instead of failing when stored data is missing or corrupted', () => {
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });

    storage.setItem('sme-demo:assistant-draft:account-smb-admin', '{broken');

    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });
  });

  it('rejects draft writes from accounts that cannot manage assistants', () => {
    expect(
      repository.saveAssistantDraft(
        'account-internal-employee',
        createEmptyAssistantDraft(),
      ),
    ).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
  });

  it('answers trial questions from fixtures according to sources and rules', () => {
    const draft = completeDraft();

    const cited = repository.previewTrialAnswer('account-smb-admin', {
      questionId: 'trial-refund-window',
      sources: draft.sources,
      rules: draft.rules,
    });
    const strictGeneral = repository.previewTrialAnswer('account-smb-admin', {
      questionId: 'trial-leather-care',
      sources: draft.sources,
      rules: draft.rules,
    });
    const allowedGeneral = repository.previewTrialAnswer('account-smb-admin', {
      questionId: 'trial-leather-care',
      sources: draft.sources,
      rules: { ...draft.rules, knowledgeScope: 'allow-general-knowledge' },
    });

    expect(cited).toMatchObject({
      status: 'ready',
      data: {
        kind: 'company-data',
        citation: { sourceId: 'knowledge-refund-policy', sourceName: '退換貨政策' },
      },
    });
    expect(strictGeneral).toMatchObject({
      status: 'ready',
      data: { kind: 'no-answer', text: draft.rules.refusalMessage },
    });
    expect(allowedGeneral).toMatchObject({
      status: 'ready',
      data: { kind: 'general-knowledge' },
    });
  });

  it('creates an assistant with mixed sources, clears the draft and lists it for its owner only', () => {
    repository.saveAssistantDraft('account-smb-admin', completeDraft());

    const created = repository.createAssistantFromDraft(
      'account-smb-admin',
      completeDraft(),
    );

    expect(created).toMatchObject({
      status: 'ready',
      data: {
        ownerAccountId: 'account-smb-admin',
        name: '客戶問答助理',
        status: 'ready',
        audience: 'members-and-external-customers',
        knowledgeBaseIds: ['knowledge-product-guide', 'knowledge-refund-policy'],
        databaseIds: ['database-orders', 'database-customer-records'],
      },
    });
    if (created.status !== 'ready') return;
    expect(created.data.id).toMatch(/^assistant-created-\d+$/);
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });

    const reloaded = new MockDemoRepository(DEMO_SEED, { storage });
    expect(reloaded.listAssistantConfigurations('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: expect.arrayContaining([
        expect.objectContaining({ id: created.data.id }),
      ]),
    });
    expect(reloaded.getAssistantSources('account-smb-admin', created.data.id)).toMatchObject({
      status: 'ready',
      data: expect.arrayContaining([{ id: 'database-orders', type: 'database' }]),
    });
    expect(reloaded.listAssistantConfigurations('account-internal-employee')).toEqual({
      status: 'ready',
      data: [],
    });
  });

  it('refuses to create an assistant from an incomplete draft', () => {
    const result = repository.createAssistantFromDraft('account-smb-admin', {
      ...completeDraft(),
      sources: [],
      testedQuestionIds: [],
    });

    expect(result).toMatchObject({
      status: 'validation-failed',
      errors: [
        { field: 'sources' },
        { field: 'trial' },
      ],
    });
  });
});
