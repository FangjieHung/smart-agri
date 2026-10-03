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
});
