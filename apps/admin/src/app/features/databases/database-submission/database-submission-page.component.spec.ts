import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseSubmissionPageComponent } from './database-submission-page.component';

async function openForm(accountId: AccountId = 'account-external-customer', url = '/app/forms/database-orders') {
  const testing = provideDatabaseTesting(accountId);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([{ path: 'app/forms/:databaseId', component: DatabaseSubmissionPageComponent }]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  await settle(harness);
  return { harness, page: () => harness.routeNativeElement as HTMLElement, repository: testing.repository };
}

async function settle(harness: RouterTestingHarness): Promise<void> {
  await harness.fixture.whenStable();
  harness.detectChanges();
}

function button(page: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(page.querySelectorAll('button')).find((b) => b.textContent?.includes(text));
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

function type(page: HTMLElement, selector: string, value: string): void {
  const input = page.querySelector<HTMLInputElement>(selector);
  if (!input) throw new Error(`missing ${selector}`);
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function fillOrder(page: HTMLElement): void {
  type(page, '#chat-field-field-order-number', 'DEMO-2001');
  page.querySelector<HTMLInputElement>('input[name="chat-field-field-issue-type"][value="配送延遲"]')?.click();
  type(page, '#chat-field-field-reported-on', '2026-09-21');
}

async function reachConsent(harness: RouterTestingHarness, page: () => HTMLElement): Promise<void> {
  fillOrder(page());
  button(page(), '下一步：確認同意').click();
  await settle(harness);
}

function agree(page: HTMLElement): void {
  page.querySelector<HTMLInputElement>('#consent-agree')?.click();
}

function recordCount(repository: MockDemoRepository): number {
  const tracking = repository.getDatabaseTracking('account-smb-admin', 'database-orders');
  return tracking.status === 'ready' ? tracking.data.subjects.reduce((sum, subject) => sum + subject.records.length, 0) : -1;
}

describe('DatabaseSubmissionPageComponent', () => {
  it('shows recipient, purpose, actual readers and the sensitive-data notice before anything is filled in', async () => {
    const { page } = await openForm();

    expect(page().querySelector('h1')?.textContent).toContain('訂單資料庫');
    const intro = page().querySelector('.intro')?.textContent ?? '';
    expect(intro).toContain('安心商行（訂單資料庫）');
    expect(intro).toContain('收集訂單問題回報');
    expect(intro).toContain('安心商行管理者');
    expect(page().querySelector('.sensitive-notice')?.textContent).toContain('敏感');
  });

  it('tells an account without the permission that it cannot fill the form, without naming it', async () => {
    const { page } = await openForm('account-smb-admin');

    expect(page().textContent).toContain('無法填寫這份表單');
    expect(page().textContent).not.toContain('訂單資料庫');
  });

  it('marks invalid fields, then asks for explicit consent and shows the receipt after submitting once', async () => {
    const { harness, page, repository } = await openForm();
    const before = recordCount(repository);

    button(page(), '下一步：確認同意').click();
    await settle(harness);
    expect(page().querySelector('app-inline-form [role="alert"]')?.textContent).toContain('「訂單編號」為必填。');
    expect(page().querySelector('#chat-field-field-order-number')?.getAttribute('aria-invalid')).toBe('true');
    expect(page().querySelector('app-consent-confirmation')).toBeNull();

    await reachConsent(harness, page);
    const consent = page().querySelector('app-consent-confirmation');
    expect(consent?.textContent).toContain('安心商行（訂單資料庫）');
    expect(consent?.textContent).toContain('DEMO-2001');
    expect(button(page(), '同意並送出').disabled).toBe(true);
    expect(recordCount(repository)).toBe(before);

    agree(page());
    harness.detectChanges();
    button(page(), '同意並送出').click();
    await settle(harness);

    const receipt = page().querySelector('[data-kind="submission-receipt"]');
    expect(receipt?.textContent).toContain('已送出');
    expect(receipt?.querySelector('.receipt-number')?.textContent).toMatch(/^R-\d{8}-\d{10}$/);
    expect(receipt?.textContent).toContain('DEMO-2001');
    expect(receipt?.textContent).toContain('第 1 版・表單連結');
    expect(recordCount(repository)).toBe(before + 1);
    expect(TestBed.inject(Router).url).toMatch(/\?receipt=submission-form-1$/);
  });

  it('keeps the answers after a failed send and retries with the same submission key, recording once', async () => {
    const { harness, page, repository } = await openForm();
    const before = recordCount(repository);
    const original = repository.submitDatabaseEntry.bind(repository);
    const submit = vi
      .spyOn(repository, 'submitDatabaseEntry')
      .mockReturnValueOnce(throwError(() => new Error('connection lost')));

    await reachConsent(harness, page);
    agree(page());
    harness.detectChanges();
    button(page(), '同意並送出').click();
    await settle(harness);

    expect(page().querySelector('app-consent-confirmation [role="alert"]')?.textContent).toContain('不會重複建立紀錄');
    expect(recordCount(repository)).toBe(before);

    submit.mockImplementation(original);
    button(page(), '同意並送出').click();
    await settle(harness);

    expect(page().querySelector('[data-kind="submission-receipt"]')).not.toBeNull();
    expect(submit).toHaveBeenCalledTimes(2);
    expect(submit.mock.calls[1][1].submissionId).toBe(submit.mock.calls[0][1].submissionId);
    expect(recordCount(repository)).toBe(before + 1);
  });

  it('asks to reload when the form changed since it was loaded, and nothing is sent', async () => {
    const { harness, page, repository } = await openForm();
    vi.spyOn(repository, 'reviewDatabaseSubmission').mockReturnValueOnce(
      of({ status: 'conflict', reason: 'form-version-changed', message: '這份表單已更新，請重新載入最新的表單後再填寫。' }),
    );

    await reachConsent(harness, page);

    expect(page().querySelector('.conflict[role="alert"]')?.textContent).toContain('重新載入');
    expect(page().querySelector('app-consent-confirmation')).toBeNull();
    button(page(), '重新載入最新表單').click();
    await settle(harness);
    expect(page().querySelector('.conflict')).toBeNull();
  });

  it('reopens the submitter’s receipt from the address', async () => {
    const first = await openForm();
    await reachConsent(first.harness, first.page);
    agree(first.page());
    first.harness.detectChanges();
    button(first.page(), '同意並送出').click();
    await settle(first.harness);
    const url = TestBed.inject(Router).url;

    // Leaving the receipt starts a new, empty fill.
    await first.harness.navigateByUrl('/app/forms/database-orders');
    await settle(first.harness);
    expect(first.page().querySelector('[data-kind="submission-receipt"]')).toBeNull();
    expect(first.page().querySelector<HTMLInputElement>('#chat-field-field-order-number')?.value).toBe('');

    await first.harness.navigateByUrl(url);
    await settle(first.harness);

    expect(first.page().querySelector('[data-kind="submission-receipt"]')?.textContent).toContain('DEMO-2001');
  });
});
