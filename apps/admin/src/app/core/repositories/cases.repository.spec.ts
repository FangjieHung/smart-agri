import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { CASE_DUE_IN_PAST_MESSAGE, CASE_TITLE_REQUIRED_MESSAGE, CASE_TYPE_INACTIVE_MESSAGE, type CreateCaseRequest } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { CASE_FEATURE_DENIED_MESSAGE, CASE_TYPE_GROUP_ARCHIVED_MESSAGE } from './case-settings.repository';
import { CasesRepository } from './cases.repository';

/** Demo 模式（沒有 API）：與後端相同的可見性與檢查（issue #248）。Hybrid 見 `cases.repository-hybrid.spec.ts`。 */
function mockRepository(accountId: AccountId | null = 'account-internal-employee') {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({ providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }] });
  return { repository: TestBed.inject(CasesRepository), activeAccountId };
}

function request(overrides: Partial<CreateCaseRequest> = {}): CreateCaseRequest {
  return {
    typeId: 'case-type-equipment-repair',
    groupId: 'case-group-equipment',
    dueAt: new Date(Date.now() + 72 * 3600 * 1000).toISOString(),
    title: '  溫控器顯示錯誤代碼 ',
    description: '三號溫室',
    ...overrides,
  };
}

function ready<T>(result: { status: string; data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

const DENIED = { status: 'permission-denied', reason: 'case', message: CASE_FEATURE_DENIED_MESSAGE };

describe('CasesRepository (mock)', () => {
  it('shows the creator, the current group\'s members and the manager their cases, open ones by default', async () => {
    const { repository, activeAccountId } = mockRepository();

    // 客服同仁：設備組成員，也是兩件設備組案件的建立者；看不到管理者在採購組建立的案件。
    expect(ready(await firstValueFrom(repository.list())).map((item) => item.id)).toEqual(['case-cold-room']);
    expect(ready(await firstValueFrom(repository.list({ status: 'closed' }))).map((item) => item.id)).toEqual(['case-irrigation-done']);
    expect(ready(await firstValueFrom(repository.list({ status: 'all' }))).map((item) => item.id))
      .toEqual(['case-cold-room', 'case-irrigation-done']);
    expect(await firstValueFrom(repository.get('case-compressor-purchase'))).toEqual(DENIED);
    expect(await firstValueFrom(repository.get('case-that-does-not-exist'))).toEqual(DENIED);

    activeAccountId.set('account-smb-admin');
    expect(ready(await firstValueFrom(repository.list())).map((item) => item.id)).toEqual(['case-compressor-purchase', 'case-cold-room']);
    expect(ready(await firstValueFrom(repository.list({ scope: 'created' }))).map((item) => item.id)).toEqual(['case-compressor-purchase']);
    expect(ready(await firstValueFrom(repository.list({ scope: 'my-groups' }))).map((item) => item.id)).toEqual(['case-compressor-purchase']);
    expect(ready(await firstValueFrom(repository.list({ groupId: 'case-group-equipment', status: 'all' }))).map((item) => item.id))
      .toEqual(['case-cold-room', 'case-irrigation-done']);
    expect(ready(await firstValueFrom(repository.list({ scope: 'owned' })))).toEqual([]);

    activeAccountId.set('account-external-customer');
    expect(await firstValueFrom(repository.list())).toEqual(DENIED);
    expect(await firstValueFrom(repository.get('case-cold-room'))).toEqual(DENIED);
    expect(await firstValueFrom(repository.create(request()))).toEqual(DENIED);
  });

  it('shows a linked conversation only as openable or not, and a record\'s state', async () => {
    const { repository, activeAccountId } = mockRepository();

    const detail = ready(await firstValueFrom(repository.get('case-cold-room')));
    expect(detail.links.thread).toEqual({ assistantId: 'assistant-internal-onboarding', threadId: 'thread-cold-room-deleted', canOpen: false });
    expect(JSON.stringify(detail)).not.toContain('thread-title');
    expect(detail.events.map((event) => event.action)).toEqual(['created']);

    activeAccountId.set('account-smb-admin');
    const purchase = ready(await firstValueFrom(repository.get('case-compressor-purchase')));
    expect(purchase.links.record).toEqual({
      databaseId: 'database-customer-records', submissionId: 'submission-mock-compressor', state: 'available', canRead: true,
    });
  });

  it('creates a pending case with its created event, and refuses what the API refuses', async () => {
    const { repository } = mockRepository();

    const created = ready(await firstValueFrom(repository.create(request())));
    expect(created.case).toMatchObject({
      title: '溫控器顯示錯誤代碼', description: '三號溫室', status: 'pending', origin: 'manual',
      type: { id: 'case-type-equipment-repair', name: '設備故障報修' },
      group: { id: 'case-group-equipment', name: '設備組', archived: false },
      createdBy: { id: 'account-internal-employee', displayName: '安心商行客服同仁' },
      owner: null,
    });
    expect(created.events).toHaveLength(1);
    expect(ready(await firstValueFrom(repository.list())).map((item) => item.title)).toContain('溫控器顯示錯誤代碼');

    expect(await firstValueFrom(repository.create(request({ title: '   ' })))).toMatchObject({
      status: 'validation-failed', reason: null, fieldErrors: { title: CASE_TITLE_REQUIRED_MESSAGE },
    });
    expect(await firstValueFrom(repository.create(request({ dueAt: new Date(Date.now() - 60_000).toISOString() })))).toMatchObject({
      status: 'validation-failed', reason: 'due-in-past', fieldErrors: { dueAt: CASE_DUE_IN_PAST_MESSAGE },
    });
    expect(await firstValueFrom(repository.create(request({ typeId: 'case-type-stocktake' })))).toMatchObject({
      status: 'validation-failed', reason: 'case-type-inactive', fieldErrors: { typeId: CASE_TYPE_INACTIVE_MESSAGE },
    });
    expect(await firstValueFrom(repository.create(request({ groupId: 'case-group-old-warehouse' })))).toMatchObject({
      status: 'validation-failed', reason: 'case-group-archived', fieldErrors: { groupId: CASE_TYPE_GROUP_ARCHIVED_MESSAGE },
    });
    expect(await firstValueFrom(repository.create(request({ previousCaseId: 'case-cold-room' })))).toMatchObject({
      status: 'validation-failed', reason: 'link-not-available',
    });
    const next = ready(await firstValueFrom(repository.create(request({ previousCaseId: 'case-irrigation-done' }))));
    expect(next.links.previousCase).toEqual({ caseId: 'case-irrigation-done', canOpen: true });
  });
});
