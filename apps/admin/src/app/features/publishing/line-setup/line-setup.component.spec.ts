import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { vi } from 'vitest';
import type { LinePublishFailure, LineSetupView, WebsiteServingState } from '../../../core/domain/publishing.model';
import {
  DEMO_LINE_CHANNEL_ID,
  DEMO_LINE_CHANNEL_SECRET,
  EXPIRED_DEMO_LINE_TOKEN,
} from '../../../core/repositories/demo-seed-publishing';
import { providePublishingTesting, publishingOf } from '../publishing.testing';
import { LineSetupComponent } from './line-setup.component';

const SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';
const VALID_TOKEN = 'demo-token-not-for-production-0123456789abcdefghij';

/**
 * `live` 為 true 時，元件送出 `changed` 就重新讀 repository 的最新狀態（像真的畫面一樣）；
 * 否則 view 固定為給的值（用來看各種狀態與原因）。
 */
function render(assistantId = SERVICE, patch: Partial<LineSetupView> = {}, live = false) {
  TestBed.resetTestingModule();
  const { providers, repository } = providePublishingTesting();
  TestBed.configureTestingModule({ imports: [LineSetupComponent], providers });
  const fixture = TestBed.createComponent(LineSetupComponent);
  const changed = vi.fn();
  const refresh = () => {
    fixture.componentRef.setInput('view', { ...publishingOf(repository, assistantId).line, ...patch });
    fixture.detectChanges();
  };
  fixture.componentRef.setInput('assistantId', assistantId);
  fixture.componentInstance.changed.subscribe(() => {
    changed();
    if (live) refresh();
  });
  refresh();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository, changed, refresh };
}

type Page = ReturnType<typeof render>;

function button(root: ParentNode, text: string): HTMLButtonElement {
  const found = Array.from(root.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(text));
  if (!found) throw new Error(`button ${text} not found`);
  return found;
}

function field(root: ParentNode, selector: string): HTMLInputElement {
  const found = root.querySelector<HTMLInputElement>(selector);
  if (found === null) throw new Error(`${selector} not found`);
  return found;
}

function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function click(page: Page, text: string): void {
  button(page.host, text).click();
  page.fixture.detectChanges();
}

function checkStates(host: HTMLElement): (string | null)[] {
  return Array.from(host.querySelectorAll('.checklist li')).map((item) => item.getAttribute('data-state'));
}

function fillNewChannel(page: Page): void {
  type(field(page.host, '#line-officialAccountId'), '@anxin-demo');
  type(field(page.host, '#line-channelId'), DEMO_LINE_CHANNEL_ID);
  type(field(page.host, '#line-channelSecret'), DEMO_LINE_CHANNEL_SECRET);
  type(field(page.host, '#line-accessToken'), VALID_TOKEN);
  page.fixture.detectChanges();
}

function refusedBy(...failures: LinePublishFailure[]) {
  return of({ status: 'publish-refused' as const, message: '目前還不能啟用 LINE 頻道，請先處理下列項目。', failures });
}

beforeAll(() => {
  Element.prototype.scrollIntoView = vi.fn();
});

describe('LineSetupComponent', () => {
  describe('write-only credentials', () => {
    it('shows 已設定・末四碼 with 更換 for a configured secret and token, and never an input, a reveal toggle or the value', () => {
      const { host } = render();

      expect(host.querySelector('#line-channelSecret-status')?.textContent?.trim()).toBe(`已設定・末四碼 ${DEMO_LINE_CHANNEL_SECRET.slice(-4)}`);
      expect(host.querySelector('#line-accessToken-status')?.textContent?.trim()).toBe(`已設定・末四碼 ${EXPIRED_DEMO_LINE_TOKEN.slice(-4)}`);
      expect(host.querySelector('#line-channelSecret')).toBeNull();
      expect(host.querySelector('#line-accessToken')).toBeNull();
      expect(button(host, '更換').getAttribute('aria-describedby')).toBe('line-channelSecret-status');
      for (const text of ['顯示', '隱藏']) {
        expect(Array.from(host.querySelectorAll('button')).some((candidate) => candidate.textContent?.includes(text))).toBe(false);
      }
      expect(host.querySelector('[aria-pressed]')).toBeNull();
      expect(host.innerHTML).not.toContain(DEMO_LINE_CHANNEL_SECRET);
      expect(host.innerHTML).not.toContain(EXPIRED_DEMO_LINE_TOKEN);
      expect(Array.from(host.querySelectorAll('input, textarea')).every((control) => !(control as HTMLInputElement).value.includes(DEMO_LINE_CHANNEL_SECRET))).toBe(true);
    });

    it('does not tie the label to a status line, only to an input', () => {
      const page = render();
      expect(page.host.querySelector('#line-channelSecret-label')?.hasAttribute('for')).toBe(false);

      click(page, '更換');
      expect(page.host.querySelector('#line-channelSecret-label')?.getAttribute('for')).toBe('line-channelSecret');
    });

    it('reveals an empty, masked, focused input on 更換, and 取消更換 puts the status back', async () => {
      const page = render();
      click(page, '更換');
      await page.fixture.whenStable();
      page.fixture.detectChanges();

      const secret = field(page.host, '#line-channelSecret');
      expect(secret.type).toBe('password');
      expect(secret.value).toBe('');
      expect(secret.getAttribute('autocomplete')).toBe('new-password');
      expect(document.activeElement).toBe(secret);
      expect(page.host.querySelector('#line-channelSecret-status')).toBeNull();
      expect(page.host.querySelector('#line-accessToken')).toBeNull();

      type(secret, 'typed-by-mistake');
      click(page, '取消更換');
      await page.fixture.whenStable();
      page.fixture.detectChanges();

      expect(page.host.querySelector('#line-channelSecret')).toBeNull();
      expect(page.host.querySelector('#line-channelSecret-status')).not.toBeNull();
      click(page, '更換');
      expect(field(page.host, '#line-channelSecret').value).toBe('');
    });

    it('sends only the credential that was replaced, then shows the new last four and keeps no typed secret anywhere', () => {
      const page = render(SERVICE, {}, true);
      const save = vi.spyOn(page.repository, 'saveLineSettings');
      page.host.querySelector<HTMLButtonElement>('#line-accessToken-replace')?.click();
      page.fixture.detectChanges();
      type(field(page.host, '#line-accessToken'), VALID_TOKEN);

      click(page, '儲存');

      expect(save).toHaveBeenCalledWith(
        SERVICE,
        {
          officialAccountId: '@anxin-demo',
          channelId: DEMO_LINE_CHANNEL_ID,
          welcomeMessage: '您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。',
          nonTextReply: '目前只能回答文字問題。',
          channelSecret: '',
          accessToken: VALID_TOKEN,
        },
        1,
      );
      expect(page.host.querySelector('#line-accessToken')).toBeNull();
      expect(page.host.querySelector('#line-accessToken-status')?.textContent?.trim()).toBe(`已設定・末四碼 ${VALID_TOKEN.slice(-4)}`);
      expect(page.host.querySelector('.feedback')?.textContent).toContain('已儲存');
      expect(page.host.innerHTML).not.toContain(VALID_TOKEN);
      expect(page.changed).toHaveBeenCalled();
      // 儲存改了 Token：舊的測試結果清掉，要重新測試。
      expect(checkStates(page.host)).toEqual(['pending', 'pending', 'pending']);
    });
  });

  describe('saving', () => {
    it('shows an error summary for every broken field of a new channel, and each link focuses its field', async () => {
      const page = render(ONBOARDING);

      click(page, '儲存');

      const summary = page.host.querySelector('.error-summary[role="alert"]');
      expect(summary?.textContent).toContain('4 個欄位需要修正');
      const links = Array.from(summary?.querySelectorAll('a') ?? []);
      expect(links.map((link) => link.getAttribute('href'))).toEqual([
        '#line-officialAccountId',
        '#line-channelId',
        '#line-channelSecret',
        '#line-accessToken',
      ]);
      expect(field(page.host, '#line-officialAccountId').getAttribute('aria-invalid')).toBe('true');
      expect(field(page.host, '#line-officialAccountId').getAttribute('aria-describedby')).toBe('line-officialAccountId-hint line-officialAccountId-error');
      links[3].click();
      expect(document.activeElement?.id).toBe('line-accessToken');
    });

    it('requires a welcome message of at most 120 characters, shown with a counter', () => {
      const page = render(ONBOARDING);
      fillNewChannel(page);
      const welcome = field(page.host, '#line-welcomeMessage');
      expect(page.host.querySelector('#line-welcomeMessage-hint')?.textContent).toContain('最多 120 字');

      type(welcome, 'x'.repeat(121));
      click(page, '儲存');

      expect(page.host.querySelector('#line-welcomeMessage-error')?.textContent).toContain('歡迎訊息請在 120 個字以內');
      expect(page.host.querySelector('.error-summary a[href="#line-welcomeMessage"]')).not.toBeNull();

      type(welcome, '歡迎光臨');
      click(page, '儲存');
      expect(page.host.querySelector('.error-summary')).toBeNull();
      expect(page.host.querySelector('.feedback')?.textContent).toContain('已儲存');
    });

    it('requires a non-text reply of at most 500 characters, shown with a counter, and keeps its line breaks and emoji (#291)', () => {
      const own = '收到您的照片了 📷\n目前只能看懂文字，\n請用文字描述問題 🙏';
      const page = render(ONBOARDING, {}, true);
      fillNewChannel(page);
      const reply = field(page.host, '#line-nonTextReply');
      expect(reply.tagName).toBe('TEXTAREA');
      expect(reply.value).toBe('目前只能回答文字問題。');
      const hint = page.host.querySelector('#line-nonTextReply-hint')?.textContent ?? '';
      expect(hint).toContain('群組與多人聊天室不回覆');
      expect(hint).toContain('最多 500 字（目前 11 字）');

      type(reply, ' \n ');
      click(page, '儲存');
      expect(page.host.querySelector('#line-nonTextReply-error')?.textContent).toContain('請填寫收到非文字訊息時的回覆');
      expect(reply.getAttribute('aria-invalid')).toBe('true');
      expect(reply.getAttribute('aria-describedby')).toBe('line-nonTextReply-hint line-nonTextReply-error');
      expect(page.host.querySelector('.error-summary a[href="#line-nonTextReply"]')?.textContent).toContain('收到非文字訊息時的回覆');

      type(reply, '收'.repeat(501));
      click(page, '儲存');
      expect(page.host.querySelector('#line-nonTextReply-error')?.textContent).toContain('收到非文字訊息時的回覆請在 500 個字以內');
      expect(page.host.querySelector('#line-nonTextReply-hint')?.textContent).toContain('目前 501 字');

      const save = vi.spyOn(page.repository, 'saveLineSettings');
      type(reply, own);
      click(page, '儲存');
      expect(save.mock.calls[0]?.[1]).toMatchObject({ nonTextReply: own });
      expect(page.host.querySelector('.error-summary')).toBeNull();
      expect(publishingOf(page.repository, ONBOARDING).line.nonTextReply).toBe(own);
      expect(field(page.host, '#line-nonTextReply').value).toBe(own);
    });

    it('answers a stale revision with a reload prompt that reloads and drops the typed secret', () => {
      const page = render();
      vi.spyOn(page.repository, 'saveLineSettings').mockReturnValue(
        of({ status: 'conflict' as const, message: 'LINE 頻道的設定已在其他分頁被更新過，請重新載入後再修改。' }),
      );
      page.host.querySelector<HTMLButtonElement>('#line-accessToken-replace')?.click();
      page.fixture.detectChanges();
      type(field(page.host, '#line-accessToken'), VALID_TOKEN);

      click(page, '儲存');

      const alert = page.host.querySelector('.error-summary[role="alert"]');
      expect(alert?.textContent).toContain('已在其他分頁被更新過');
      click(page, '重新載入最新設定');
      expect(page.changed).toHaveBeenCalledTimes(1);
      expect(page.host.querySelector('#line-accessToken')).toBeNull();
      expect(page.host.querySelector('.error-summary')).toBeNull();
    });
  });

  describe('testing the connection', () => {
    it('is disabled until settings are saved', () => {
      const { host } = render(ONBOARDING);

      expect(button(host, '測試連線').disabled).toBe(true);
      expect(host.textContent).toContain('請先儲存連接資訊');
    });

    it('shows the three checks with the server’s messages, failed and skipped states, and announces the outcome', () => {
      const page = render(SERVICE, {}, true);

      expect(checkStates(page.host)).toEqual(['failed', 'skipped', 'skipped']);
      click(page, '測試連線');

      const items = Array.from(page.host.querySelectorAll('.checklist li'));
      expect(items).toHaveLength(3);
      expect(items[0].textContent).toContain('Channel access token 與官方帳號');
      expect(items[0].textContent).toContain('未通過');
      expect(items[0].textContent).toContain('LINE 不接受這個 Channel access token');
      expect(items[1].textContent).toContain('略過');
      expect(items[1].textContent).toContain('前一項檢查未通過，這一項沒有執行。');
      const live = Array.from(page.host.querySelectorAll('[role="status"]')).map((region) => region.textContent?.trim());
      expect(live).toContain('連線測試未通過，請依下列原因處理後再測一次。');
      expect(page.host.textContent).toContain('最近一次測試：');
    });

    it('goes pending → passed for a new channel: save, then 測試連線, then every check passes', () => {
      const page = render(ONBOARDING, {}, true);
      fillNewChannel(page);
      expect(button(page.host, '測試連線').disabled).toBe(true);

      click(page, '儲存');
      expect(page.host.querySelector('.feedback')?.textContent).toContain('下一步請按「測試連線」');
      expect(checkStates(page.host)).toEqual(['pending', 'pending', 'pending']);
      expect(page.host.textContent).toContain('尚未測試');

      click(page, '測試連線');

      expect(checkStates(page.host)).toEqual(['passed', 'passed', 'passed']);
      expect(page.host.textContent).toContain('三項檢查都通過，可以啟用。');
      expect(page.host.querySelector('.checklist__symbol')?.getAttribute('aria-hidden')).toBe('true');
    });

    it('says what is refused when the server’s own preconditions are missing (no saved settings, no public address)', () => {
      const page = render();
      vi.spyOn(page.repository, 'testLineConnection').mockReturnValue(
        of({
          status: 'test-refused' as const,
          message: '目前還不能測試連線，請先處理下列項目。',
          failures: [
            { reason: 'settings' as const, message: '請先填寫並儲存 LINE 官方帳號的連接資訊，再測試連線。' },
            { reason: 'public-base-url' as const, message: '伺服器尚未設定對外網址；請洽系統管理者。' },
          ],
        }),
      );

      click(page, '測試連線');

      const alert = page.host.querySelector('.error-summary[role="alert"]');
      expect(alert?.textContent).toContain('目前還不能測試連線');
      expect(alert?.querySelectorAll('li')).toHaveLength(2);
      expect(alert?.textContent).toContain('請洽系統管理者');
    });

    it('asks to reload when the settings changed while LINE was being called', () => {
      const page = render();
      vi.spyOn(page.repository, 'testLineConnection').mockReturnValue(
        of({ status: 'conflict' as const, message: '測試連線期間 LINE 頻道的設定已在其他分頁被更新過，請重新載入後再測試一次。' }),
      );

      click(page, '測試連線');

      expect(page.host.querySelector('.error-summary[role="alert"]')?.textContent).toContain('測試連線期間');
      expect(button(page.host, '重新載入最新設定')).toBeTruthy();
    });

    it('does not lose its place when the request fails', () => {
      const page = render();
      vi.spyOn(page.repository, 'testLineConnection').mockReturnValue(new Observable((subscriber) => subscriber.error(new Error('boom'))));

      click(page, '測試連線');

      expect(page.host.textContent).toContain('目前無法測試連線，請稍後再試。');
      expect(button(page.host, '測試連線').disabled).toBe(false);
    });

    it('notes that the test uses the saved settings while there are unsaved changes', () => {
      const page = render();
      type(field(page.host, '#line-channelId'), '1650000009');
      page.fixture.detectChanges();

      expect(page.host.textContent).toContain('測試連線使用的是已儲存的設定');
    });
  });

  describe('webhook URL', () => {
    it('shows the real URL read-only with a copy button, and the steps no longer ask to paste it', async () => {
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
      const page = render(SERVICE, { webhookUrl: 'http://localhost:5153/api/v1/line/webhook/abc' });

      expect(page.host.querySelector('#line-webhook-url')?.textContent).toBe('http://localhost:5153/api/v1/line/webhook/abc');
      expect(page.host.querySelector('#line-webhook-url')?.tagName).toBe('CODE');
      expect(page.host.textContent).toContain('不需要貼到任何地方');
      expect(page.host.querySelector('.guide')?.textContent).not.toContain('貼回');
      expect(page.host.querySelector('.guide')?.textContent).toContain('不需要自己貼網址');

      click(page, '複製');
      await page.fixture.whenStable();
      page.fixture.detectChanges();

      expect(writeText).toHaveBeenCalledWith('http://localhost:5153/api/v1/line/webhook/abc');
      expect(page.host.textContent).toContain('已複製 Webhook 網址');
    });

    it('explains a server without a public address instead of showing a URL', () => {
      const { host } = render(SERVICE, { webhookUrl: null });

      expect(host.querySelector('#line-webhook-url')).toBeNull();
      const live = Array.from(host.querySelectorAll('[role="status"]')).map((region) => region.textContent);
      expect(live.join(' ')).toContain('請洽系統管理者');
    });
  });

  describe('enabling and its refusals', () => {
    it('shows 啟用 only for a draft, and enables a tested channel whose gate passes', () => {
      const page = render(ONBOARDING, {}, true);
      expect(button(page.host, '啟用')).toBeTruthy();
      expect(Array.from(page.host.querySelectorAll('button')).some((candidate) => candidate.textContent?.includes('暫停服務'))).toBe(false);

      vi.spyOn(page.repository, 'publishLine').mockReturnValue(
        of({ status: 'ready' as const, data: { ...publishingOf(page.repository, ONBOARDING).line, state: 'published' as const, servingState: 'serving' as const } }),
      );
      click(page, '啟用');

      expect(page.host.textContent).toContain('已啟用 LINE 頻道');
      expect(page.changed).toHaveBeenCalled();
    });

    it('lists the unmet gate (never tested, acceptance not passed) with a link to the test-set tab and one to the test button', () => {
      const page = render(ONBOARDING, {}, true);
      fillNewChannel(page);
      click(page, '儲存');

      click(page, '啟用');

      const summary = page.host.querySelector('.error-summary[role="alert"][aria-labelledby="line-publish-failures-title"]');
      expect(summary?.querySelector('#line-publish-failures-title')?.textContent).toContain('目前還不能啟用 LINE 頻道');
      expect(Array.from(summary?.querySelectorAll('li') ?? []).map((item) => item.getAttribute('data-reason'))).toEqual(['connection', 'acceptance']);
      expect(summary?.querySelector('a[href="/app/assistants/assistant-internal-onboarding/acceptance"]')?.textContent).toContain('前往驗收題組頁');
      const toTest = summary?.querySelector<HTMLAnchorElement>('a[href="#line-test"]');
      expect(toTest).not.toBeNull();
      toTest?.click();
      expect(document.activeElement?.id).toBe('line-test');
      expect(page.host.querySelector('.feedback[role="status"]')).not.toBeNull();
    });

    it('shows every reason of one refusal: connection, acceptance, paused assistant, each knowledge base, and the missing public address', () => {
      // 已啟用的頻道沒有「啟用」按鈕，所以用草稿的 view 來按。
      const draft = render(SERVICE, { state: 'draft' });
      vi.spyOn(draft.repository, 'publishLine').mockReturnValue(
        refusedBy(
          { reason: 'connection', message: '請先測試連線，三項檢查都通過後才能啟用。' },
          { reason: 'acceptance', message: '驗收狀態必須是「通過」才能對外發布。' },
          { reason: 'assistant-paused', message: '助理目前已暫停，請先恢復助理再發布。' },
          { reason: 'knowledge-ownership', message: '「同仁的筆記」不是助理擁有者自己的知識庫。' },
          { reason: 'knowledge-ownership', message: '「同仁的手冊」不是助理擁有者自己的知識庫。' },
          { reason: 'public-base-url', message: '伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl）。' },
          { reason: 'other', message: '日後新增的原因' },
        ),
      );

      click(draft, '啟用');

      const items = Array.from(draft.host.querySelectorAll('.error-summary li'));
      expect(items.map((item) => item.getAttribute('data-reason'))).toEqual([
        'connection',
        'acceptance',
        'assistant-paused',
        'knowledge-ownership',
        'knowledge-ownership',
        'public-base-url',
        'other',
      ]);
      expect(items[3].textContent).toContain('同仁的筆記');
      expect(items[4].textContent).toContain('同仁的手冊');
      expect(draft.host.querySelector('.error-summary a[href="/app/assistants/assistant-customer-service/data-sources"]')).not.toBeNull();
      expect(draft.host.querySelector('.error-summary')?.textContent).toContain('這是伺服器的設定，請洽系統管理者');
    });

    it('does not enable twice while a request is in flight', () => {
      const page = render(SERVICE, { state: 'draft' });
      const publish = vi.spyOn(page.repository, 'publishLine').mockReturnValue(new Observable(() => undefined));

      click(page, '啟用');
      click(page, '啟用');

      expect(publish).toHaveBeenCalledTimes(1);
      expect(button(page.host, '啟用').disabled).toBe(true);
    });
  });

  describe('serving state and its reason', () => {
    it.each<[WebsiteServingState, Partial<LineSetupView>, string, string[]]>([
      ['not-published', { state: 'draft' }, '尚未啟用', ['啟用']],
      ['serving', { state: 'published' }, '服務中', ['暫停服務', '取消啟用']],
      ['paused', { state: 'paused' }, '擁有者暫停中', ['恢復服務', '取消啟用']],
      ['suspended-acceptance', { state: 'published' }, '自動暫停：驗收未通過', ['暫停服務', '取消啟用']],
      ['suspended-knowledge', { state: 'published' }, '自動暫停：連接了別人的知識庫', ['暫停服務', '取消啟用']],
      ['suspended-quota', { state: 'published' }, '自動暫停：本月用量已達上限', ['暫停服務', '取消啟用']],
    ])('shows %s with its label and the matching actions', (servingState, patch, label, actions) => {
      const { host } = render(SERVICE, { servingState, ...patch });

      expect(host.querySelector('.serving__reason')?.textContent).toBe(label);
      expect(host.querySelector('.block--status')?.getAttribute('data-serving')).toBe(servingState);
      const labels = Array.from(host.querySelectorAll('.block--status .actions button')).map((candidate) => candidate.textContent?.trim());
      expect(labels).toEqual(actions);
    });

    it('shows the card status and the server’s reason sentence next to the serving label', () => {
      const base = publishingOf(providePublishingTesting().repository, SERVICE).line;
      const { host } = render(SERVICE, {
        servingState: 'suspended-quota',
        state: 'published',
        channel: { ...base.channel, status: 'needs-attention', statusDetail: '本月用量已達上限，已暫停 LINE 回覆；下個月或調高上限後會自動恢復。' },
      });

      expect(host.querySelector('.serving__label')?.textContent).toBe('需要處理');
      expect(host.querySelector('.serving__detail')?.textContent).toContain('本月用量已達上限');
    });

    it('links a failed acceptance to the test-set tab', () => {
      const { host } = render(SERVICE, { servingState: 'suspended-acceptance', state: 'published', acceptanceStatus: 'failed' });

      expect(host.querySelector('a[href="/app/assistants/assistant-customer-service/acceptance"]')?.textContent).toContain('前往驗收題組頁處理');
    });

    it('lists the knowledge bases that are not the owner’s with a link to the data-source tab', () => {
      const { host } = render(SERVICE, {
        servingState: 'suspended-knowledge',
        state: 'published',
        nonOwnedKnowledgeBases: [{ id: 'kb-1', name: '同仁的公開知識庫' }],
      });

      const list = host.querySelector('.blocked ul[aria-label="不是擁有者自己的知識庫"]');
      expect(list?.textContent).toContain('同仁的公開知識庫');
      expect(host.querySelector('.blocked a[href="/app/assistants/assistant-customer-service/data-sources"]')).not.toBeNull();
    });
  });

  describe('pause, resume and unpublish', () => {
    it('pauses through setPublishingChannelPaused(…, line, true), announces it, and resumes the other way', () => {
      const page = render(SERVICE, { state: 'published', servingState: 'serving' });
      const pause = vi.spyOn(page.repository, 'setPublishingChannelPaused');

      click(page, '暫停服務');

      expect(pause).toHaveBeenCalledWith(SERVICE, 'line', true);
      expect(page.host.textContent).toContain('已暫停 LINE');
      expect(page.changed).toHaveBeenCalled();

      const resumed = render(SERVICE, { state: 'paused', servingState: 'paused' });
      const resume = vi.spyOn(resumed.repository, 'setPublishingChannelPaused');
      click(resumed, '恢復服務');
      expect(resume).toHaveBeenCalledWith(SERVICE, 'line', false);
      expect(resumed.host.textContent).toContain('已恢復 LINE');
    });

    it('unpublishes with 取消啟用 and says the settings and credentials are kept', () => {
      const page = render(SERVICE, { state: 'published', servingState: 'serving' });
      const unpublish = vi.spyOn(page.repository, 'unpublishLine');

      click(page, '取消啟用');

      expect(unpublish).toHaveBeenCalledWith(SERVICE);
      expect(page.host.textContent).toContain('設定與憑證都保留');
      expect(page.changed).toHaveBeenCalled();
    });

    it('shows a failure in an alert and reloads when the request fails', () => {
      const page = render(SERVICE, { state: 'published', servingState: 'serving' });
      vi.spyOn(page.repository, 'setPublishingChannelPaused').mockReturnValue(new Observable((subscriber) => subscriber.error(new Error('boom'))));

      click(page, '暫停服務');

      expect(page.host.querySelector('.error[role="alert"]')?.textContent).toContain('目前無法變更 LINE 的狀態');
      expect(page.changed).toHaveBeenCalled();
    });
  });

  describe('usage and LINE-side reminders', () => {
    it('shows this month’s push fallback count and what to switch in LINE Official Account Manager', () => {
      const { host } = render(SERVICE, { pushFallbackCount: 3 });

      expect(host.querySelector('.push-count')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('本月補送次數：3 次');
      const reminders = host.querySelector('.reminders')?.textContent ?? '';
      expect(reminders).toContain('開啟「Webhook」');
      expect(reminders).toContain('關閉「自動回應訊息」');
      expect(reminders).toContain('開啟「允許加入群組」');
      expect(host.textContent).toContain('群組裡逾時不補送');
    });
  });

  describe('accessibility', () => {
    it('labels every input, announces results in status regions and keeps the error summary an alert', () => {
      const page = render(ONBOARDING);
      click(page, '儲存');

      for (const control of Array.from(page.host.querySelectorAll('input, textarea'))) {
        expect(page.host.querySelector(`label[for="${control.id}"]`)).not.toBeNull();
      }
      expect(page.host.querySelectorAll('[role="status"]').length).toBeGreaterThanOrEqual(3);
      expect(page.host.querySelector('.error-summary')?.getAttribute('role')).toBe('alert');
      expect(page.host.querySelector('ol.checklist')?.getAttribute('aria-label')).toBe('連線測試的三項檢查');
    });
  });
});
