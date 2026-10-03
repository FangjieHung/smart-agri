import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { map, of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import type { UpdateDatabaseFieldsResult } from '../../../core/repositories/demo-repository';
import { API_UPCOMING_DATABASE_FEATURES } from '../../../core/repositories/hybrid-demo-repository';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { syncValue } from '../../../core/repositories/sync-value.testing';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseDetailPageComponent } from './database-detail-page.component';

async function openDetail(url: string, arrange: (repository: MockDemoRepository) => void = () => undefined) {
  const testing = provideDatabaseTesting();
  arrange(testing.repository);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([{ path: 'app/databases/:id/:tab', component: DatabaseDetailPageComponent }]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  // 詳情是非同步契約（rxResource）：等第一次讀取完成再畫面。
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

describe('DatabaseDetailPageComponent', () => {
  it('renders the five detail tabs with the active tab marked for assistive tech', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/form');
    const tabs = Array.from(page().querySelectorAll('nav.tabs a'));

    expect(page().querySelector('h1')?.textContent).toContain('客戶資料庫');
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual(['表單設計', '收集紀錄', '趨勢比較', '已連接助理', '權限']);
    expect(page().querySelector('nav.tabs')?.getAttribute('aria-label')).toBe('數據庫頁籤');
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
    const detail = syncValue(repository.getDatabaseDetail('database-orders'));
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

  describe('when saving the form does not go through', () => {
    function editLabel(page: HTMLElement, value: string): void {
      const label = page.querySelector<HTMLInputElement>('#field-label-field-order-number');
      if (!label) throw new Error('missing label input');
      label.value = value;
      label.dispatchEvent(new Event('input'));
    }

    it('keeps the draft and lets the owner try again when the request fails', async () => {
      const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
      const original = repository.updateDatabaseFields.bind(repository);
      vi.spyOn(repository, 'updateDatabaseFields').mockReturnValueOnce(throwError(() => new Error('500')));

      editLabel(page(), '訂單號碼');
      button(page(), '儲存表單').click();
      harness.detectChanges();

      expect(page().querySelector('app-form-designer [role="alert"]')?.textContent).toContain('目前無法儲存表單');
      expect(page().querySelector<HTMLInputElement>('#field-label-field-order-number')?.value).toBe('訂單號碼');
      expect(button(page(), '儲存表單').disabled).toBe(false);

      vi.mocked(repository.updateDatabaseFields).mockImplementation(original);
      button(page(), '儲存表單').click();
      harness.detectChanges();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(page().querySelector('.designer-feedback')?.textContent).toContain('表單已儲存');
      expect(page().querySelector('app-form-designer [role="alert"]')).toBeNull();
    });

    it('sends one save at a time and disables the button while it is pending', async () => {
      const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
      const pending = new Subject<UpdateDatabaseFieldsResult>();
      const save = vi.spyOn(repository, 'updateDatabaseFields').mockReturnValue(pending);

      editLabel(page(), '訂單號碼');
      button(page(), '儲存表單').click();
      harness.detectChanges();
      expect(button(page(), '儲存中').disabled).toBe(true);
      page().querySelector('form.designer')?.dispatchEvent(new Event('submit'));
      harness.detectChanges();

      expect(save).toHaveBeenCalledTimes(1);
      pending.next({ status: 'validation-failed', errors: [{ fieldId: null, message: '表單至少需要一個欄位。' }], message: 'x' });
      pending.complete();
      harness.detectChanges();
      expect(button(page(), '儲存表單').disabled).toBe(false);
    });

    it('tells the owner the form changed elsewhere and reloads the latest form on request', async () => {
      const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
      const conflict: UpdateDatabaseFieldsResult = { status: 'conflict', message: '這份表單已被更新過，請重新載入後再修改。你這次的修改尚未儲存。' };
      vi.spyOn(repository, 'updateDatabaseFields').mockReturnValueOnce(of(conflict));
      const detail = vi.spyOn(repository, 'getDatabaseDetail');

      editLabel(page(), '訂單號碼');
      button(page(), '儲存表單').click();
      harness.detectChanges();

      expect(page().querySelector('app-form-designer [role="alert"]')?.textContent).toContain('已被更新過');
      expect(detail).not.toHaveBeenCalled();

      button(page(), '重新載入最新表單').click();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(detail).toHaveBeenCalledTimes(1);
      expect(page().querySelector('app-form-designer [role="alert"]')).toBeNull();
      expect(page().querySelector<HTMLInputElement>('#field-label-field-order-number')?.value).toBe('訂單編號');
    });

    it('marks the field the server rejected and keeps the other edits', async () => {
      const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
      const rejected: UpdateDatabaseFieldsResult = {
        status: 'validation-failed',
        errors: [{ fieldId: 'field-issue-type', message: '單選或多選至少需要 2 個選項。' }],
        message: '單選或多選至少需要 2 個選項。',
      };
      vi.spyOn(repository, 'updateDatabaseFields').mockReturnValueOnce(of(rejected));

      editLabel(page(), '訂單號碼');
      button(page(), '儲存表單').click();
      harness.detectChanges();

      expect(page().querySelector('#field-error-field-issue-type')?.textContent).toContain('至少需要 2 個選項');
      expect(page().querySelector('#field-error-field-order-number')).toBeNull();
      expect(page().querySelector<HTMLInputElement>('#field-label-field-order-number')?.value).toBe('訂單號碼');
    });

    it('shows a recoverable message when the trial request fails and keeps the answers', async () => {
      const { harness, page, repository } = await openDetail('/app/databases/database-orders/form');
      vi.spyOn(repository, 'previewDatabaseEntry').mockReturnValueOnce(throwError(() => new Error('500')));
      const order = page().querySelector<HTMLInputElement>('#trial-field-order-number');
      if (order) {
        order.value = 'A-1024';
        order.dispatchEvent(new Event('input'));
      }

      button(page(), '送出試填').click();
      harness.detectChanges();

      expect(page().querySelector('app-form-trial [role="alert"]')?.textContent).toContain('目前無法試填');
      expect(page().querySelector<HTMLInputElement>('#trial-field-order-number')?.value).toBe('A-1024');
      expect(button(page(), '送出試填').disabled).toBe(false);
    });
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
    expect(access.page().querySelector('app-database-access')).not.toBeNull();
  });

  it('lets the owner change the data managers and gates 收集紀錄 both ways', async () => {
    const removed = await openDetail('/app/databases/database-customer-records/access');
    removed.page().querySelector<HTMLInputElement>('#data-manager-account-smb-admin')?.click();
    removed.harness.detectChanges();
    button(removed.page(), '儲存資料管理者').click();
    removed.harness.detectChanges();

    expect(removed.page().querySelector('[aria-live="polite"]')?.textContent).toContain(
      '已更新資料管理者',
    );
    // 同一次變更立刻反映在「你的權限」上，不用重新整理。
    expect(removed.page().querySelector('.access-list')?.textContent).toContain(
      '可管理表單設定，不能查看收集紀錄',
    );

    await removed.harness.navigateByUrl('/app/databases/database-customer-records/records');
    removed.harness.detectChanges();
    expect(removed.page().textContent).toContain('無法查看收集紀錄');
    expect(removed.page().textContent).not.toContain('王小姐');

    // 重新指定：紀錄沒有被刪掉，時間軸原封不動回來。
    await removed.harness.navigateByUrl('/app/databases/database-customer-records/access');
    removed.harness.detectChanges();
    removed.page().querySelector<HTMLInputElement>('#data-manager-account-smb-admin')?.click();
    removed.harness.detectChanges();
    button(removed.page(), '儲存資料管理者').click();
    removed.harness.detectChanges();

    await removed.harness.navigateByUrl('/app/databases/database-customer-records/records');
    removed.harness.detectChanges();
    expect(removed.page().querySelectorAll('ol.timeline > li')).toHaveLength(4);
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

  it('shows the owner, template and update time in the header', async () => {
    const { page } = await openDetail('/app/databases/database-customer-records/form');
    const meta = page().querySelector('.database-meta');

    expect(meta?.querySelector('.database-owner')?.textContent).toContain('安心商行管理者');
    expect(meta?.textContent).toContain('定期回報');
    expect(meta?.textContent).toContain('6 個');
    expect(meta?.querySelector('time')?.getAttribute('datetime')).toBeTruthy();
  });

  it('shows a retryable error, not a permission state, when the detail fails to load', async () => {
    const { harness, page, repository } = await openDetail('/app/databases/database-orders/form', (repository) => {
      vi.spyOn(repository, 'getDatabaseDetail').mockReturnValueOnce(throwError(() => new Error('500')));
    });

    expect(page().textContent).toContain('目前無法載入這個資料庫');
    expect(page().textContent).not.toContain('無法查看這個資料庫');

    button(page(), '重新載入').click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(repository.getDatabaseDetail).toHaveBeenCalledTimes(2);
    expect(page().querySelector('h1')?.textContent).toContain('訂單資料庫');
  });

  describe('features the API does not have yet (API mode, #144–#148)', () => {
    /** 與 Hybrid repository 在 API 模式回傳的一樣：每一項都還沒開放。 */
    function asApiMode(repository: MockDemoRepository): void {
      const original = repository.getDatabaseDetail.bind(repository);
      vi.spyOn(repository, 'getDatabaseDetail').mockImplementation((id) =>
        original(id).pipe(
          map((result) =>
            result.status === 'ready' ? { ...result, data: { ...result.data, upcomingFeatures: API_UPCOMING_DATABASE_FEATURES } } : result,
          ),
        ),
      );
      vi.spyOn(repository, 'getDatabaseTracking');
    }

    it('still offers the form designer and the trial: form editing is available through the API', async () => {
      const { page } = await openDetail('/app/databases/database-customer-records/form', asApiMode);

      expect(page().querySelector('app-form-designer')).not.toBeNull();
      expect(page().querySelector('app-form-trial')).not.toBeNull();
      expect(page().querySelector('.upcoming-notice')).toBeNull();
    });

    it('does not read records for the trends tab: trends are #147', async () => {
      for (const tab of ['trends']) {
        TestBed.resetTestingModule();
        const { page, repository } = await openDetail(`/app/databases/database-customer-records/${tab}`, asApiMode);

        expect(page().querySelector('.upcoming-notice')?.textContent, tab).toContain('將於後續版本開放');
        expect(page().textContent, tab).not.toContain('王小姐');
        expect(page().textContent, tab).not.toContain('客服助理');
        expect(repository.getDatabaseTracking, tab).not.toHaveBeenCalled();
      }
    });

    it('lists the connected assistants the API returned (#148), not a notice', async () => {
      const { page } = await openDetail('/app/databases/database-customer-records/assistants', asApiMode);

      expect(page().querySelector('.upcoming-notice')).toBeNull();
      expect(page().textContent).toContain('客服助理');
    });

    it('keeps a failed timeline read apart from "no records" and lets it be retried', async () => {
      let calls = 0;
      const { page, repository, harness } = await openDetail('/app/databases/database-customer-records/records', (repo) => {
        const original = repo.getDatabaseTracking.bind(repo);
        asApiMode(repo);
        vi.mocked(repo.getDatabaseTracking).mockImplementation((id) =>
          ++calls === 1 ? throwError(() => new Error('503')) : original(id),
        );
      });

      expect(page().textContent).toContain('目前無法載入收集紀錄');
      expect(page().textContent).not.toContain('還沒有收集紀錄');

      button(page(), '重新載入').click();
      await harness.fixture.whenStable();
      harness.detectChanges();

      expect(repository.getDatabaseTracking).toHaveBeenCalledTimes(2);
      expect(page().querySelector('app-records-table')).not.toBeNull();
    });

    it('reads the timeline through the repository on the records tab (available since #146)', async () => {
      const { page, repository } = await openDetail('/app/databases/database-customer-records/records', asApiMode);

      expect(repository.getDatabaseTracking).toHaveBeenCalledWith('database-customer-records');
      expect(page().querySelector('.upcoming-notice')).toBeNull();
      expect(page().querySelector('app-records-table')).not.toBeNull();
    });

    it('offers the permissions tab in API mode (data managers are served by the API since #144)', async () => {
      const { page } = await openDetail('/app/databases/database-customer-records/access', asApiMode);

      expect(page().querySelector('app-database-access')).not.toBeNull();
      expect(page().querySelector('.upcoming-notice')).toBeNull();
      expect(page().textContent).toContain('已指定資料管理者');
      expect(page().textContent).toContain('目前可讀紀錄');
    });
  });
});
