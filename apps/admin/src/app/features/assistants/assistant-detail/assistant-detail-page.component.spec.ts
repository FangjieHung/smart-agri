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
});
