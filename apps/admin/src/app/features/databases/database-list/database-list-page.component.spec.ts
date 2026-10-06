import { Component } from '@angular/core';
import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { map, of, throwError } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseListPageComponent } from './database-list-page.component';

@Component({ template: '' })
class DetailStubComponent {}

async function openList(
  accountId: AccountId = 'account-smb-admin',
  /** 在畫面建立前替換 repository 的行為（例如模擬讀取或儲存失敗）。 */
  arrange: (repository: MockDemoRepository) => void = () => undefined,
  apiMode = false,
) {
  const testing = provideDatabaseTesting(accountId);
  arrange(testing.repository);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      ...(apiMode ? [{ provide: ApiSessionService, useValue: { apiMode: true } }] : []),
      provideRouter([
        { path: 'app/databases', component: DatabaseListPageComponent },
        { path: 'app/databases/:id/:tab', component: DetailStubComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/app/databases');
  // 清單與模板都是非同步契約（rxResource）：等第一次讀取完成再畫面。
  await harness.fixture.whenStable();
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
    expect(customer?.textContent).toContain('安心商行管理者');
    expect(Array.from(page().querySelectorAll('lib-data-table thead th')).map((th) => th.textContent?.trim())).toContain('擁有者');
  });

  it('shows the assistants the repository returns in API mode too, with the same empty state as mock (#188)', async () => {
    const { page } = await openList(
      'account-smb-admin',
      (repository) => {
        const real = repository.listDatabaseSummaries.bind(repository);
        vi.spyOn(repository, 'listDatabaseSummaries').mockImplementation((...args) =>
          real(...args).pipe(
            map((outcome) =>
              outcome.status === 'ready'
                ? {
                    ...outcome,
                    data: outcome.data.map((item) => ({
                      ...item,
                      connectedAssistantNames: item.name === '客戶資料庫' ? ['客服助理', '門市小幫手'] : [],
                    })),
                  }
                : outcome,
            ),
          ),
        );
      },
      true,
    );
    const rows = Array.from(page().querySelectorAll('lib-data-table tbody tr'));
    const connected = rows.find((row) => row.textContent?.includes('客戶資料庫'));
    const empty = rows.find((row) => !row.textContent?.includes('客戶資料庫'));

    expect(connected?.textContent).toContain('客服助理、門市小幫手');
    expect(empty?.textContent).toContain('尚未連接');
    expect(page().textContent).not.toContain('將於後續版本開放');
  });

  it('keeps the dialog and the typed name when saving fails, and creates on retry', async () => {
    const { harness, page, repository, router } = await openList();
    const create = vi.spyOn(repository, 'createDatabaseFromTemplate');
    create.mockReturnValueOnce(throwError(() => new Error('network down')));

    const dialog = openCreateDialog(page(), harness);
    const name = dialog.querySelector<HTMLInputElement>('#database-name');
    if (name) {
      name.value = '門市回報';
      name.dispatchEvent(new Event('input'));
    }
    button(dialog, '建立資料庫').click();
    harness.detectChanges();

    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('目前無法建立資料庫');
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
    expect(dialog.querySelector<HTMLInputElement>('#database-name')?.value).toBe('門市回報');
    expect(router.url).toBe('/app/databases');

    button(dialog, '建立資料庫').click();
    await harness.fixture.whenStable();

    expect(create).toHaveBeenCalledTimes(2);
    expect(create).toHaveBeenLastCalledWith({ templateId: 'template-customer-profile', name: '門市回報' });
    expect(router.url).toMatch(/^\/app\/databases\/database-created-\d+\/form$/);
  });

  it('shows a validation message from the repository next to the name', async () => {
    const { harness, page } = await openList('account-smb-admin', (repository) => {
      vi.spyOn(repository, 'createDatabaseFromTemplate').mockReturnValue(
        of({ status: 'validation-failed', message: '資料庫名稱請在 40 個字以內。' }),
      );
    });

    const dialog = openCreateDialog(page(), harness);
    button(dialog, '建立資料庫').click();
    harness.detectChanges();

    expect(dialog.querySelector('#database-name-error')?.textContent).toContain('資料庫名稱請在 40 個字以內。');
    expect(dialog.querySelector('#database-name')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('moves an archived database out of the default list into the archived filter, with a badge (#180)', async () => {
    const { harness, page, repository } = await openList('account-smb-admin', (repository) => {
      repository.archiveDatabase('database-orders').subscribe();
    });
    const list = vi.spyOn(repository, 'listDatabaseSummaries');
    const rows = () => Array.from(page().querySelectorAll('lib-data-table tbody tr'));

    expect(button(page(), '使用中').getAttribute('aria-pressed')).toBe('true');
    expect(rows().map((row) => row.querySelector('th a')?.textContent)).toEqual(['客戶資料庫']);
    expect(page().querySelector('app-status-badge')).toBeNull();

    button(page(), '已封存').click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(list).toHaveBeenLastCalledWith('archived');
    expect(button(page(), '已封存').getAttribute('aria-pressed')).toBe('true');
    expect(rows()).toHaveLength(1);
    expect(rows()[0].querySelector('th a')?.textContent).toBe('訂單資料庫');
    expect(rows()[0].querySelector('app-status-badge')?.textContent).toContain('已封存');

    button(page(), '使用中').click();
    await harness.fixture.whenStable();
    harness.detectChanges();
    expect(list).toHaveBeenLastCalledWith('active');
    expect(rows()).toHaveLength(1);
  });

  it('says there is nothing archived instead of offering to create one in the archived filter', async () => {
    const { harness, page } = await openList();
    button(page(), '已封存').click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(page().querySelector('lib-data-table')).toBeNull();
    expect(page().querySelector('#empty-archived-title')?.textContent).toContain('沒有已封存的資料庫');
    expect(page().querySelector('#empty-database-title')).toBeNull();
  });

  it('shows an empty state with a way to start from a template', async () => {
    const { page } = await openList('account-smb-admin', (repository) => {
      vi.spyOn(repository, 'listDatabaseSummaries').mockReturnValue(of({ status: 'ready', data: [] }));
    });

    expect(page().querySelector('lib-data-table')).toBeNull();
    expect(page().querySelector('#empty-database-title')?.textContent).toContain('還沒有資料庫');
    expect(button(page(), '從模板建立資料庫')).toBeTruthy();
  });

  it('tells a load failure apart from having no databases', async () => {
    const { page } = await openList('account-smb-admin', (repository) => {
      vi.spyOn(repository, 'listDatabaseSummaries').mockReturnValue(throwError(() => new Error('500')));
    });

    expect(page().textContent).toContain('目前無法載入資料庫');
    expect(page().querySelector('#empty-database-title')).toBeNull();
  });

  it('offers to reload templates when they fail to load, instead of hiding creation silently', async () => {
    const { harness, page, repository } = await openList('account-smb-admin', (repository) => {
      vi.spyOn(repository, 'listDatabaseTemplates').mockReturnValueOnce(throwError(() => new Error('500')));
    });

    expect(page().querySelector('.templates-error')?.textContent).toContain('目前無法載入資料庫模板');
    expect(page().querySelector('button[page-header-actions]')).toBeNull();

    button(page(), '重新載入模板').click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(repository.listDatabaseTemplates).toHaveBeenCalledTimes(2);
    expect(button(page(), '新增資料庫')).toBeTruthy();
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
