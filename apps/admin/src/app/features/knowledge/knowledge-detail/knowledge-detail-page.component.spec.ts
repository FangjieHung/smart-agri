import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  DEMO_PROCESSING_STEP_MS,
  KnowledgeDetailPageComponent,
} from './knowledge-detail-page.component';
import { provideKnowledgeTesting } from '../knowledge.testing';

async function openDetail(url: string) {
  const testing = provideKnowledgeTesting();
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([
        { path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent },
      ]),
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

function rowFor(page: HTMLElement, name: string): HTMLElement | undefined {
  return Array.from(page.querySelectorAll<HTMLElement>('.document-list > li')).find((row) =>
    row.textContent?.includes(name),
  );
}

describe('KnowledgeDetailPageComponent', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders the three detail tabs with the active tab marked for assistive tech', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    const tabs = Array.from(page().querySelectorAll('nav.tabs a'));

    expect(page().querySelector('h1')?.textContent).toContain('商品使用指南');
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual(['內容', '已連接助理', '分享權限']);
    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('內容');
    expect(page().querySelector('nav.tabs')?.getAttribute('aria-label')).toBe('知識庫頁籤');
  });

  it('keeps usable documents available while one item fails, and says so', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/content');

    expect(page().querySelector('.attention-summary')?.textContent).toContain('其餘 4 項仍可供助理使用');
    const failed = page().querySelector('.document-status[data-status="failed"]');
    expect(failed?.closest('li')?.querySelector('button')?.textContent).toContain('重新處理');
    expect(page().querySelectorAll('.document-status[data-status="ready"]').length).toBe(4);
  });

  it('labels the add action as a demo and steps the new item to 可使用 without uploading', async () => {
    const { harness, page } = await openDetail('/app/knowledge/knowledge-refund-policy/content');

    expect(page().textContent).toContain('Demo：不會真正上傳檔案');
    expect(page().querySelector('input[type="file"]')).toBeNull();

    vi.useFakeTimers();
    const add = Array.from(page().querySelectorAll('button')).find((button) =>
      button.textContent?.includes('加入示範文件'),
    );
    add?.click();
    harness.detectChanges();
    const name = page().querySelector('.document-list > li:last-child .document-name')?.textContent?.trim() ?? '';
    expect(rowFor(page(), name)?.querySelector('.document-status')?.getAttribute('data-status')).toBe('queued');

    vi.advanceTimersByTime(DEMO_PROCESSING_STEP_MS);
    harness.detectChanges();
    expect(rowFor(page(), name)?.querySelector('.document-status')?.getAttribute('data-status')).toBe('processing');

    vi.advanceTimersByTime(DEMO_PROCESSING_STEP_MS);
    harness.detectChanges();
    expect(rowFor(page(), name)?.querySelector('.document-status')?.getAttribute('data-status')).toBe('ready');
  });

  it('retries a failed item through the simulated processing states', async () => {
    const { harness, page } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    vi.useFakeTimers();
    const failedRow = page().querySelector('.document-status[data-status="failed"]')?.closest('li');
    const name = failedRow?.querySelector('.document-name')?.textContent?.trim() ?? '';
    failedRow?.querySelector('button')?.click();
    harness.detectChanges();

    expect(rowFor(page(), name)?.querySelector('.document-status')?.getAttribute('data-status')).toBe('queued');
    vi.advanceTimersByTime(DEMO_PROCESSING_STEP_MS * 2);
    harness.detectChanges();
    expect(rowFor(page(), name)?.querySelector('.document-status')?.getAttribute('data-status')).toBe('ready');
  });

  it('lists every connected assistant on the 已連接助理 tab', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/assistants');
    const items = Array.from(page().querySelectorAll('.assistant-list li')).map((li) => li.textContent ?? '');

    expect(items).toHaveLength(2);
    expect(items[0]).toContain('客服助理');
    expect(items[1]).toContain('內部教育訓練助理');
  });

  it('saves sharing changes from the 分享權限 tab and confirms them', async () => {
    const { harness, page, repository } = await openDetail('/app/knowledge/knowledge-refund-policy/sharing');

    page().querySelector<HTMLInputElement>('input[value="public"]')?.click();
    harness.detectChanges();
    page().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    harness.detectChanges();

    expect(page().querySelector('.sharing-feedback[role="status"]')?.textContent).toContain('已儲存');
    const detail = repository.getKnowledgeBaseDetail('account-smb-admin', 'knowledge-refund-policy');
    expect(detail.status === 'ready' && detail.data.sharing.scope).toBe('public');
  });

  it('shows a generic permission state without leaking another account’s knowledge base name', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-staff-notes/content');

    expect(page().textContent).toContain('無法查看這個知識庫');
    expect(page().textContent).not.toContain('同仁個人筆記');
    expect(page().querySelector('nav.tabs')).toBeNull();
  });

  it('falls back to the content tab for an unknown tab segment', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/unknown');

    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('內容');
  });
});
