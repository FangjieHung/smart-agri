import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantDetailPageComponent } from './assistant-detail-page.component';

describe('AssistantDetailPageComponent', () => {
  it('provides the six planned detail tabs as recognisable placeholder panels', async () => {
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
    expect(page.textContent).toContain('概覽內容將在下一階段完成');
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

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      '資料來源內容將在下一階段完成',
    );
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

  async function renderTab(tab: string): Promise<HTMLElement> {
    const params = new BehaviorSubject(new Map([['id', 'assistant-customer-service'], ['tab', tab]]));
    await TestBed.configureTestingModule({
      imports: [AssistantDetailPageComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: params.value }, paramMap: params.asObservable() } },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-smb-admin' } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AssistantDetailPageComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('opens the end-user chat from the test tab', async () => {
    const page = await renderTab('test');
    const link = Array.from(page.querySelectorAll('a')).find((anchor) => anchor.textContent?.includes('開啟使用者對話畫面'));

    expect(link?.getAttribute('href')).toBe('/use/assistant-customer-service');
  });

  it('shows the owner anonymous usage counts without any conversation text', async () => {
    const page = await renderTab('activity');
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

    expect(page.textContent).not.toContain('發布內容將在下一階段完成');
    expect(page.querySelectorAll('app-assistant-publishing app-channel-card')).toHaveLength(3);
    expect(page.textContent).toContain('Demo，不會連接外部服務');
    expect(page.querySelector('app-line-setup')).not.toBeNull();
    expect(page.querySelector('app-website-embed')).toBeNull();
  });
});

