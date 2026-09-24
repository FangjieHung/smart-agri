import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { AccountId } from '../../../core/domain/account.model';
import { provideKnowledgeTesting } from '../knowledge.testing';
import { KnowledgeListPageComponent } from './knowledge-list-page.component';

async function openList(accountId: AccountId = 'account-smb-admin') {
  const testing = provideKnowledgeTesting(accountId);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([{ path: 'app/knowledge', component: KnowledgeListPageComponent }]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/app/knowledge');
  harness.detectChanges();
  return { page: harness.routeNativeElement as HTMLElement, repository: testing.repository };
}

describe('KnowledgeListPageComponent', () => {
  it('lists each knowledge base with counts, processing status, sharing scope and connected assistants', async () => {
    const { page } = await openList();
    const rows = Array.from(page.querySelectorAll('lib-data-table tbody tr'));

    expect(rows).toHaveLength(3);
    const guide = rows[0].textContent ?? '';
    expect(guide).toContain('商品使用指南');
    expect(guide).toContain('4 份文件');
    expect(guide).toContain('2 則 FAQ');
    expect(guide).toContain('2 項需要處理');
    expect(guide).toContain('指定帳號／團隊');
    expect(guide).toContain('客服助理');
    expect(guide).toContain('內部教育訓練助理');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe(
      '/app/knowledge/knowledge-product-guide/content',
    );
  });

  it('shows in-progress processing as text for knowledge bases that are still processing', async () => {
    const { page } = await openList();
    const shipping = Array.from(page.querySelectorAll('lib-data-table tbody tr')).find((row) =>
      row.textContent?.includes('配送常見問題'),
    );

    expect(shipping?.textContent).toContain('處理中');
  });

  it('never shows another account’s knowledge base', async () => {
    const { page } = await openList();

    expect(page.textContent).not.toContain('同仁個人筆記');
  });

  it('shows a permission state instead of the list when the scenario denies access', async () => {
    const testing = provideKnowledgeTesting();
    testing.repository.setScenario('permission-denied');
    TestBed.configureTestingModule({
      providers: [
        ...testing.providers,
        provideRouter([{ path: 'app/knowledge', component: KnowledgeListPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/knowledge');
    harness.detectChanges();
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('[role="alert"]')?.textContent).toContain('無法查看知識庫');
    expect(page.querySelector('lib-data-table')).toBeNull();
  });
});
