import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseDetailPageComponent } from './database-detail-page.component';

async function openDetail(url: string) {
  const testing = provideDatabaseTesting();
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([{ path: 'app/databases/:id/:tab', component: DatabaseDetailPageComponent }]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
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

describe('DatabaseDetailPageComponent', () => {
  it('renders the five detail tabs with the active tab marked for assistive tech', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/form');
    const tabs = Array.from(page().querySelectorAll('nav.tabs a'));

    expect(page().querySelector('h1')?.textContent).toContain('客戶資料庫');
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual(['表單設計', '收集紀錄', '趨勢比較', '已連接助理', '權限']);
    expect(page().querySelector('nav.tabs')?.getAttribute('aria-label')).toBe('資料庫頁籤');
    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('表單設計');
    expect(page().querySelector('app-form-designer')).not.toBeNull();
    expect(page().querySelector('app-form-trial')).not.toBeNull();
  });

  it('saves edited fields through the repository and confirms it', async () => {
    const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
    const label = page().querySelector<HTMLInputElement>('#field-label-field-order-number');
    if (label) {
      label.value = '訂單號碼';
      label.dispatchEvent(new Event('input'));
    }
    button(page(), '儲存表單').click();
    harness.detectChanges();

    expect(page().querySelector('.designer-feedback')?.textContent).toContain('表單已儲存');
    const detail = repository.getDatabaseDetail('account-smb-admin', 'database-orders');
    expect(detail.status === 'ready' && detail.data.fields[0].label).toBe('訂單號碼');
  });

  it('shows validation errors from the repository instead of saving', async () => {
    const { harness, page } = await openDetail('/app/databases/database-orders/form');
    const label = page().querySelector<HTMLInputElement>('#field-label-field-order-number');
    if (label) {
      label.value = '';
      label.dispatchEvent(new Event('input'));
    }
    button(page(), '儲存表單').click();
    harness.detectChanges();

    expect(page().querySelector('app-form-designer [role="alert"]')?.textContent).toContain('還有欄位需要修正');
    expect(page().textContent).toContain('請填寫欄位名稱。');
  });

  it('runs a trial fill against the saved form and previews it', async () => {
    const { harness, page } = await openDetail('/app/databases/database-orders/form');
    button(page(), '送出試填').click();
    harness.detectChanges();
    expect(page().textContent).toContain('「訂單編號」為必填。');

    const order = page().querySelector<HTMLInputElement>('#trial-field-order-number');
    if (order) {
      order.value = 'A-1024';
      order.dispatchEvent(new Event('input'));
    }
    page().querySelector<HTMLInputElement>('input[name="trial-field-issue-type"][value="商品瑕疵"]')?.click();
    const date = page().querySelector<HTMLInputElement>('#trial-field-reported-on');
    if (date) {
      date.value = '2026-09-22';
      date.dispatchEvent(new Event('input'));
    }
    button(page(), '送出試填').click();
    harness.detectChanges();

    expect(page().querySelector('.trial-preview')?.textContent).toContain('A-1024');
  });

  it('shows a subject’s timeline on the 收集紀錄 tab and switches subjects through the URL', async () => {
    const { harness, page, router } = await openDetail('/app/databases/database-customer-records/records');
    const select = page().querySelector<HTMLSelectElement>('#subject-select');

    expect(page().querySelector('label[for="subject-select"]')?.textContent).toContain('追蹤對象');
    expect(Array.from(select?.options ?? []).map((option) => option.textContent?.trim())).toEqual([
      '王小姐（4 筆）',
      '林小姐（2 筆）',
      '陳先生（1 筆）',
    ]);
    expect(page().querySelectorAll('ol.timeline > li')).toHaveLength(4);
    expect(page().textContent).toContain('只顯示使用者明確同意提交的紀錄');

    if (select) {
      select.value = 'subject-chen';
      select.dispatchEvent(new Event('change'));
    }
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(router.url).toBe('/app/databases/database-customer-records/records?subject=subject-chen');
    expect(page().querySelectorAll('ol.timeline > li')).toHaveLength(1);
  });

  it('shows the comparison and trend on the 趨勢比較 tab', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/trends?subject=subject-wang');

    expect(page().querySelector('.trend-conclusion')?.textContent).toContain('較首次 +2 分');
    expect(page().querySelectorAll('app-trend-chart')).toHaveLength(2);
  });

  it('explains instead of concluding when a subject has fewer than two records', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/trends?subject=subject-chen');

    expect(page().querySelector('.trend-conclusion')).toBeNull();
    expect(page().querySelector('.insufficient-records')?.textContent).toContain('累積 2 筆以上');
  });

  it('shows an empty state when nothing has been collected yet', async () => {
    const { page } = await openDetail('/app/databases/database-orders/records');

    expect(page().querySelector('#subject-select')).toBeNull();
    expect(page().textContent).toContain('還沒有收集紀錄');
  });

  it('lists connected assistants and the access rules', async () => {
    const assistants = await openDetail('/app/databases/database-customer-records/assistants');
    expect(assistants.page().querySelector('.assistant-list a')?.textContent).toContain('客服助理');

    TestBed.resetTestingModule();
    const access = await openDetail('/app/databases/database-customer-records/access');
    expect(access.page().querySelector('.access-list')?.textContent).toContain('安心商行管理者');
    expect(access.page().textContent).toContain('只有指定資料管理者可以查看結構化紀錄');
  });

  it('shows a generic permission state without leaking another account’s database name', async () => {
    const { page } = await openDetail('/app/databases/database-staff-checkins/form');

    expect(page().textContent).toContain('無法查看這個資料庫');
    expect(page().textContent).not.toContain('同仁排班回報');
    expect(page().querySelector('nav.tabs')).toBeNull();
  });

  it('falls back to the form tab for an unknown tab segment', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/unknown');

    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('表單設計');
  });
});
