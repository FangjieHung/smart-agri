import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, firstValueFrom, NEVER, throwError } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantDetailPageComponent } from './assistant-detail-page.component';

@Component({ template: 'list' })
class ListStubComponent {}

describe('AssistantDetailPageComponent', () => {
  it('provides the six detail tabs and opens the overview editor', async () => {
    const params = new BehaviorSubject(
      new Map([
        ['id', 'assistant-customer-service'],
        ['tab', 'overview'],
      ]),
    );

    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: params.value },
            paramMap: params.asObservable(),
          },
        },
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-smb-admin' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    for (const label of ['概覽', '資料來源', '回答與記錄', '測試', '驗收', '發布', '使用紀錄']) {
      expect(page.textContent).toContain(label);
    }
    expect(page.querySelector('app-assistant-overview-tab')).not.toBeNull();
    expect((page.querySelector('#assistant-name') as HTMLInputElement).value).toBe('客服助理');
  });

  it('updates the visible panel when a detail tab link changes the route parameter', async () => {
    const params = new BehaviorSubject(
      new Map([
        ['id', 'assistant-customer-service'],
        ['tab', 'overview'],
      ]),
    );

    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: params.value },
            paramMap: params.asObservable(),
          },
        },
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-smb-admin' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();
    params.next(
      new Map([
        ['id', 'assistant-customer-service'],
        ['tab', 'data-sources'],
      ]),
    );
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('app-assistant-sources-tab')).not.toBeNull();
    expect(page.querySelector('app-source-connection-list')).not.toBeNull();
  });

  it('does not reveal a different account owner\'s assistant settings', async () => {
    const params = new BehaviorSubject(
      new Map([
        ['id', 'assistant-customer-service'],
        ['tab', 'overview'],
      ]),
    );

    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: params.value },
            paramMap: params.asObservable(),
          },
        },
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-internal-employee' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('你沒有這個助理的設定權限');
    expect(page.textContent).not.toContain('客服助理');
  });

  async function renderTab(
    tab: string,
    options: {
      readonly accountId?: AccountId;
      readonly storage?: ReturnType<typeof createMemoryStorage>;
      readonly repository?: MockDemoRepository;
      readonly apiMode?: boolean;
      readonly channel?: string;
      /** false：不等資料落地（讀取永遠不會完成時，`whenStable()` 也不會完成）。 */
      readonly waitForData?: boolean;
    } = {},
  ): Promise<{
    readonly page: HTMLElement;
    readonly flush: () => void;
    readonly settle: () => Promise<void>;
    readonly repository: MockDemoRepository;
  }> {
    TestBed.resetTestingModule();
    const params = new BehaviorSubject(new Map([['id', 'assistant-customer-service'], ['tab', tab]]));
    const query = new BehaviorSubject(new Map(options.channel === undefined ? [] : [['channel', options.channel]]));
    const accountId = options.accountId ?? 'account-smb-admin';
    const repository =
      options.repository ??
      new MockDemoRepository(DEMO_SEED, {
        storage: options.storage ?? createMemoryStorage(),
        now: () => new Date('2026-09-23T02:00:00.000Z'),
        // 助理的非同步契約（issue #49、#81）由 repository 的 `viewer` 選項推導目前帳號，
        // 要與下面 `DemoSessionService` 的假身分一致。
        viewer: () => accountId,
      });
    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([{ path: 'app/assistants', component: ListStubComponent }]),
        { provide: DEMO_REPOSITORY, useValue: repository },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: params.value, queryParamMap: query.value },
            paramMap: params.asObservable(),
            queryParamMap: query.asObservable(),
          },
        },
        {
          provide: DemoSessionService,
          useValue: {
            activeAccountId: signal<AccountId | null>(accountId),
          },
        },
        ...(options.apiMode ? [{ provide: ApiSessionService, useValue: { apiMode: true } }] : []),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    // 設定與 `listConnectableSources()` 都是非同步契約；即使 mock 是同步 Observable，
    // 仍要等一輪穩定才能讀到 `ready` 的結果。
    if (options.waitForData === false) fixture.detectChanges();
    else await settle();
    return {
      page: fixture.nativeElement as HTMLElement,
      flush: () => fixture.detectChanges(),
      settle,
      repository,
    };
  }

  it('shows a loading state while the settings are being read, and an error state when they cannot be', async () => {
    const pending = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' });
    pending.getAssistantSettings = () => NEVER;
    const loading = await renderTab('overview', { repository: pending, waitForData: false });
    expect(loading.page.textContent).toContain('正在載入助理設定');
    expect(loading.page.textContent).not.toContain('你沒有這個助理的設定權限');

    const failing = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' });
    failing.getAssistantSettings = () => throwError(() => new Error('offline'));
    const failed = await renderTab('overview', { repository: failing });
    expect(failed.page.textContent).toContain('目前無法載入這個助理');
    expect(failed.page.textContent).not.toContain('你沒有這個助理的設定權限');
  });

  it('asks for confirmation, saying everyone’s conversations go too, before deleting the assistant', async () => {
    const { page, settle, repository } = await renderTab('overview');
    const router = TestBed.inject(Router);

    (Array.from(page.querySelectorAll('button')).find((button) => button.textContent?.includes('刪除助理')) as HTMLButtonElement).click();
    await settle();

    const dialog = document.querySelector('.delete-panel') as HTMLElement;
    expect(dialog.textContent).toContain('所有成員與這個助理的對話紀錄也會一併刪除');
    expect(dialog.textContent).toContain('無法復原');

    (Array.from(dialog.querySelectorAll('button')).find((button) => button.textContent?.includes('刪除助理與所有對話')) as HTMLButtonElement).click();
    await settle();

    expect(router.url).toBe('/app/assistants');
    const settings = await firstValueFrom(repository.getAssistantSettings('assistant-customer-service'));
    expect(settings.status).toBe('permission-denied');
  });

  it('shows both the website and the LINE setup in API mode (LINE is no longer 將於後續版本開放)', async () => {
    const website = await renderTab('publishing', { apiMode: true, channel: 'website' });

    expect(website.page.querySelector('app-website-embed')).not.toBeNull();
    expect(website.page.querySelectorAll('app-assistant-publishing app-channel-card')).toHaveLength(3);

    const line = await renderTab('publishing', { apiMode: true, channel: 'line' });
    expect(line.page.querySelector('app-line-setup')).not.toBeNull();
    expect(line.page.textContent).not.toContain('將於後續版本開放');
    expect(line.page.querySelector('app-line-setup #line-test')).not.toBeNull();
  });

  it('links the test tab to the in-platform chat and shows outcome-only usage statistics in API mode', async () => {
    const test = await renderTab('test', { apiMode: true });
    const link = Array.from(test.page.querySelectorAll('a')).find((anchor) => anchor.textContent?.includes('開啟使用者對話畫面'));
    expect(link?.getAttribute('href')).toBe('/app/chat/assistant-customer-service');

    const activity = await renderTab('activity', { apiMode: true });
    expect(activity.page.querySelector('.usage-summary')).not.toBeNull();
    expect(activity.page.textContent).toContain('回覆總數');
    expect(activity.page.textContent).toContain('不含問題、回答、帳號或對話內容');
  });

  it('edits the audience from the overview tab and keeps it after the page is rebuilt', async () => {
    const storage = createMemoryStorage();
    const { page, flush } = await renderTab('overview', { storage });

    // 種子助理的使用對象是「內部員工與外部客戶」；取消內部員工後只剩外部客戶。
    (page.querySelector('#audience-internal') as HTMLInputElement).click();
    flush();
    expect(page.textContent).toContain('已自動儲存');

    const reopened = await renderTab('overview', { storage });
    expect((reopened.page.querySelector('#audience-internal') as HTMLInputElement).checked).toBe(false);
    expect((reopened.page.querySelector('#audience-external') as HTMLInputElement).checked).toBe(true);
    expect(reopened.page.textContent).toContain('上次儲存');
  });

  it('refuses to clear the last remaining audience and explains why', async () => {
    const { page, flush } = await renderTab('overview');

    (page.querySelector('#audience-internal') as HTMLInputElement).click();
    flush();
    (page.querySelector('#audience-external') as HTMLInputElement).click();
    flush();

    expect(page.textContent).toContain('至少要保留一種使用對象');
    expect((page.querySelector('#audience-external') as HTMLInputElement).checked).toBe(true);
  });

  it('disconnects a data source from the data source tab', async () => {
    const { page, flush } = await renderTab('data-sources');
    const row = Array.from(page.querySelectorAll('.source-row')).find((candidate) =>
      candidate.textContent?.includes('商品使用指南'),
    );

    expect(row?.querySelector('button')?.getAttribute('aria-pressed')).toBe('true');
    (row?.querySelector('button') as HTMLButtonElement).click();
    flush();

    const after = Array.from(page.querySelectorAll('.source-row')).find((candidate) =>
      candidate.textContent?.includes('商品使用指南'),
    );
    expect(after?.querySelector('button')?.getAttribute('aria-pressed')).toBe('false');
  });

  it('links each connected source name to its knowledge or database detail page', async () => {
    const { page } = await renderTab('data-sources');

    const knowledgeRow = Array.from(page.querySelectorAll('.source-row')).find((candidate) =>
      candidate.textContent?.includes('商品使用指南'),
    );
    expect(knowledgeRow?.querySelector('th a')?.getAttribute('href')).toBe(
      '/app/knowledge/knowledge-product-guide/content',
    );

    const databaseRow = Array.from(page.querySelectorAll('.source-row')).find((candidate) =>
      candidate.textContent?.includes('訂單資料庫'),
    );
    expect(databaseRow?.querySelector('th a')?.getAttribute('href')).toBe(
      '/app/databases/database-orders/form',
    );
  });

  it('toggles 嚴格回答 and 保存自己的對話 from the answer tab', async () => {
    const storage = createMemoryStorage();
    const { page, flush } = await renderTab('rules', { storage });

    expect((page.querySelector('#scope-general') as HTMLInputElement).checked).toBe(true);
    (page.querySelector('#scope-strict') as HTMLInputElement).click();
    flush();
    (page.querySelector('#keep-conversations') as HTMLInputElement).click();
    flush();

    const reopened = await renderTab('rules', { storage });
    expect((reopened.page.querySelector('#scope-strict') as HTMLInputElement).checked).toBe(true);
    expect((reopened.page.querySelector('#keep-conversations') as HTMLInputElement).checked).toBe(false);
    expect(reopened.page.textContent).toContain('已經保存的對話不會被刪除');
  });

  it('keeps the three editors away from an account that does not own the assistant', async () => {
    for (const tab of ['overview', 'data-sources', 'rules']) {
      const { page } = await renderTab(tab, { accountId: 'account-internal-employee' });

      expect(page.textContent).toContain('你沒有這個助理的設定權限');
      expect(page.querySelector('#assistant-name')).toBeNull();
      expect(page.querySelector('app-source-connection-list')).toBeNull();
      expect(page.querySelector('#keep-conversations')).toBeNull();
    }
  });

  it('opens the end-user chat from the test tab', async () => {
    const { page } = await renderTab('test');
    const link = Array.from(page.querySelectorAll('a')).find((anchor) => anchor.textContent?.includes('開啟使用者對話畫面'));

    expect(link?.getAttribute('href')).toBe('/use/assistant-customer-service');
  });

  it('shows the owner outcome-only usage counts without any conversation text', async () => {
    const { page } = await renderTab('activity');
    const usage = page.querySelector('.usage-summary');

    expect(usage?.textContent).toContain('回覆總數');
    expect(usage?.textContent).toContain('18');
    expect(page.textContent).toContain('不含問題、回答、帳號或對話內容');
    expect(page.textContent).not.toContain('如何處理退貨申請');
  });
  it('shows the same publishing channel content in the publishing tab, opened at the requested channel', async () => {
    const params = new BehaviorSubject(new Map([['id', 'assistant-customer-service'], ['tab', 'publishing']]));
    const query = new BehaviorSubject(new Map([['channel', 'line']]));
    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: params.value, queryParamMap: query.value },
            paramMap: params.asObservable(),
            queryParamMap: query.asObservable(),
          },
        },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-smb-admin' } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelectorAll('app-assistant-publishing app-channel-card')).toHaveLength(3);
    expect(page.textContent).not.toContain('Demo，不會連接外部服務');
    expect(page.querySelector('app-line-setup')).not.toBeNull();
    expect(page.querySelector('app-website-embed')).toBeNull();
  });
});
