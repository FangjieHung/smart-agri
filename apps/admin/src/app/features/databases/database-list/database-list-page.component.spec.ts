import { Component } from '@angular/core';
import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { AccountId } from '../../../core/domain/account.model';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseListPageComponent } from './database-list-page.component';

@Component({ template: '' })
class DetailStubComponent {}

async function openList(accountId: AccountId = 'account-smb-admin') {
  const testing = provideDatabaseTesting(accountId);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([
        { path: 'app/databases', component: DatabaseListPageComponent },
        { path: 'app/databases/:id/:tab', component: DetailStubComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/app/databases');
  harness.detectChanges();
  return {
    harness,
    page: () => harness.routeNativeElement as HTMLElement,
    repository: testing.repository,
    router: TestBed.inject(Router),
  };
}

function button(page: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(page.querySelectorAll('button')).find((b) => b.textContent?.includes(text));
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

function openCreateDialog(page: HTMLElement, harness: RouterTestingHarness): HTMLElement {
  button(page, '新增資料庫').click();
  harness.detectChanges();
  const dialog = document.querySelector<HTMLElement>('mat-dialog-container');
  if (!dialog) throw new Error('missing create dialog');
  return dialog;
}

describe('DatabaseListPageComponent', () => {
  it('lists the account’s databases with fields, records and connected assistants', async () => {
    const { page } = await openList();
    const cards = Array.from(page().querySelectorAll('lib-data-table tbody tr'));

    expect(page().querySelector('h1')?.textContent).toContain('數據庫');
    expect(cards).toHaveLength(2);
    const customer = cards.find((card) => card.textContent?.includes('客戶資料庫'));
    expect(customer?.querySelector('th[scope="row"] a')?.getAttribute('href')).toBe('/app/databases/database-customer-records/form');
    expect(customer?.textContent).toContain('6 個欄位');
    expect(customer?.textContent).toContain('3 位對象・7 筆紀錄');
    expect(customer?.textContent).toContain('客服助理');
  });

  it('starts creation by asking what to collect, offering the five templates', async () => {
    const { page, harness } = await openList();
    expect(page().querySelector('form.create-panel')).toBeNull();
    const picker = openCreateDialog(page(), harness).querySelector('fieldset.template-picker');

    expect(picker?.querySelector('legend')?.textContent).toContain('你要收集什麼？');
    expect(
      Array.from(picker?.querySelectorAll('label.template-option strong') ?? []).map((node) => node.textContent?.trim()),
    ).toEqual(['客戶基本資料', '定期回報', '滿意度調查', '症狀或進度追蹤', '空白模板']);
  });

  it('prefills the name from the chosen template and opens the new database’s form design', async () => {
    const { harness, page, router } = await openList();

    const dialog = openCreateDialog(page(), harness);
    dialog.querySelector<HTMLInputElement>('input[type="radio"][value="template-satisfaction"]')?.click();
    harness.detectChanges();
    const name = dialog.querySelector<HTMLInputElement>('#database-name');
    expect(name?.value).toBe('滿意度調查');

    if (name) {
      name.value = '門市滿意度調查';
      name.dispatchEvent(new Event('input'));
    }
    button(dialog, '建立資料庫').click();
    await harness.fixture.whenStable();

    expect(router.url).toMatch(/^\/app\/databases\/database-created-\d+\/form$/);
  });

  it('asks for a name before creating', async () => {
    const { harness, page, router } = await openList();

    const dialog = openCreateDialog(page(), harness);
    dialog.querySelector<HTMLInputElement>('input[type="radio"][value="template-blank"]')?.click();
    harness.detectChanges();
    const name = dialog.querySelector<HTMLInputElement>('#database-name');
    if (name) {
      name.value = ' ';
      name.dispatchEvent(new Event('input'));
    }
    button(dialog, '建立資料庫').click();
    harness.detectChanges();

    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('請輸入資料庫名稱');
    expect(name?.getAttribute('aria-invalid')).toBe('true');
    expect(router.url).toBe('/app/databases');
  });

  it('hides template creation from accounts that cannot manage data sources', async () => {
    const { page } = await openList('account-internal-employee');

    expect(page().querySelector('fieldset.template-picker')).toBeNull();
    expect(page().textContent).toContain('只有可管理資料來源的帳號可以建立資料庫');
    expect(page().textContent).toContain('同仁排班回報');
    expect(page().textContent).not.toContain('客戶資料庫');
  });

  it('clicking a non-link cell in the row navigates to the database detail', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page().querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('客戶資料庫'),
    ) as HTMLElement;
    const purposeCell = Array.from(row.querySelectorAll('td')).find((td) => !td.querySelector('a'));
    if (!purposeCell) throw new Error('fixture: no non-link cell found');

    (purposeCell as HTMLElement).click();
    await harness.fixture.whenStable();

    expect(router.url).toBe('/app/databases/database-customer-records/form');
  });

  it('pressing Enter on the focused row navigates to the database detail', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page().querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('客戶資料庫'),
    ) as HTMLElement;

    row.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await harness.fixture.whenStable();

    expect(router.url).toBe('/app/databases/database-customer-records/form');
  });

  it('clicking the name link navigates exactly once', async () => {
    const { page, harness, router } = await openList();
    const row = Array.from(page().querySelectorAll('lib-data-table tbody tr')).find((tr) =>
      tr.textContent?.includes('客戶資料庫'),
    ) as HTMLElement;
    const navigateSpy = vi.spyOn(router, 'navigate');

    row.querySelector('a')?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await harness.fixture.whenStable();

    expect(navigateSpy).toHaveBeenCalledTimes(0);
  });
});
