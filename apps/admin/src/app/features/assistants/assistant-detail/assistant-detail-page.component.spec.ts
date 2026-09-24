import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantDetailPageComponent } from './assistant-detail-page.component';

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
    for (const label of ['概覽', '資料來源', '回答與記錄', '測試', '發布', '使用紀錄']) {
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
    } = {},
  ): Promise<{ readonly page: HTMLElement; readonly flush: () => void }> {
    TestBed.resetTestingModule();
    const params = new BehaviorSubject(new Map([['id', 'assistant-customer-service'], ['tab', tab]]));
    const repository = new MockDemoRepository(DEMO_SEED, {
      storage: options.storage ?? createMemoryStorage(),
      now: () => new Date('2026-09-23T02:00:00.000Z'),
    });
    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: repository },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: params.value }, paramMap: params.asObservable() } },
        {
          provide: DemoSessionService,
          useValue: {
            activeAccountId: signal<AccountId | null>(options.accountId ?? 'account-smb-admin'),
          },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();
    return {
      page: fixture.nativeElement as HTMLElement,
      flush: () => fixture.detectChanges(),
    };
  }

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

  it('shows the owner anonymous usage counts without any conversation text', async () => {
    const { page } = await renderTab('activity');
    const usage = page.querySelector('.usage-summary');

    expect(usage?.textContent).toContain('對話次數');
    expect(usage?.textContent).toContain('18');
    expect(page.textContent).toContain('看不到使用者的對話內容');
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
