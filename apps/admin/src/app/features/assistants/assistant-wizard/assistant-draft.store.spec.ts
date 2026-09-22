import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AccountId } from '../../../core/domain/account.model';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantDraftStore } from './assistant-draft.store';

function setup(storage = createMemoryStorage()) {
  const activeAccountId = signal<AccountId | null>('account-smb-admin');
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });

  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      AssistantDraftStore,
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId } },
    ],
  });

  return { store: TestBed.inject(AssistantDraftStore), activeAccountId, storage, repository };
}

describe('AssistantDraftStore', () => {
  it('prefills the draft from the customer-question template and autosaves it', () => {
    const { store } = setup();

    expect(store.saveStatusLabel()).toBe('尚未開始編輯');

    store.applyTemplate('answer-customer-questions');

    expect(store.draft()).toMatchObject({
      templateId: 'answer-customer-questions',
      name: '客戶問答助理',
      rules: { knowledgeScope: 'company-data-only' },
    });
    expect(store.draft().purpose).not.toBe('');
    expect(store.saveStatusLabel()).toContain('已自動儲存');
  });

  it('validates only the required fields of the step being left', () => {
    const { store } = setup();

    expect(store.errorsFor('purpose')).toEqual([]);
    expect(store.tryAdvance('purpose')).toBe(false);
    expect(store.errorsFor('purpose').map((error) => error.field)).toEqual([
      'name',
      'purpose',
      'audience',
    ]);
    expect(store.errorsFor('sources')).toEqual([]);

    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: true });

    expect(store.draft().audience).toBe('members-and-external-customers');
    expect(store.errorsFor('purpose')).toEqual([]);
    expect(store.tryAdvance('purpose')).toBe(true);
    expect(store.draft().currentStep).toBe('sources');
  });

  it('connects and disconnects mixed sources by connection id only', () => {
    const { store } = setup();

    store.toggleSource({ id: 'knowledge-product-guide', type: 'knowledge-base' });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
    store.toggleSource({ id: 'database-orders', type: 'database' });
    store.toggleSource({ id: 'database-customer-records', type: 'database' });
    store.toggleSource({ id: 'knowledge-product-guide', type: 'knowledge-base' });

    expect(store.draft().sources).toEqual([
      { id: 'knowledge-refund-policy', type: 'knowledge-base' },
      { id: 'database-orders', type: 'database' },
      { id: 'database-customer-records', type: 'database' },
    ]);
  });

  it('clears the data-write target when that database is disconnected', () => {
    const { store } = setup();

    store.toggleSource({ id: 'database-orders', type: 'database' });
    store.updateRules({ dataWriteDatabaseId: 'database-orders', dataWritePurpose: '記錄詢問' });
    store.toggleSource({ id: 'database-orders', type: 'database' });

    expect(store.draft().rules.dataWriteDatabaseId).toBeNull();
  });

  it('resumes the saved draft after a reload and isolates drafts per account', () => {
    const storage = createMemoryStorage();
    const first = setup(storage);
    first.store.applyTemplate('answer-customer-questions');
    first.store.setAudience({ internal: true, external: false });

    const second = setup(storage);

    expect(second.store.draft().name).toBe('客戶問答助理');
    expect(second.store.draft().audience).toBe('account-members');
    expect(second.store.saveStatusLabel()).toContain('已載入先前的草稿');

    second.activeAccountId.set('account-internal-employee');
    expect(second.store.draft().name).toBe('');
    expect(second.store.canManage()).toBe(false);
  });

  it('records trial answers and creates the assistant through the repository', () => {
    const { store, repository } = setup();
    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: true });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });

    expect(store.create()).toBeNull();

    const answer = store.runTrial('trial-refund-window');

    expect(answer?.kind).toBe('company-data');
    expect(store.draft().testedQuestionIds).toEqual(['trial-refund-window']);

    const assistantId = store.create();

    expect(assistantId).toMatch(/^assistant-created-\d+$/);
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({
      status: 'ready',
      data: null,
    });
  });
});
