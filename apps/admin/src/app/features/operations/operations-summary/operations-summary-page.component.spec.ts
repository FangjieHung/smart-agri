import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of, throwError } from 'rxjs';
import type { OperationsSummaryView } from '../../../core/domain/operations.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { OperationsSummaryPageComponent } from './operations-summary-page.component';

const EMPTY: OperationsSummaryView = {
  from: '2026-09-01', to: '2026-09-30', assistants: [], mostCitedDocuments: [],
  knowledge: { processingFailedCount: 0, overduePendingReviewCount: 0 },
  issues: { openCount: 0, averageResolutionHours: null },
  databaseQueries: { totalCount: 0, answeredCount: 0, notPermittedCount: 0, insufficientRecordsCount: 0, failedCount: 0, failureRate: 0 },
};

async function render(result: unknown, waitForStable = true) {
  await TestBed.configureTestingModule({
    imports: [OperationsSummaryPageComponent],
    providers: [
      provideRouter([]),
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-smb-admin' } },
      { provide: DEMO_REPOSITORY, useValue: { getOperationsSummary: () => result } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OperationsSummaryPageComponent);
  fixture.detectChanges();
  if (waitForStable) await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('OperationsSummaryPageComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('shows loading while the operational summary is pending', async () => {
    const page = await render(NEVER, false);
    expect(page.textContent).toContain('正在載入營運追蹤');
  });

  it('shows permission denied and connection errors separately', async () => {
    const denied = await render(of({ status: 'permission-denied', reason: 'assistant-configuration', message: 'denied' }));
    expect(denied.textContent).toContain('你沒有查看營運追蹤的權限');
    TestBed.resetTestingModule();
    const failed = await render(throwError(() => new Error('offline')));
    expect(failed.textContent).toContain('目前無法載入營運追蹤');
  });

  it('distinguishes an empty period from a populated summary', async () => {
    const empty = await render(of({ status: 'ready', data: EMPTY }));
    expect(empty.textContent).toContain('這段期間尚無助理回覆');
    expect(empty.textContent).toContain('尚無已解決事項');
    expect(empty.textContent).toContain('這段期間沒有數據庫查詢回答');
    TestBed.resetTestingModule();
    const populated = await render(of({ status: 'ready', data: {
      ...EMPTY,
      assistants: [{ assistantId: 'assistant-1', assistantName: '客服助理', totalReplies: 12, noResultRate: .25, rejectedCitationRate: .1 }],
      mostCitedDocuments: [{ documentId: 'document-1', documentName: '退換貨辦法', count: 8 }],
      issues: { openCount: 2, averageResolutionHours: 18 },
    } }));
    expect(populated.textContent).toContain('客服助理');
    expect(populated.textContent).toContain('25%');
    expect(populated.textContent).toContain('退換貨辦法：8 次');
  });

  it('shows database query answers as their own category, apart from the reply rates', async () => {
    const page = await render(of({ status: 'ready', data: {
      ...EMPTY,
      assistants: [{ assistantId: 'assistant-1', assistantName: '客服助理', totalReplies: 4, noResultRate: .5, rejectedCitationRate: 0 }],
      databaseQueries: { totalCount: 8, answeredCount: 3, notPermittedCount: 1, insufficientRecordsCount: 2, failedCount: 2, failureRate: .25 },
    } }));
    const cards = Array.from(page.querySelectorAll('[aria-label="數據庫查詢摘要"] article')).map((card) => card.textContent?.replace(/\s+/g, ''));
    expect(cards).toEqual(['查詢回答8', '成功3', '無權限1', '紀錄不足2', '失敗2', '失敗率25%']);
    expect(page.textContent).toContain('不計入上方的回覆數、查無資料率與引用錯誤率');
    // The assistant's own row is untouched by the query answers.
    const row = page.querySelector('tbody tr')?.textContent?.replace(/\s+/g, '');
    expect(row).toBe('客服助理450%0%');
  });
});
