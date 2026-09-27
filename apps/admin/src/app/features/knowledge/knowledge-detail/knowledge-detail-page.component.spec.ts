import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { firstValueFrom, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import type { RetryKnowledgeDocumentResult } from '../../../core/repositories/demo-repository';
import {
  MOCK_KNOWLEDGE_PROCESSING_MS,
  MOCK_KNOWLEDGE_QUEUED_MS,
} from '../../../core/repositories/mock-demo-repository';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { provideKnowledgeTesting } from '../knowledge.testing';
import {
  KNOWLEDGE_DETAIL_POLL_MS,
  KnowledgeDetailPageComponent,
} from './knowledge-detail-page.component';

const START = new Date('2026-09-22T02:00:00.000Z');

@Component({ template: '' })
class ListStubComponent {}

async function openDetail(url: string, options: { readonly realClock?: boolean } = {}) {
  // 用 `Date.now()` 讓 mock 的處理進度跟著 `vi.advanceTimersByTime` 前進。
  const testing = provideKnowledgeTesting('account-smb-admin', options.realClock ? () => new Date() : undefined);
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      provideRouter([
        { path: 'app/knowledge', component: ListStubComponent },
        { path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  await settle(harness);
  return {
    harness,
    page: () => harness.routeNativeElement as HTMLElement,
    repository: testing.repository,
    router: TestBed.inject(Router),
  };
}

async function settle(harness: RouterTestingHarness): Promise<void> {
  harness.detectChanges();
  await harness.fixture.whenStable();
  harness.detectChanges();
}

/** 推進假時鐘（含 rxjs 的 timer），再讓 resource 與畫面更新。 */
async function advance(harness: RouterTestingHarness, ms: number): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await settle(harness);
}

function rowFor(page: HTMLElement, name: string): HTMLElement | undefined {
  return Array.from(page.querySelectorAll<HTMLElement>('.document-list > li')).find((row) =>
    row.textContent?.includes(name),
  );
}

function statusOf(page: HTMLElement, name: string): string | null | undefined {
  return rowFor(page, name)?.querySelector('.document-status')?.getAttribute('data-status');
}

function buttonIn(root: ParentNode, label: string): HTMLButtonElement | undefined {
  return Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
    button.textContent?.includes(label),
  );
}

function setVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('KnowledgeDetailPageComponent', () => {
  afterEach(() => {
    vi.useRealTimers();
    // 還原成 jsdom 原本的 getter（定義在 Document.prototype 上）。
    delete (document as { visibilityState?: unknown }).visibilityState;
  });

  it('renders the three detail tabs with the active tab marked for assistive tech', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    const tabs = Array.from(page().querySelectorAll('nav.tabs a'));

    expect(page().querySelector('h1')?.textContent).toContain('商品使用指南');
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual(['內容', '已連接助理', '分享權限']);
    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('內容');
    expect(page().querySelector('nav.tabs')?.getAttribute('aria-label')).toBe('知識庫頁籤');
  });

  it('keeps usable documents available while one item fails, and offers retry only for the failed one', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/content');

    expect(page().querySelector('.attention-summary')?.textContent).toContain('其餘 4 項仍可供助理使用');
    const failed = page().querySelector('.document-status[data-status="failed"]');
    expect(buttonIn(failed?.closest('li') as HTMLElement, '重新處理')).toBeDefined();
    const partial = page().querySelector('.document-status[data-status="partially-readable"]');
    expect(buttonIn(partial?.closest('li') as HTMLElement, '重新處理')).toBeUndefined();
    expect(page().querySelectorAll('.document-status[data-status="ready"]').length).toBe(4);
  });

  it('no longer offers a demo add button (uploading comes with batch upload)', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-refund-policy/content');

    expect(buttonIn(page(), '加入示範文件')).toBeUndefined();
    expect(page().textContent).not.toContain('Demo：不會真正上傳檔案');
  });

  describe('polling while items are queued or processing', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
      vi.setSystemTime(START);
    });

    it('retries a failed item and follows it to 可使用 by re-reading every 3 seconds', async () => {
      const { harness, page } = await openDetail('/app/knowledge/knowledge-product-guide/content', { realClock: true });
      const failedRow = page().querySelector('.document-status[data-status="failed"]')?.closest('li');
      const name = failedRow?.querySelector('.document-name')?.textContent?.trim() ?? '';

      buttonIn(failedRow as HTMLElement, '重新處理')?.click();
      await settle(harness);
      expect(statusOf(page(), name)).toBe('queued');
      expect(page().querySelector('[role="status"][aria-live="polite"]')?.textContent).toContain('等待處理');

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS);
      expect(statusOf(page(), name)).toBe('processing');
      expect(page().querySelector('[role="status"][aria-live="polite"]')?.textContent).toContain('處理中');

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS);
      expect(MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS).toBeLessThanOrEqual(2 * KNOWLEDGE_DETAIL_POLL_MS);
      expect(statusOf(page(), name)).toBe('ready');
      expect(page().querySelector('[role="status"][aria-live="polite"]')?.textContent).toContain('可使用');
    });

    it('stops polling once nothing is pending', async () => {
      const { harness, repository } = await openDetail('/app/knowledge/knowledge-refund-policy/content', {
        realClock: true,
      });
      const reads = vi.spyOn(repository, 'getKnowledgeBaseDetail');

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS * 3);

      expect(reads).not.toHaveBeenCalled();
    });

    it('pauses while the tab is hidden and catches up when it is shown again', async () => {
      const { harness, repository } = await openDetail('/app/knowledge/knowledge-shipping-faq/content', {
        realClock: true,
      });
      const reads = vi.spyOn(repository, 'getKnowledgeBaseDetail');

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS);
      expect(reads).toHaveBeenCalledTimes(1);

      setVisibility('hidden');
      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS * 4);
      expect(reads).toHaveBeenCalledTimes(1);

      setVisibility('visible');
      await settle(harness);
      expect(reads).toHaveBeenCalledTimes(2);
    });

    it('stops polling when the page is destroyed', async () => {
      const { harness, repository } = await openDetail('/app/knowledge/knowledge-shipping-faq/content', {
        realClock: true,
      });
      const reads = vi.spyOn(repository, 'getKnowledgeBaseDetail');

      harness.fixture.destroy();
      await vi.advanceTimersByTimeAsync(KNOWLEDGE_DETAIL_POLL_MS * 3);

      expect(reads).not.toHaveBeenCalled();
    });

    it('keeps showing the last result when a poll fails, and tries again next time', async () => {
      const { harness, page, repository } = await openDetail('/app/knowledge/knowledge-shipping-faq/content', {
        realClock: true,
      });
      const reads = vi
        .spyOn(repository, 'getKnowledgeBaseDetail')
        .mockReturnValueOnce(throwError(() => new Error('offline')));

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS);
      expect(page().querySelector('[data-state="error"]')).toBeNull();
      expect(page().querySelector('.document-list')).not.toBeNull();

      await advance(harness, KNOWLEDGE_DETAIL_POLL_MS);
      expect(reads).toHaveBeenCalledTimes(2);
    });
  });

  it('disables the retry button while the request is in flight', async () => {
    const { harness, page, repository } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    const response = new Subject<RetryKnowledgeDocumentResult>();
    const retry = vi.spyOn(repository, 'retryKnowledgeDocument').mockReturnValue(response);
    const failedRow = page().querySelector('.document-status[data-status="failed"]')?.closest('li') as HTMLElement;

    buttonIn(failedRow, '重新處理')?.click();
    await settle(harness);
    const button = buttonIn(page().querySelector('.document-status[data-status="failed"]')?.closest('li') as HTMLElement, '重新處理');
    expect(button?.disabled).toBe(true);
    button?.click();
    expect(retry).toHaveBeenCalledTimes(1);
    expect(retry).toHaveBeenCalledWith('knowledge-product-guide', 'document-guide-locked', 'document-guide-locked:v1');

    response.next({ status: 'validation-failed', message: '只有處理失敗的版本可以重試。' });
    response.complete();
    await settle(harness);
    expect(page().querySelector('.action-error[role="alert"]')?.textContent).toContain('只有處理失敗的版本可以重試。');
  });

  it('says so when a retry cannot be sent, instead of failing silently', async () => {
    const { harness, page, repository } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    vi.spyOn(repository, 'retryKnowledgeDocument').mockReturnValue(throwError(() => new Error('offline')));

    buttonIn(page().querySelector('.document-status[data-status="failed"]')?.closest('li') as HTMLElement, '重新處理')?.click();
    await settle(harness);

    expect(page().querySelector('.action-error[role="alert"]')?.textContent).toContain('目前無法重新處理');
  });

  it('lists every connected assistant on the 已連接助理 tab', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/assistants');
    const items = Array.from(page().querySelectorAll('.assistant-list li')).map((li) => li.textContent ?? '');

    expect(items).toHaveLength(2);
    expect(items[0]).toContain('客服助理');
    expect(items[1]).toContain('內部教育訓練助理');
    expect(page().textContent).not.toContain('助理設定仍為示範資料');
  });

  /**
   * 對應 issue #49：助理在 M3 之前仍是 mock 資料，API 模式下「已連接助理」這份清單是
   * 由 mock 助理推算出來的，不是後端的真實紀錄，畫面要加註提醒，避免誤以為是真實資料。
   */
  it('notes that the assistant list is still demo data in API mode', async () => {
    const testing = provideKnowledgeTesting('account-smb-admin');
    TestBed.configureTestingModule({
      providers: [
        ...testing.providers,
        { provide: ApiSessionService, useValue: { apiMode: true } },
        provideRouter([
          { path: 'app/knowledge', component: ListStubComponent },
          { path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent },
        ]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/knowledge/knowledge-product-guide/assistants');
    await settle(harness);

    expect(harness.routeNativeElement?.textContent).toContain('助理設定仍為示範資料');
  });

  it('saves sharing changes from the 分享權限 tab and confirms them', async () => {
    const { harness, page, repository } = await openDetail('/app/knowledge/knowledge-refund-policy/sharing');

    page().querySelector<HTMLInputElement>('input[value="public"]')?.click();
    harness.detectChanges();
    page().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await settle(harness);

    expect(page().querySelector('.sharing-feedback[role="status"]')?.textContent).toContain('已儲存');
    expect(page().querySelector('.hint')?.textContent).toContain('公開分享');
    const detail = await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-refund-policy'));
    expect(detail.status === 'ready' && detail.data.sharing.scope).toBe('public');
  });

  it('deletes a document after confirmation and leaves the rest', async () => {
    const { harness, page } = await openDetail('/app/knowledge/knowledge-product-guide/content');
    const before = page().querySelectorAll('.document-list > li').length;

    buttonIn(rowFor(page(), '舊版說明書掃描檔.pdf') as HTMLElement, '刪除')?.click();
    await settle(harness);
    const dialog = document.querySelector('.delete-panel') as HTMLElement;
    expect(dialog.textContent).toContain('刪除「舊版說明書掃描檔.pdf」？');
    expect(dialog.textContent).toContain('無法復原');

    buttonIn(dialog, '取消')?.click();
    await settle(harness);
    expect(page().querySelectorAll('.document-list > li').length).toBe(before);

    buttonIn(rowFor(page(), '舊版說明書掃描檔.pdf') as HTMLElement, '刪除')?.click();
    await settle(harness);
    const confirm = Array.from(document.querySelectorAll<HTMLButtonElement>('.delete-panel button')).find(
      (button) => button.textContent?.trim() === '刪除',
    );
    confirm?.click();
    await settle(harness);

    expect(page().querySelectorAll('.document-list > li').length).toBe(before - 1);
    expect(rowFor(page(), '舊版說明書掃描檔.pdf')).toBeUndefined();
    expect(page().querySelector('[role="status"][aria-live="polite"]')?.textContent).toContain('已刪除');
  });

  it('deletes the whole knowledge base after confirmation and returns to the list', async () => {
    const { harness, page, repository, router } = await openDetail('/app/knowledge/knowledge-refund-policy/content');

    buttonIn(page(), '刪除知識庫')?.click();
    await settle(harness);
    expect(document.querySelector('.delete-panel')?.textContent).toContain('刪除知識庫「退換貨政策」？');
    Array.from(document.querySelectorAll<HTMLButtonElement>('.delete-panel button'))
      .find((button) => button.textContent?.trim() === '刪除')
      ?.click();
    await settle(harness);

    expect(router.url).toBe('/app/knowledge');
    expect(await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-refund-policy'))).toMatchObject({
      status: 'permission-denied',
    });
  });

  describe('four separate states', () => {
    it('shows loading while the first read has not returned', async () => {
      const testing = provideKnowledgeTesting();
      testing.repository.setScenario('loading');
      TestBed.configureTestingModule({
        providers: [...testing.providers, provideRouter([{ path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent }])],
      });
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/app/knowledge/knowledge-product-guide/content');
      await settle(harness);

      expect(harness.routeNativeElement?.querySelector('[data-state="loading"]')?.textContent).toContain('正在載入知識庫');
    });

    it('shows an error — not an empty knowledge base — when the read fails', async () => {
      const testing = provideKnowledgeTesting();
      vi.spyOn(testing.repository, 'getKnowledgeBaseDetail').mockReturnValue(throwError(() => new Error('503')));
      TestBed.configureTestingModule({
        providers: [...testing.providers, provideRouter([{ path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent }])],
      });
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl('/app/knowledge/knowledge-product-guide/content');
      await settle(harness);
      const page = harness.routeNativeElement as HTMLElement;

      expect(page.querySelector('[data-state="error"]')?.textContent).toContain('目前無法載入這個知識庫');
      expect(page.querySelector('.document-list')).toBeNull();
      expect(page.textContent).not.toContain('還沒有文件');
    });

    it('shows a generic permission state without leaking another account’s knowledge base name', async () => {
      const { page } = await openDetail('/app/knowledge/knowledge-staff-notes/content');

      expect(page().textContent).toContain('無法查看這個知識庫');
      expect(page().textContent).not.toContain('同仁個人筆記');
      expect(page().querySelector('nav.tabs')).toBeNull();
    });

    it('explains a truly empty knowledge base', async () => {
      const testing = provideKnowledgeTesting();
      const created = await firstValueFrom(testing.repository.createKnowledgeBase({ name: '空的知識庫', purpose: '' }));
      if (created.status !== 'ready') throw new Error('expected ready');
      TestBed.configureTestingModule({
        providers: [...testing.providers, provideRouter([{ path: 'app/knowledge/:id/:tab', component: KnowledgeDetailPageComponent }])],
      });
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(`/app/knowledge/${created.data.id}/content`);
      await settle(harness);

      expect(harness.routeNativeElement?.querySelector('.document-list .empty')?.textContent).toContain(
        '這個知識庫還沒有文件或 FAQ。',
      );
    });
  });

  it('falls back to the content tab for an unknown tab segment', async () => {
    const { page } = await openDetail('/app/knowledge/knowledge-product-guide/unknown');

    expect(page().querySelector('nav.tabs [aria-current="page"]')?.textContent).toContain('內容');
  });
});
