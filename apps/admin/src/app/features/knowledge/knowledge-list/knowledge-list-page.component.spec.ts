import { Component } from '@angular/core';
import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { AccountId } from '../../../core/domain/account.model';
import { provideKnowledgeTesting } from '../knowledge.testing';
import { KnowledgeListPageComponent } from './knowledge-list-page.component';

@Component({ template: '' })
class DetailStubComponent {}

async function openList(accountId: AccountId = 'account-smb-admin') {
  const testing = provideKnowledgeTesting(accountId);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([
        { path: 'app/knowledge', component: KnowledgeListPageComponent },
        { path: 'app/knowledge/:id/:tab', component: DetailStubComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/app/knowledge');
  harness.detectChanges();
  return {
    harness,
    page: harness.routeNativeElement as HTMLElement,
    repository: testing.repository,
    router: TestBed.inject(Router),
  };
}

describe('KnowledgeListPageComponent', () => {
  it('lists each knowledge base with counts, processing status, sharing scope and connected assistants', async () => {
    const { page } = await openList();
    const rows = Array.from(page.querySelectorAll('lib-data-table tbody tr'));

    expect(rows).toHaveLength(3);
    expect(rows[0].querySelector('th[scope="row"]')?.textContent?.trim()).toBe('商品使用指南');
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

  it('clicking a non-link cell in the row navigates to the knowledge base detail', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page.querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('商品使用指南'),
    ) as HTMLElement;
    const purposeCell = Array.from(row.querySelectorAll('td')).find((td) => !td.querySelector('a'));
    if (!purposeCell) throw new Error('fixture: no non-link cell found');

    (purposeCell as HTMLElement).click();
    await harness.fixture.whenStable();

    expect(router.url).toBe('/app/knowledge/knowledge-product-guide/content');
  });

  it('pressing Enter on the focused row navigates to the knowledge base detail', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page.querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('商品使用指南'),
    ) as HTMLElement;

    row.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await harness.fixture.whenStable();

    expect(router.url).toBe('/app/knowledge/knowledge-product-guide/content');
  });

  it('clicking the name link navigates exactly once', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page.querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('商品使用指南'),
    ) as HTMLElement;
    const navigateSpy = vi.spyOn(router, 'navigate');

    row.querySelector('a')?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await harness.fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledTimes(0);
  });
});
