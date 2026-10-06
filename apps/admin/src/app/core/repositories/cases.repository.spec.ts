import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../domain/account.model';
import {
  CASE_ACTION_DENIED_MESSAGE,
  CASE_CHANGED_MESSAGE,
  CASE_DUE_IN_PAST_MESSAGE,
  CASE_RESOLUTION_REQUIRED_MESSAGE,
  CASE_TITLE_REQUIRED_MESSAGE,
  CASE_TYPE_INACTIVE_MESSAGE,
  type CreateCaseRequest,
} from '../domain/case.model';
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
    expect(ready(await firstValueFrom(repository.list())).map((item) => item.id)).toEqual(['case-cold-room', 'case-greenhouse-sensor']);
    expect(ready(await firstValueFrom(repository.list({ status: 'closed' }))).map((item) => item.id)).toEqual(['case-irrigation-done']);
    expect(ready(await firstValueFrom(repository.list({ status: 'all' }))).map((item) => item.id))
      .toEqual(['case-cold-room', 'case-greenhouse-sensor', 'case-irrigation-done']);
    expect(await firstValueFrom(repository.get('case-compressor-purchase'))).toEqual(DENIED);
    expect(await firstValueFrom(repository.get('case-that-does-not-exist'))).toEqual(DENIED);

    activeAccountId.set('account-smb-admin');
    expect(ready(await firstValueFrom(repository.list())).map((item) => item.id))
      .toEqual(['case-compressor-purchase', 'case-cold-room', 'case-greenhouse-sensor']);
    expect(ready(await firstValueFrom(repository.list({ scope: 'created' }))).map((item) => item.id))
      .toEqual(['case-compressor-purchase', 'case-greenhouse-sensor']);
    expect(ready(await firstValueFrom(repository.list({ scope: 'my-groups' }))).map((item) => item.id)).toEqual(['case-compressor-purchase']);
    expect(ready(await firstValueFrom(repository.list({ groupId: 'case-group-equipment', status: 'all' }))).map((item) => item.id))
      .toEqual(['case-cold-room', 'case-greenhouse-sensor', 'case-irrigation-done']);
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

  it('accepts, transfers and completes like the API: the former owner keeps seeing the case, and each action is one event', async () => {
    const { repository, activeAccountId } = mockRepository();
    const first = ready(await firstValueFrom(repository.get('case-cold-room')));
    expect(first.allowedActions).toEqual(['accept', 'cancel', 'comment']);
    expect(first.cancelReasonRequired).toBe(false);

    const accepted = ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 1 })));
    expect(accepted.case).toMatchObject({ status: 'in-progress', owner: { id: 'account-internal-employee' }, eventCount: 2 });
    expect(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 1 })))
      .toEqual({ status: 'changed', message: CASE_CHANGED_MESSAGE });
    expect(await firstValueFrom(repository.act('case-cold-room', 'complete', { eventCount: 2, resolution: ' ' }))).toMatchObject({
      status: 'validation-failed', reason: 'resolution-required', fieldErrors: { resolution: CASE_RESOLUTION_REQUIRED_MESSAGE },
    });
    expect(await firstValueFrom(repository.act('case-cold-room', 'transfer', { eventCount: 2, groupId: 'case-group-equipment' }))).toMatchObject({
      status: 'validation-failed', reason: 'case-group-unchanged',
    });

    const moved = ready(await firstValueFrom(repository.act('case-cold-room', 'transfer', { eventCount: 2, groupId: 'case-group-purchasing', note: '需要採購' })));
    expect(moved.case).toMatchObject({ status: 'pending', owner: null, group: { id: 'case-group-purchasing' } });
    expect(moved.allowedActions).toEqual(['cancel', 'comment']);
    expect(moved.events.at(-1)).toMatchObject({ action: 'transferred', fromGroup: { name: '設備組' }, toGroup: { name: '採購組' }, note: '需要採購' });

    // 管理者是採購組成員：受理、要求補件；建立者補充後自動回到處理中；管理者完成。
    activeAccountId.set('account-smb-admin');
    ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 3 })));
    ready(await firstValueFrom(repository.act('case-cold-room', 'request-info', { eventCount: 4, note: '請補照片' })));
    activeAccountId.set('account-internal-employee');
    expect(await firstValueFrom(repository.act('case-cold-room', 'complete', { eventCount: 5, resolution: '已修好' })))
      .toEqual({ status: 'permission-denied', reason: 'case-action', message: CASE_ACTION_DENIED_MESSAGE });
    const answered = ready(await firstValueFrom(repository.act('case-cold-room', 'comment', { eventCount: 5, note: '照片已上傳' })));
    expect(answered.case.status).toBe('in-progress');
    activeAccountId.set('account-smb-admin');
    const done = ready(await firstValueFrom(repository.act('case-cold-room', 'complete', { eventCount: 6, resolution: '已更換溫控器' })));
    expect(done.case).toMatchObject({ status: 'completed', resolution: '已更換溫控器' });
    expect(done.allowedActions).toEqual([]);
    expect(done.events.map((event) => [event.action, event.actor?.displayName])).toEqual([
      ['created', '安心商行客服同仁'],
      ['accepted', '安心商行客服同仁'],
      ['transferred', '安心商行客服同仁'],
      ['accepted', '安心商行管理者'],
      ['info-requested', '安心商行管理者'],
      ['commented', '安心商行客服同仁'],
      ['completed', '安心商行管理者'],
    ]);
    expect(await firstValueFrom(repository.act('case-cold-room', 'comment', { eventCount: 7, note: '再補充' })))
      .toEqual({ status: 'changed', message: CASE_CHANGED_MESSAGE });
  });

  it('refuses a due time in the past without writing anything', async () => {
    const { repository } = mockRepository();
    ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 1 })));
    expect(await firstValueFrom(repository.act('case-cold-room', 'set-due', { eventCount: 2, dueAt: new Date(Date.now() - 60_000).toISOString() })))
      .toMatchObject({ status: 'validation-failed', reason: 'due-in-past', fieldErrors: { dueAt: CASE_DUE_IN_PAST_MESSAGE } });
    expect(ready(await firstValueFrom(repository.get('case-cold-room'))).case.eventCount).toBe(2);
    const due = new Date(Date.now() + 86_400_000).toISOString();
    const changed = ready(await firstValueFrom(repository.act('case-cold-room', 'set-due', { eventCount: 2, dueAt: due })));
    expect(changed.case.dueAt).toBe(due);
  });

  describe('逾期提示 (issue #250)', () => {
    afterEach(() => {
      vi.useRealTimers();
    });

    /** 只假造 `Date`：mock 以「現在」判斷逾期，與後端注入的時鐘相同的邊界。 */
    function at(iso: string): void {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date(iso));
    }

    async function ids(repository: CasesRepository, filter: Parameters<CasesRepository['list']>[0]): Promise<string[]> {
      return ready(await firstValueFrom(repository.list(filter))).map((item) => item.id);
    }

    it('counts the owner\'s overdue cases and the group\'s overdue pending ones, never extra for the manager, and moves with a transfer', async () => {
      at('2026-10-07T01:00:00.000Z');
      const { repository, activeAccountId } = mockRepository();

      // 客服同仁（設備組）：「溫室感測器離線」待受理且已逾期；「冷藏庫溫度降不下來」10/9 才到時限。
      expect(ready(await firstValueFrom(repository.attention())))
        .toEqual({ overdueCount: 1, ownedOverdueCount: 0, groupPendingOverdueCount: 1, pendingForMeCount: 2 });
      expect(await ids(repository, { overdue: true })).toEqual(['case-greenhouse-sensor']);

      // 管理者（採購組成員）看得到全部，但不因為是管理者而多算。
      activeAccountId.set('account-smb-admin');
      expect(await ids(repository, { overdue: true })).toEqual(['case-greenhouse-sensor']);
      expect(ready(await firstValueFrom(repository.attention())))
        .toEqual({ overdueCount: 0, ownedOverdueCount: 0, groupPendingOverdueCount: 0, pendingForMeCount: 1 });

      // 同仁受理：改算在負責人身上。
      activeAccountId.set('account-internal-employee');
      ready(await firstValueFrom(repository.act('case-greenhouse-sensor', 'accept', { eventCount: 1 })));
      expect(ready(await firstValueFrom(repository.attention())))
        .toEqual({ overdueCount: 1, ownedOverdueCount: 1, groupPendingOverdueCount: 0, pendingForMeCount: 1 });

      // 待補件照樣計時。
      ready(await firstValueFrom(repository.act('case-greenhouse-sensor', 'request-info', { eventCount: 2, note: '請補感測器序號' })));
      expect(await ids(repository, { scope: 'owned', overdue: true })).toEqual(['case-greenhouse-sensor']);

      // 轉給採購組：清空負責人，改算給新的承辦組。
      ready(await firstValueFrom(repository.act('case-greenhouse-sensor', 'transfer', { eventCount: 3, groupId: 'case-group-purchasing' })));
      expect(ready(await firstValueFrom(repository.attention())))
        .toEqual({ overdueCount: 0, ownedOverdueCount: 0, groupPendingOverdueCount: 0, pendingForMeCount: 1 });
      activeAccountId.set('account-smb-admin');
      expect(ready(await firstValueFrom(repository.attention())))
        .toEqual({ overdueCount: 1, ownedOverdueCount: 0, groupPendingOverdueCount: 1, pendingForMeCount: 2 });

      activeAccountId.set('account-external-customer');
      expect(await firstValueFrom(repository.attention())).toEqual(DENIED);
    });

    it('is overdue only after the due time, and completed or cancelled cases never count', async () => {
      at('2026-10-09T01:00:00.000Z');
      const { repository } = mockRepository();
      expect(await ids(repository, { overdue: true })).toEqual(['case-greenhouse-sensor']);

      at('2026-10-09T01:00:00.001Z');
      expect(await ids(repository, { overdue: true })).toEqual(['case-cold-room', 'case-greenhouse-sensor']);
      expect(await ids(repository, { status: 'all', overdue: true })).toEqual(['case-cold-room', 'case-greenhouse-sensor']);

      ready(await firstValueFrom(repository.act('case-cold-room', 'cancel', { eventCount: 1 })));
      expect(await ids(repository, { status: 'all', overdue: true })).toEqual(['case-greenhouse-sensor']);
      expect(await ids(repository, { status: 'closed', overdue: true })).toEqual([]);
    });

    it('makes the side navigation\'s number the owned and my-groups pending filters with overdue', async () => {
      at('2026-10-20T00:00:00.000Z');
      const { repository } = mockRepository();
      ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 1 })));

      const attention = ready(await firstValueFrom(repository.attention()));
      const owned = await ids(repository, { scope: 'owned', overdue: true });
      const groupPending = await ids(repository, { scope: 'my-groups', status: 'pending', overdue: true });
      expect([owned, groupPending]).toEqual([['case-cold-room'], ['case-greenhouse-sensor']]);
      expect(owned.length + groupPending.length).toBe(attention.overdueCount);
      expect(await ids(repository, { scope: 'my-groups', status: 'pending' })).toHaveLength(attention.pendingForMeCount);
    });
  });
});
