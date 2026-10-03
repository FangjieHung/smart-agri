import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { DatabaseSubmissionReceiptView, OwnDatabaseSubmissionView } from '../../../core/domain/database.model';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { syncValue } from '../../../core/repositories/sync-value.testing';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { provideDatabaseTesting } from '../../databases/databases.testing';
import { OwnSubmissionsComponent } from './own-submissions.component';

const ACTIVE: OwnDatabaseSubmissionView = {
  id: 'submission-1',
  receiptNumber: 'R-20261003-0000000001',
  submittedAt: '2026-10-03T02:00:00.000Z',
  databaseId: 'database-orders',
  databaseName: '訂單資料庫',
  formVersion: 1,
  source: 'form-link',
  withdrawnAt: null,
};

const WITHDRAWN_RECEIPT: DatabaseSubmissionReceiptView = {
  id: 'submission-1',
  receiptNumber: 'R-20261003-0000000001',
  submittedAt: '2026-10-03T02:00:00.000Z',
  databaseId: 'database-orders',
  databaseName: '訂單資料庫',
  purpose: '處理訂單問題',
  recipient: '安心商行（訂單資料庫）',
  viewers: [],
  formVersion: 1,
  source: 'form-link',
  entries: [],
  withdrawnAt: '2026-10-04T02:00:00.000Z',
};

async function render(repository: object, stable = true) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OwnSubmissionsComponent],
    providers: [
      provideRouter([]),
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId: signal('account-external-customer') } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OwnSubmissionsComponent);
  const settle = async () => {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  if (stable) await settle();
  else fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  const button = (label: string) => {
    const found = [...root.querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === label);
    if (!found) throw new Error(`no button ${label}`);
    return found;
  };
  return { root, settle, button };
}

function listing(response: Observable<RepositoryView<readonly OwnDatabaseSubmissionView[]>>) {
  return { listOwnDatabaseSubmissions: vi.fn(() => response), withdrawDatabaseSubmission: vi.fn() };
}

describe('OwnSubmissionsComponent (issue #146)', () => {
  it('keeps loading, failure, denial and an empty list apart', async () => {
    expect((await render(listing(NEVER), false)).root.textContent).toContain('正在載入你送出的資料');
    expect((await render(listing(throwError(() => new Error('offline'))))).root.textContent).toContain('目前無法載入你送出的資料');
    expect(
      (await render(listing(of({ status: 'permission-denied', reason: 'authorized-form', message: '不能看' })))).root.textContent,
    ).toContain('不能看');
    expect((await render(listing(of({ status: 'ready', data: [] })))).root.textContent).toContain('尚未送出任何資料');
  });

  it('withdraws only after confirmation, then shows the trail without content', async () => {
    const { providers, repository } = provideDatabaseTesting('account-external-customer');
    const submitted = syncValue(
      repository.submitDatabaseEntry('database-orders', {
        submissionId: 'key-1',
        formVersion: 1,
        consent: true,
        answers: { 'field-order-number': 'DEMO-4001', 'field-issue-type': '配送延遲', 'field-reported-on': '2026-09-21' },
      }),
    );
    if (submitted.status !== 'ready') throw new Error('expected ready');
    const withdraw = vi.spyOn(repository, 'withdrawDatabaseSubmission');
    const repo = (providers[0] as { useValue: unknown }).useValue as object;
    const { root, settle, button } = await render(repo);

    expect(root.textContent).toContain('有效');
    expect(root.querySelector('a')?.getAttribute('href')).toContain(`receipt=${submitted.data.id}`);

    button('撤回').click();
    await settle();
    expect(root.textContent).toContain('無法復原');
    button('取消').click();
    await settle();
    expect(root.querySelector('.confirm')).toBeNull();
    expect(withdraw).not.toHaveBeenCalled();

    button('撤回').click();
    await settle();
    button('確認撤回').click();
    await settle();

    expect(withdraw).toHaveBeenCalledWith(submitted.data.id);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('內容已刪除');
    expect(root.querySelector('li')?.getAttribute('data-withdrawn')).toBe('true');
    expect(root.textContent).toContain('已撤回');
    expect([...root.querySelectorAll('button')].some((candidate) => candidate.textContent?.trim() === '撤回')).toBe(false);
  });

  it('keeps the confirmation open after a failed withdrawal so it can be tried again', async () => {
    const repository = {
      listOwnDatabaseSubmissions: vi.fn(() => of({ status: 'ready' as const, data: [ACTIVE] })),
      withdrawDatabaseSubmission: vi
        .fn()
        .mockReturnValueOnce(throwError(() => new Error('503')))
        .mockReturnValueOnce(of({ status: 'ready', data: WITHDRAWN_RECEIPT })),
    };
    const { root, settle, button } = await render(repository);

    button('撤回').click();
    await settle();
    button('確認撤回').click();
    await settle();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('沒有任何變更');
    expect(button('確認撤回').disabled).toBe(false);

    button('確認撤回').click();
    await settle();
    expect(repository.withdrawDatabaseSubmission).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('R-20261003-0000000001');
  });

  it('shows a refusal from the server next to the confirmation', async () => {
    const repository = {
      listOwnDatabaseSubmissions: vi.fn(() => of({ status: 'ready' as const, data: [ACTIVE] })),
      withdrawDatabaseSubmission: vi.fn(() =>
        of({ status: 'permission-denied', reason: 'submission-withdrawal', message: '找不到這筆紀錄，或你沒有撤回它的權限。' }),
      ),
    };
    const { root, settle, button } = await render(repository);

    button('撤回').click();
    await settle();
    button('確認撤回').click();
    await settle();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('你沒有撤回它的權限');
    expect(repository.listOwnDatabaseSubmissions).toHaveBeenCalledTimes(2);
  });
});
