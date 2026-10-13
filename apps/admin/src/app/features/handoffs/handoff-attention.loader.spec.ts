import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../core/domain/account.model';
import type { VisitorHandoffAttentionView } from '../../core/domain/handoff.model';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { HandoffsRepository } from '../../core/repositories/handoffs.repository';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { refreshHandoffPendingCount } from './handoff-attention.loader';

const ready = (pendingCount: number): RepositoryView<VisitorHandoffAttentionView> => ({ status: 'ready', data: { pendingCount } });

function setup(accountId: AccountId | null, answers: Observable<RepositoryView<VisitorHandoffAttentionView>>[]) {
  TestBed.resetTestingModule();
  const activeAccountId = signal<AccountId | null>(accountId);
  const attention = vi.fn<() => Observable<RepositoryView<VisitorHandoffAttentionView>>>();
  for (const answer of answers) attention.mockReturnValueOnce(answer);
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: HandoffsRepository, useValue: { attention } },
    ],
  });
  return { injector: TestBed.inject(Injector), attention, activeAccountId, count: signal(0) };
}

/** 側欄「轉真人」待處理數字的讀取程式（issue #355）：shell 換頁時以動態 `import()` 呼叫。 */
describe('refreshHandoffPendingCount', () => {
  it('writes an internal account\'s pending number into the side navigation\'s signal', async () => {
    const { injector, attention, count } = setup('account-internal-employee', [of(ready(2)), of(ready(1))]);

    await refreshHandoffPendingCount(injector, count);
    expect(count()).toBe(2);
    await refreshHandoffPendingCount(injector, count);
    expect(count()).toBe(1);
    expect(attention).toHaveBeenCalledTimes(2);
  });

  it('never asks for an external customer or without an account, and shows no number', async () => {
    for (const accountId of ['account-external-customer', null] as const) {
      const { injector, attention, count } = setup(accountId, [of(ready(5))]);
      count.set(4);
      await refreshHandoffPendingCount(injector, count);
      expect(attention).not.toHaveBeenCalled();
      expect(count()).toBe(0);
    }
  });

  it('is 0 on 403, keeps the last number when the read fails, and ignores an older read that answers late', async () => {
    const late = new Subject<RepositoryView<VisitorHandoffAttentionView>>();
    const { injector, count } = setup('account-smb-admin', [
      of(ready(4)),
      throwError(() => new Error('offline')),
      late,
      of(ready(7)),
      of({ status: 'permission-denied', reason: 'handoff', message: '轉真人通知只給助理的擁有者。' }),
    ]);

    await refreshHandoffPendingCount(injector, count);
    await refreshHandoffPendingCount(injector, count);
    expect(count()).toBe(4);

    const older = refreshHandoffPendingCount(injector, count);
    await refreshHandoffPendingCount(injector, count);
    late.next(ready(9));
    await older;
    expect(count()).toBe(7);

    await refreshHandoffPendingCount(injector, count);
    expect(count()).toBe(0);
  });
});
