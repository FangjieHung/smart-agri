import { Component } from '@angular/core';
import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogState } from '@angular/material/dialog';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Subject, throwError } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import type { CreateKnowledgeBaseResult } from '../../../core/repositories/demo-repository';
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
  await settle(harness);
  return {
    harness,
    page: harness.routeNativeElement as HTMLElement,
    repository: testing.repository,
    router: TestBed.inject(Router),
  };
}

async function settle(harness: RouterTestingHarness): Promise<void> {
  harness.detectChanges();
  await harness.fixture.whenStable();
  harness.detectChanges();
}

function buttonIn(root: ParentNode, label: string): HTMLButtonElement | undefined {
  return Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
    button.textContent?.includes(label),
  );
}

function typeInto(selector: string, value: string): void {
  const field = document.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector);
  if (!field) throw new Error(`missing ${selector}`);
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

function rowNames(page: HTMLElement): string[] {
  return Array.from(page.querySelectorAll('lib-data-table tbody tr th[scope="row"]')).map(
    (cell) => cell.textContent?.trim() ?? '',
  );
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
    await settle(harness);
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('[role="alert"]')?.textContent).toContain('無法查看知識庫');
    expect(page.querySelector('lib-data-table')).toBeNull();
  });

  it('shows an error — not “還沒有知識庫” — when the list cannot be read', async () => {
    const testing = provideKnowledgeTesting();
    vi.spyOn(testing.repository, 'listKnowledgeBaseSummaries').mockReturnValue(throwError(() => new Error('503')));
    TestBed.configureTestingModule({
      providers: [
        ...testing.providers,
        provideRouter([{ path: 'app/knowledge', component: KnowledgeListPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/knowledge');
    await settle(harness);
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('[data-state="error"]')?.textContent).toContain('目前無法載入知識庫');
    expect(page.textContent).not.toContain('還沒有知識庫');
  });

  it('shows loading until the first read returns', async () => {
    const testing = provideKnowledgeTesting();
    testing.repository.setScenario('loading');
    TestBed.configureTestingModule({
      providers: [
        ...testing.providers,
        provideRouter([{ path: 'app/knowledge', component: KnowledgeListPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/knowledge');
    await settle(harness);

    expect(harness.routeNativeElement?.querySelector('[data-state="loading"]')?.textContent).toContain('正在載入知識庫');
  });

  describe('creating a knowledge base', () => {
    it('offers 建立知識庫 only to accounts with manage-data-sources', async () => {
      const admin = await openList();
      expect(buttonIn(admin.page, '建立知識庫')).toBeDefined();

      TestBed.resetTestingModule();
      const customer = await openList('account-external-customer');
      expect(buttonIn(customer.page, '建立知識庫')).toBeUndefined();
    });

    it('offers the same entry in the empty state, or explains who can create', async () => {
      const customer = await openList('account-external-customer');
      const empty = customer.page.querySelector('.empty-panel') as HTMLElement;
      expect(empty.textContent).toContain('還沒有知識庫');
      expect(empty.textContent).toContain('只有可管理資料來源的帳號可以建立知識庫。');

      TestBed.resetTestingModule();
      const testing = provideKnowledgeTesting();
      for (const id of ['knowledge-product-guide', 'knowledge-refund-policy', 'knowledge-shipping-faq']) {
        await new Promise<void>((resolve) =>
          testing.repository.deleteKnowledgeBase(id).subscribe({ complete: resolve }),
        );
      }
      TestBed.configureTestingModule({
        providers: [...testing.providers, provideRouter([{ path: 'app/knowledge', component: KnowledgeListPageComponent }])],
      });
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/app/knowledge');
      await settle(harness);
      const adminEmpty = (harness.routeNativeElement as HTMLElement).querySelector('.empty-panel') as HTMLElement;
      expect(buttonIn(adminEmpty, '建立知識庫')).toBeDefined();
    });

    it('creates a knowledge base from the dialog and reloads the list with it', async () => {
      const { page, harness } = await openList();

      buttonIn(page, '建立知識庫')?.click();
      await settle(harness);
      typeInto('#knowledge-name', '門市作業手冊');
      typeInto('#knowledge-purpose', '開店與結帳流程');
      document.querySelector<HTMLFormElement>('.create-panel')?.dispatchEvent(new Event('submit'));
      await settle(harness);

      expect(rowNames(page)).toContain('門市作業手冊');
      expect(page.querySelector('.feedback[role="status"]')?.textContent).toContain('已建立「門市作業手冊」');
      // 關閉動畫結束前 DOM 與 openDialogs 都還在；以對話框自己的狀態判斷已開始關閉。
      expect(TestBed.inject(MatDialog).openDialogs.map((ref) => ref.getState())).not.toContain(MatDialogState.OPEN);
    });

    it('keeps the dialog open with the message when the name is missing', async () => {
      const { page, harness, repository } = await openList();
      const create = vi.spyOn(repository, 'createKnowledgeBase');

      buttonIn(page, '建立知識庫')?.click();
      await settle(harness);
      typeInto('#knowledge-name', '   ');
      document.querySelector<HTMLFormElement>('.create-panel')?.dispatchEvent(new Event('submit'));
      await settle(harness);

      expect(create).not.toHaveBeenCalled();
      expect(document.querySelector('#knowledge-create-error[role="alert"]')?.textContent).toContain('請輸入知識庫名稱。');
    });

    it('shows the repository’s validation message and blocks a second submit while in flight', async () => {
      const { page, harness, repository } = await openList();
      const response = new Subject<CreateKnowledgeBaseResult>();
      const create = vi.spyOn(repository, 'createKnowledgeBase').mockReturnValue(response);

      buttonIn(page, '建立知識庫')?.click();
      await settle(harness);
      typeInto('#knowledge-name', '名稱');
      const form = document.querySelector<HTMLFormElement>('.create-panel') as HTMLFormElement;
      form.dispatchEvent(new Event('submit'));
      await settle(harness);
      expect(buttonIn(form, '建立知識庫')?.disabled).toBe(true);
      form.dispatchEvent(new Event('submit'));
      expect(create).toHaveBeenCalledTimes(1);

      response.next({ status: 'validation-failed', message: '知識庫名稱最多 100 個字。' });
      response.complete();
      await settle(harness);
      expect(document.querySelector('#knowledge-create-error')?.textContent).toContain('知識庫名稱最多 100 個字。');
    });
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
