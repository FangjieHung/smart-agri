import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { DatabaseSummaryView } from '../domain/database.model';
import type { DemoKeyValueStorage, RepositoryView, SendChatMessageResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { syncValue } from './sync-value.testing';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const CUSTOMER: AccountId = 'account-external-customer';
const ORDERS = 'database-orders';
const ASSISTANT = 'assistant-customer-service';
const ANSWERS = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

function repositoryFor(viewer: AccountId | null, storage: DemoKeyValueStorage, now = '2026-10-05T02:00:00.000Z') {
  return new MockDemoRepository(DEMO_SEED, { storage, now: () => new Date(now), viewer: () => viewer });
}

function summaryOf(result: RepositoryView<DatabaseSummaryView>): DatabaseSummaryView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function lastReplyKind(result: SendChatMessageResult): string {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  const last = result.data.messages.at(-1);
  if (last?.author !== 'assistant') throw new Error('expected an assistant reply');
  return last.reply.kind;
}

function idsOf(result: RepositoryView<readonly DatabaseSummaryView[]>): readonly string[] {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.map((item) => item.id);
}

describe('MockDemoRepository database archive (issue #180)', () => {
  it('lets only the owner archive and unarchive, idempotently, with the same denial as a missing database for anyone else', () => {
    const storage = createMemoryStorage();
    const admin = repositoryFor(ADMIN, storage);
    syncValue(admin.updateDatabaseAccess(ORDERS, [ADMIN, EMPLOYEE]));
    const missing = syncValue(admin.archiveDatabase('database-missing'));
    expect(missing).toMatchObject({ status: 'permission-denied', reason: 'database' });

    for (const viewer of [EMPLOYEE, CUSTOMER]) {
      expect(syncValue(repositoryFor(viewer, storage).archiveDatabase(ORDERS))).toEqual(missing);
      expect(syncValue(repositoryFor(viewer, storage).unarchiveDatabase(ORDERS))).toEqual(missing);
    }
    expect(syncValue(repositoryFor(null, storage).archiveDatabase(ORDERS))).toEqual(missing);
    // Unarchiving a database in use changes nothing.
    expect(summaryOf(syncValue(admin.unarchiveDatabase(ORDERS))).archivedAt).toBeNull();

    const archived = summaryOf(syncValue(admin.archiveDatabase(ORDERS)));
    expect(archived.archivedAt).toBe('2026-10-05T02:00:00.000Z');
    const later = repositoryFor(ADMIN, storage, '2026-10-06T02:00:00.000Z');
    expect(summaryOf(syncValue(later.archiveDatabase(ORDERS))).archivedAt).toBe('2026-10-05T02:00:00.000Z');

    expect(summaryOf(syncValue(later.unarchiveDatabase(ORDERS))).archivedAt).toBeNull();
    expect(summaryOf(syncValue(later.unarchiveDatabase(ORDERS))).archivedAt).toBeNull();
  });

  it('hides an archived database from the default list and shows it with the archived filter, the detail still opening', () => {
    const storage = createMemoryStorage();
    const admin = repositoryFor(ADMIN, storage);
    expect(idsOf(syncValue(admin.listDatabaseSummaries()))).toContain(ORDERS);
    expect(idsOf(syncValue(admin.listDatabaseSummaries('archived')))).toEqual([]);

    syncValue(admin.archiveDatabase(ORDERS));
    expect(idsOf(syncValue(admin.listDatabaseSummaries()))).not.toContain(ORDERS);
    expect(idsOf(syncValue(admin.listDatabaseSummaries('active')))).not.toContain(ORDERS);
    expect(idsOf(syncValue(admin.listDatabaseSummaries('archived')))).toEqual([ORDERS]);
    const detail = syncValue(admin.getDatabaseDetail(ORDERS));
    expect(detail).toMatchObject({ status: 'ready', data: { summary: { id: ORDERS, archivedAt: '2026-10-05T02:00:00.000Z' } } });

    syncValue(admin.unarchiveDatabase(ORDERS));
    expect(idsOf(syncValue(admin.listDatabaseSummaries()))).toContain(ORDERS);
  });

  it('refuses the form link like a missing database while archived, keeps own submissions and withdrawals, and restores on unarchive', () => {
    const storage = createMemoryStorage();
    const admin = repositoryFor(ADMIN, storage);
    const customer = repositoryFor(CUSTOMER, storage);
    const receipt = syncValue(customer.submitDatabaseEntry(ORDERS, { submissionId: 'key-1', formVersion: 1, consent: true, answers: ANSWERS }));
    if (receipt.status !== 'ready') throw new Error(`expected ready, got ${receipt.status}`);

    syncValue(admin.archiveDatabase(ORDERS));
    const missing = syncValue(customer.getDatabaseSubmissionForm('database-missing'));
    expect(syncValue(customer.getDatabaseSubmissionForm(ORDERS))).toEqual(missing);
    expect(syncValue(customer.reviewDatabaseSubmission(ORDERS, 1, ANSWERS))).toEqual(missing);
    expect(
      syncValue(customer.submitDatabaseEntry(ORDERS, { submissionId: 'key-2', formVersion: 1, consent: true, answers: ANSWERS })),
    ).toEqual(missing);

    // What was already submitted stays the member's to see and withdraw.
    const own = syncValue(customer.listOwnDatabaseSubmissions());
    if (own.status !== 'ready') throw new Error(`expected ready, got ${own.status}`);
    expect(own.data.map((item) => item.id)).toContain(receipt.data.id);
    expect(syncValue(customer.withdrawDatabaseSubmission(receipt.data.id))).toMatchObject({ status: 'ready' });

    syncValue(admin.unarchiveDatabase(ORDERS));
    expect(syncValue(customer.getDatabaseSubmissionForm(ORDERS))).toMatchObject({ status: 'ready' });
  });

  it('keeps the assistant connection but offers no form, query or connection while archived', async () => {
    const storage = createMemoryStorage();
    const admin = repositoryFor(ADMIN, storage);
    const customer = repositoryFor(CUSTOMER, storage);
    expect(lastReplyKind(customer.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題'))).toBe('form-request');
    expect(lastReplyKind(admin.sendChatMessage(ADMIN, ASSISTANT, '近 30 天有幾筆訂單問題回報？'))).toBe('database-query');

    syncValue(admin.archiveDatabase(ORDERS));
    expect(lastReplyKind(customer.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題'))).not.toBe('form-request');
    expect(syncValue(customer.listChatForms(CUSTOMER, ASSISTANT))).toEqual({ status: 'ready', data: [] });
    expect(
      syncValue(customer.submitChatForm(CUSTOMER, ASSISTANT, {
        formId: ORDERS, formVersion: 1, submissionId: crypto.randomUUID(), answers: ANSWERS, consent: true,
      })),
    ).toMatchObject({ status: 'permission-denied', reason: 'assistant-form' });
    expect(lastReplyKind(admin.sendChatMessage(ADMIN, ASSISTANT, '近 30 天有幾筆訂單問題回報？'))).not.toBe('database-query');

    const sources = await firstValueFrom(admin.listConnectableSources());
    if (sources.status !== 'ready') throw new Error(`expected ready, got ${sources.status}`);
    expect(sources.data.map((source) => source.id)).not.toContain(ORDERS);
    const settings = syncValue(admin.getAssistantSettings(ASSISTANT));
    if (settings.status !== 'ready') throw new Error(`expected ready, got ${settings.status}`);
    expect(settings.data.sources.map((source) => source.id)).toContain(ORDERS);

    syncValue(admin.unarchiveDatabase(ORDERS));
    expect(syncValue(customer.listChatForms(CUSTOMER, ASSISTANT))).toMatchObject({ status: 'ready', data: [{ id: ORDERS }] });
  });
});
