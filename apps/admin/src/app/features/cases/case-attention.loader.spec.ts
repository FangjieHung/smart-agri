import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../core/domain/account.model';
import type { CaseAttentionView } from '../../core/domain/case.model';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { CasesRepository } from '../../core/repositories/cases.repository';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { refreshCaseOverdueCount } from './case-attention.loader';

const ready = (overdueCount: number): RepositoryView<CaseAttentionView> => ({
  status: 'ready',
  data: { overdueCount, ownedOverdueCount: overdueCount, groupPendingOverdueCount: 0, pendingForMeCount: 0 },
});

function setup(accountId: AccountId | null, answers: Observable<RepositoryView<CaseAttentionView>>[]) {
  TestBed.resetTestingModule();
  const activeAccountId = signal<AccountId | null>(accountId);
  const attention = vi.fn<() => Observable<RepositoryView<CaseAttentionView>>>();
  for (const answer of answers) attention.mockReturnValueOnce(answer);
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: CasesRepository, useValue: { attention } },
    ],
  });
  return { injector: TestBed.inject(Injector), attention, activeAccountId, count: signal(0) };
}

/** 側欄逾期數字的讀取程式（issue #250）：shell 換頁時以動態 `import()` 呼叫。 */
describe('refreshCaseOverdueCount', () => {
  it('writes an internal account\'s overdue number into the side navigation\'s signal', async () => {
    const { injector, attention, count } = setup('account-internal-employee', [of(ready(2)), of(ready(1))]);

    await refreshCaseOverdueCount(injector, count);
    expect(count()).toBe(2);
    await refreshCaseOverdueCount(injector, count);
    expect(count()).toBe(1);
    expect(attention).toHaveBeenCalledTimes(2);
  });

  it('never asks for an external customer or without an account, and shows no number', async () => {
    for (const accountId of ['account-external-customer', null] as const) {
      const { injector, attention, count } = setup(accountId, [of(ready(5))]);
      count.set(4);
      await refreshCaseOverdueCount(injector, count);
      expect(attention).not.toHaveBeenCalled();
      expect(count()).toBe(0);
    }
  });

  it('is 0 on 403, keeps the last number when the read fails, and ignores an older read that answers late', async () => {
    const late = new Subject<RepositoryView<CaseAttentionView>>();
    const { injector, count } = setup('account-smb-admin', [
      of(ready(4)),
      throwError(() => new Error('offline')),
      late,
      of(ready(7)),
      of({ status: 'permission-denied', reason: 'case', message: '案件功能只開放組織內部帳號使用。' }),
    ]);

    await refreshCaseOverdueCount(injector, count);
    await refreshCaseOverdueCount(injector, count);
    expect(count()).toBe(4);

    const older = refreshCaseOverdueCount(injector, count);
    await refreshCaseOverdueCount(injector, count);
    late.next(ready(9));
    await older;
    expect(count()).toBe(7);

    await refreshCaseOverdueCount(injector, count);
    expect(count()).toBe(0);
  });
});
