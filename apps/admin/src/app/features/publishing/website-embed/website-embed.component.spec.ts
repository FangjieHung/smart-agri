import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { vi } from 'vitest';
import type { WebsiteEmbedView, WebsitePublishFailure, WebsiteServingState } from '../../../core/domain/publishing.model';
import { providePublishingTesting, publishingOf } from '../publishing.testing';
import { WebsiteEmbedComponent, formatLastSeen } from './website-embed.component';

const SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';

type Page = ReturnType<typeof render>;

function render(assistantId = SERVICE, patch: Partial<WebsiteEmbedView> = {}) {
  const { providers, repository } = providePublishingTesting();
  TestBed.configureTestingModule({ imports: [WebsiteEmbedComponent], providers });
  const fixture = TestBed.createComponent(WebsiteEmbedComponent);
  fixture.componentRef.setInput('assistantId', assistantId);
  fixture.componentRef.setInput('view', { ...publishingOf(repository, assistantId).website, ...patch });
  const changed = vi.fn();
  fixture.componentInstance.changed.subscribe(changed);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository, changed };
}

/** 重新讀 repository 目前的官網管道（寫入之後的狀態）。 */
function websiteNow(page: Page, assistantId = SERVICE): WebsiteEmbedView {
  return publishingOf(page.repository, assistantId).website;
}

function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function field(root: ParentNode, selector: string): HTMLInputElement {
  const found = root.querySelector<HTMLInputElement>(selector);
  if (found === null) throw new Error(`${selector} not found`);
  return found;
}

function button(root: ParentNode, text: string): HTMLButtonElement {
  const found = Array.from(root.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(text));
  if (!found) throw new Error(`button ${text} not found`);
  return found;
}

function dialog(): HTMLElement {
  const found = document.querySelector<HTMLElement>('.publish-panel');
  if (found === null) throw new Error('publish dialog not open');
  return found;
}

async function openPublishDialog(page: Page): Promise<HTMLElement> {
  button(page.host, '發布官網嵌入').click();
  page.fixture.detectChanges();
  await page.fixture.whenStable();
  page.fixture.detectChanges();
  return dialog();
}

function closeDialogs(): void {
  document.querySelectorAll<HTMLButtonElement>('.publish-panel .dialog-actions button').forEach((candidate) => candidate.click());
}

beforeAll(() => {
  // jsdom 沒有 scrollIntoView。
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  closeDialogs();
  document.querySelectorAll('.cdk-overlay-container').forEach((container) => (container.innerHTML = ''));
});

describe('WebsiteEmbedComponent', () => {
  it('toggles between desktop and mobile previews with pressed-state buttons', () => {
    const { fixture, host } = render();
    const preview = () => host.querySelector('.embed-preview');

    expect(preview()?.getAttribute('data-device')).toBe('desktop');
    expect(button(host, '桌機').getAttribute('aria-pressed')).toBe('true');
    button(host, '手機').click();
    fixture.detectChanges();

    expect(preview()?.getAttribute('data-device')).toBe('mobile');
    expect(button(host, '手機').getAttribute('aria-pressed')).toBe('true');
    expect(preview()?.textContent).toContain('安心商行線上客服');
  });

  it('validates a new allowed domain before adding it to the list', () => {
    const { fixture, host } = render();
    const input = host.querySelector<HTMLInputElement>('#website-domain-input');
    if (!input) throw new Error('domain input missing');

    type(input, 'https://shop.example.com/about');
    button(host, '加入網域').click();
    fixture.detectChanges();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector('#website-domain-error')?.textContent).toContain('不要包含 https://');

    type(input, 'blog.anxin-demo.example');
    button(host, '加入網域').click();
    fixture.detectChanges();
    const domains = Array.from(host.querySelectorAll('.domain-list li'), (item) => item.textContent ?? '');
    expect(domains.some((text) => text.includes('blog.anxin-demo.example'))).toBe(true);
    expect(input.getAttribute('aria-invalid')).toBeNull();
  });

  it('shows an error summary when required settings are missing on save', () => {
    const { fixture, host } = render();
    const name = host.querySelector<HTMLInputElement>('#website-display-name');
    if (!name) throw new Error('name input missing');
    type(name, '');
    button(host, '儲存官網設定').click();
    fixture.detectChanges();

    const summary = host.querySelector('.error-summary[role="alert"]');
    expect(summary?.textContent).toContain('顯示名稱');
    expect(summary?.querySelector('a')?.getAttribute('href')).toBe('#website-display-name');
    expect(name.getAttribute('aria-invalid')).toBe('true');
  });

  it('saves with the revision it read, announces it and asks the parent to reload', () => {
    const page = render();
    const spy = vi.spyOn(page.repository, 'updateWebsiteEmbed');
    const revision = page.fixture.componentInstance.view().revision;
    type(field(page.host, '#website-display-name'), '新的名稱');
    button(page.host, '儲存官網設定').click();
    page.fixture.detectChanges();

    expect(spy).toHaveBeenCalledWith(SERVICE, expect.objectContaining({ displayName: '新的名稱' }), revision);
    expect(page.host.querySelector('.save-status')?.textContent).toContain('已儲存官網設定');
    expect(page.changed).toHaveBeenCalledTimes(1);
  });

  it('asks to reload when the settings were changed elsewhere (409), without saving', () => {
    const page = render();
    vi.spyOn(page.repository, 'updateWebsiteEmbed').mockReturnValue(
      of({ status: 'conflict' as const, message: '官網嵌入的設定已在其他分頁被更新過，請重新載入後再修改。' }),
    );
    button(page.host, '儲存官網設定').click();
    page.fixture.detectChanges();

    const alert = page.host.querySelector('.error-summary[role="alert"]');
    expect(alert?.textContent).toContain('已在其他分頁被更新過');
    expect(page.changed).not.toHaveBeenCalled();
    button(alert as HTMLElement, '重新載入最新設定').click();
    page.fixture.detectChanges();

    expect(page.changed).toHaveBeenCalledTimes(1);
    expect(page.host.querySelector('.error-summary')).toBeNull();
  });

  describe('serving state', () => {
    const detailOf = (servingState: WebsiteServingState): string =>
      ({
        'not-published': '尚未發布：設定已儲存，驗收通過後即可發布。',
        paused: '已暫停：官網訪客目前看到「暫停服務」，設定會保留。',
        'suspended-acceptance': '驗收未通過，已自動暫停對外回覆；請到題組頁處理，重跑通過後會自動恢復。',
        'suspended-knowledge': '連接了不是助理擁有者自己的知識庫，已自動暫停對外回覆；解除連接這些知識庫後會自動恢復。',
        'suspended-quota': '本月用量已達上限，已暫停對外回覆；下個月或調高上限後會自動恢復。',
        serving: '已發布，官網訪客可以使用。',
      })[servingState];

    it.each<[WebsiteServingState, 'testing' | 'needs-attention' | 'paused' | 'published', string, string]>([
      ['not-published', 'testing', '測試中', '尚未發布'],
      ['paused', 'paused', '已暫停', '擁有者暫停中'],
      ['suspended-acceptance', 'needs-attention', '需要處理', '自動暫停：驗收未通過'],
      ['suspended-knowledge', 'needs-attention', '需要處理', '自動暫停：連接了別人的知識庫'],
      ['suspended-quota', 'needs-attention', '需要處理', '自動暫停：本月用量已達上限'],
      ['serving', 'published', '已發布', '服務中'],
    ])('shows %s as the %s card status with its reason', (servingState, status, label, reason) => {
      const base = publishingOf(providePublishingTesting().repository, SERVICE).website;
      const { host } = render(SERVICE, {
        servingState,
        channel: { ...base.channel, status, statusDetail: detailOf(servingState) },
        nonOwnedKnowledgeBases: servingState === 'suspended-knowledge' ? [{ id: 'kb-1', name: '同仁個人筆記' }] : [],
      });

      const panel = host.querySelector('[aria-labelledby="website-status-title"]');
      expect(panel?.getAttribute('data-serving')).toBe(servingState);
      expect(panel?.querySelector('.serving__label')?.textContent).toBe(label);
      expect(panel?.querySelector('.serving__reason')?.textContent).toBe(reason);
      expect(panel?.querySelector('.serving__detail')?.textContent).toBe(detailOf(servingState));
    });

    it('links a failed acceptance to the assistant’s test-set tab', () => {
      const { host } = render(SERVICE, { servingState: 'suspended-acceptance' });

      const link = host.querySelector('.block--status a.next-step');
      expect(link?.getAttribute('href')).toBe(`/app/assistants/${SERVICE}/acceptance`);
    });

    it('names each knowledge base that is not the owner’s while suspended for it', () => {
      const { host } = render(SERVICE, {
        servingState: 'suspended-knowledge',
        nonOwnedKnowledgeBases: [
          { id: 'kb-1', name: '同仁個人筆記' },
          { id: 'kb-2', name: '別組的常見問題' },
        ],
      });

      const names = Array.from(host.querySelectorAll('.blocked li'), (item) => item.textContent);
      expect(names).toEqual(['同仁個人筆記', '別組的常見問題']);
      expect(host.querySelector('.blocked a')?.getAttribute('href')).toBe(`/app/assistants/${SERVICE}/data-sources`);
    });

    it('offers publish for a draft, and pause plus unpublish once published', () => {
      const draft = render(ONBOARDING);
      expect(button(draft.host, '發布官網嵌入')).toBeTruthy();
      expect(() => button(draft.host, '暫停服務')).toThrow();
      TestBed.resetTestingModule();

      const published = render();
      expect(button(published.host, '暫停服務')).toBeTruthy();
      expect(button(published.host, '取消發布')).toBeTruthy();
      expect(() => button(published.host, '發布官網嵌入')).toThrow();
    });

    it('pauses, resumes and unpublishes with an announced result and a reload of the parent', () => {
      const page = render();

      button(page.host, '暫停服務').click();
      page.fixture.detectChanges();
      expect(websiteNow(page).state).toBe('paused');
      expect(page.host.querySelector('.action-status')?.textContent).toContain('已暫停官網嵌入');

      page.fixture.componentRef.setInput('view', websiteNow(page));
      page.fixture.detectChanges();
      button(page.host, '恢復服務').click();
      page.fixture.detectChanges();
      expect(websiteNow(page).state).toBe('published');
      expect(page.host.querySelector('.action-status')?.textContent).toContain('已恢復官網嵌入');

      button(page.host, '取消發布').click();
      page.fixture.detectChanges();
      expect(websiteNow(page).state).toBe('draft');
      expect(page.host.querySelector('.action-status')?.textContent).toContain('已取消發布');
      expect(page.changed).toHaveBeenCalledTimes(3);
    });
  });

  describe('publishing', () => {
    it('confirms first: lists the knowledge bases visitors will be able to query, only the owner’s own', async () => {
      const page = render(SERVICE, { state: 'draft', servingState: 'not-published' });

      const panel = await openPublishDialog(page);

      const names = Array.from(panel.querySelectorAll('ul[aria-label="訪客可以檢索的知識庫"] li'), (item) => item.textContent);
      expect(names).toEqual(['商品使用指南', '退換貨政策', '配送常見問題']);
      expect(panel.textContent).toContain('不能查資料庫或填表單');
      expect(panel.textContent).toContain('找不到資料時的回覆');
      expect(panel.textContent).toContain('shop.anxin-demo.example');
      expect(document.querySelector('[role="alertdialog"]')?.getAttribute('aria-labelledby')).toBe('website-publish-title');
    });

    it('explains that publishing is blocked, and offers no confirm button, while someone else’s knowledge base is connected', async () => {
      const page = render(SERVICE, {
        state: 'draft',
        servingState: 'not-published',
        nonOwnedKnowledgeBases: [{ id: 'kb-1', name: '同仁個人筆記' }],
      });

      const panel = await openPublishDialog(page);

      expect(panel.textContent).toContain('目前還不能發布');
      expect(panel.textContent).toContain('同仁個人筆記');
      expect(() => button(panel, '確認發布')).toThrow();
      expect(panel.querySelector('a')?.getAttribute('href')).toBe(`/app/assistants/${SERVICE}/data-sources`);
    });

    it('publishes after the confirmation, announces it and asks the parent to reload', async () => {
      const page = render();
      button(page.host, '取消發布').click();
      page.fixture.componentRef.setInput('view', websiteNow(page));
      page.fixture.detectChanges();
      page.changed.mockClear();

      const panel = await openPublishDialog(page);
      button(panel, '確認發布').click();
      page.fixture.detectChanges();

      expect(websiteNow(page).state).toBe('published');
      expect(page.host.querySelector('.action-status')?.textContent).toContain('已發布官網嵌入');
      expect(page.changed).toHaveBeenCalledTimes(1);
      await vi.waitFor(() => expect(document.querySelector('.publish-panel')).toBeNull());
    });

    it('does not publish when the dialog is cancelled', async () => {
      const page = render(SERVICE, { state: 'draft', servingState: 'not-published' });
      const spy = vi.spyOn(page.repository, 'publishWebsite');

      const panel = await openPublishDialog(page);
      button(panel, '取消').click();
      page.fixture.detectChanges();

      expect(spy).not.toHaveBeenCalled();
      await vi.waitFor(() => expect(document.querySelector('.publish-panel')).toBeNull());
    });

    it('shows every refusal reason in Chinese with a way to fix each', async () => {
      const failures: WebsitePublishFailure[] = [
        { reason: 'acceptance', message: '驗收狀態必須是「通過」才能對外發布；請先到題組頁執行驗收並讓所有題目通過。' },
        { reason: 'allowed-domains', message: '請先設定至少一個允許嵌入的網域。' },
        { reason: 'assistant-paused', message: '助理目前已暫停，請先恢復助理再發布。' },
        { reason: 'knowledge-ownership', message: '「同仁的公開知識庫」不是助理擁有者自己的知識庫，對外發布時不能使用；請解除連接後再發布。' },
        { reason: 'knowledge-ownership', message: '「同仁的筆記」不是助理擁有者自己的知識庫，對外發布時不能使用；請解除連接後再發布。' },
        { reason: 'public-base-url', message: '伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl），無法產生嵌入程式碼；請洽系統管理者。' },
      ];
      const page = render(SERVICE, { state: 'draft', servingState: 'not-published' });
      vi.spyOn(page.repository, 'publishWebsite').mockReturnValue(
        of({ status: 'publish-refused' as const, message: '目前還不能對外發布，請先處理下列項目。', failures }),
      );

      const panel = await openPublishDialog(page);
      button(panel, '確認發布').click();
      page.fixture.detectChanges();

      const alert = page.host.querySelector('.error-summary[role="alert"]') as HTMLElement;
      expect(alert.querySelector('#website-publish-failures-title')?.textContent).toBe('目前還不能對外發布，請先處理下列項目。');
      expect(Array.from(alert.querySelectorAll('li'), (item) => item.getAttribute('data-reason'))).toEqual([
        'acceptance',
        'allowed-domains',
        'assistant-paused',
        'knowledge-ownership',
        'knowledge-ownership',
        'public-base-url',
      ]);
      expect(Array.from(alert.querySelectorAll('li'), (item) => item.textContent)).toEqual(failures.map((failure) => failure.message));
      expect(alert.textContent).toContain('洽系統管理者');
      const hrefs = Array.from(alert.querySelectorAll('a.next-step'), (link) => link.getAttribute('href'));
      expect(hrefs).toEqual([`/app/assistants/${SERVICE}/acceptance`, '#website-domain-input', `/app/assistants/${SERVICE}/data-sources`]);
      expect(page.changed).not.toHaveBeenCalled();
      await vi.waitFor(() => expect(document.querySelector('.publish-panel')).toBeNull());
    });

    it('moves focus to the domain field from the “no allowed domain” refusal', async () => {
      const page = render(SERVICE, { state: 'draft', servingState: 'not-published' });
      vi.spyOn(page.repository, 'publishWebsite').mockReturnValue(
        of({
          status: 'publish-refused' as const,
          message: '目前還不能對外發布，請先處理下列項目。',
          failures: [{ reason: 'allowed-domains' as const, message: '請先設定至少一個允許嵌入的網域。' }],
        }),
      );
      document.body.appendChild(page.host);

      const panel = await openPublishDialog(page);
      button(panel, '確認發布').click();
      page.fixture.detectChanges();
      (page.host.querySelector('a.next-step') as HTMLAnchorElement).click();

      expect(document.activeElement?.id).toBe('website-domain-input');
      page.host.remove();
    });

    it('keeps the dialog open with an error when the request fails', async () => {
      const page = render(SERVICE, { state: 'draft', servingState: 'not-published' });
      vi.spyOn(page.repository, 'publishWebsite').mockReturnValue(
        new Observable((subscriber) => subscriber.error(new Error('boom'))),
      );

      const panel = await openPublishDialog(page);
      button(panel, '確認發布').click();
      page.fixture.detectChanges();

      expect(dialog().querySelector('[role="alert"]')?.textContent).toContain('目前無法發布');
    });

    it('tells the owner that visitors who find nothing see the refusal message, where the contact details belong', () => {
      const { host } = render();

      const hint = host.querySelector('#website-refusal-hint');
      expect(hint?.textContent).toContain('找不到資料時的回覆');
      expect(hint?.textContent).toContain('聯絡窗口');
      expect(hint?.querySelector('a')?.getAttribute('href')).toBe(`/app/assistants/${SERVICE}/rules`);
    });

    it('says publishing uses the saved settings while there are unsaved changes', () => {
      const { fixture, host } = render();
      expect(host.textContent).not.toContain('尚未儲存的變更');

      type(field(host, '#website-display-name'), '改到一半');
      fixture.detectChanges();

      expect(host.textContent).toContain('有尚未儲存的變更；發布使用的是已儲存的設定');
    });
  });

  describe('embed code and passive detection', () => {
    it('shows the server’s embed code and copies exactly it, with an announced result', async () => {
      const code = '<script src="https://admin.example.com/embed.js" data-assistant="abc" async></script>';
      const { fixture, host } = render(SERVICE, { embedCode: code });
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

      expect(host.querySelector('.embed-code')?.textContent).toBe(code);
      button(host, '複製嵌入碼').click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(writeText).toHaveBeenCalledWith(code);
      expect(host.querySelector('.copy-status[aria-live="polite"]')?.textContent).toContain('已複製');
    });

    it('marks the mock embed code as demo-only', () => {
      const { host } = render();

      expect(host.querySelector('.embed-code')?.textContent).toContain('不可用於正式環境');
    });

    it('says to contact the system administrator, and offers no copy button, while the server has no public address', () => {
      const { host } = render(SERVICE, { embedCode: null });

      expect(host.querySelector('.embed-code')).toBeNull();
      expect(() => button(host, '複製嵌入碼')).toThrow();
      expect(host.textContent).toContain('伺服器尚未設定對外網址');
      expect(host.textContent).toContain('洽系統管理者');
    });

    it('shows when each allowed domain was last seen, or that it was not seen yet', () => {
      const seenAt = '2026-10-05T06:03:00.000Z';
      const { host } = render(SERVICE, {
        domains: [
          { domain: 'shop.example.com', lastSeenAt: seenAt },
          { domain: 'www.example.com', lastSeenAt: null },
        ],
      });

      const rows = Array.from(host.querySelectorAll('.seen-list li'), (item) => item.textContent);
      expect(rows).toEqual([`最後一次在 shop.example.com 偵測到：${formatLastSeen(seenAt)}`, 'www.example.com：尚未偵測到']);
      expect(host.querySelector('#website-seen-title')?.textContent).toBe('安裝偵測');
      expect(host.textContent).toContain('伺服器不會連到你的網站');
      expect(() => button(host, '檢查安裝狀態')).toThrow();
    });

    it('formats the last-seen time as month/day hour:minute', () => {
      expect(formatLastSeen('2026-10-06T06:03:00.000Z')).toMatch(/^\d{2}\/\d{2} \d{2}:\d{2}$/);
    });

    it('asks to save domains first when there are none', () => {
      const { host } = render(SERVICE, { domains: [] });

      expect(host.querySelector('.seen-list')).toBeNull();
      expect(host.textContent).toContain('請先設定並儲存允許嵌入的網域');
    });
  });
});
