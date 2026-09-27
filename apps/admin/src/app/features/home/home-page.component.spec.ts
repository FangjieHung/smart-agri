import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { createEmptyAssistantDraft } from '../../core/domain/assistant-draft.model';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { HomePageComponent } from './home-page.component';

describe('HomePageComponent', () => {
  it('shows the create, continue, and pending-work areas for a signed-in account', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-smb-admin' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.page-header__actions a[href="/app/assistants/new/purpose"]')?.textContent).toContain(
      '建立新助理',
    );
    expect(page.textContent).toContain('建立新助理');
    expect(page.textContent).toContain('繼續設定');
    expect(page.textContent).toContain('待處理事項');
  });

  it('offers to resume the signed-in account\'s unfinished assistant draft at its saved step', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    repository.saveAssistantDraft('account-smb-admin', {
      ...createEmptyAssistantDraft(),
      name: '客戶問答助理',
      currentStep: 'rules',
    });

    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: repository },
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-smb-admin' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    const link = Array.from(page.querySelectorAll('a')).find((anchor) =>
      anchor.textContent?.includes('繼續最近的設定'),
    );
    expect(page.textContent).toContain('客戶問答助理');
    expect(link?.getAttribute('href')).toBe('/app/assistants/drafts/draft-legacy/rules');
  });

  it('lists the assistants an external customer can use with a link into the chat', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() }) },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const section = (fixture.nativeElement as HTMLElement).querySelector('section[aria-labelledby="usable-title"]');
    const link = Array.from(section?.querySelectorAll('a') ?? []).find((anchor) =>
      anchor.textContent?.includes('客服助理'),
    );
    expect(section?.textContent).toContain('我的助理');
    expect(link?.getAttribute('href')).toBe('/use/assistant-customer-service');
    expect(section?.textContent).not.toContain('內部教育訓練助理');
  });

  it('hides 建立新助理 from an account without manage-assistants and offers a usable assistant instead', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() }) },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('a[href="/app/assistants/new/purpose"]')).toBeNull();
    expect(page.textContent).not.toContain('建立新助理');

    const primaryCard = page.querySelector('.home-card--primary');
    expect(primaryCard?.textContent).toContain('客服助理');
    expect(primaryCard?.querySelector('a')?.getAttribute('href')).toBe('/use/assistant-customer-service');
  });

  it('tells an account without manage-assistants and without any usable assistant who to contact', async () => {
    const seed = { ...DEMO_SEED, assistants: [] };

    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(seed, { storage: createMemoryStorage() }) },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('a[href="/app/assistants/new/purpose"]')).toBeNull();
    expect(page.textContent).not.toContain('建立新助理');

    const primaryCard = page.querySelector('.home-card--primary');
    expect(primaryCard?.querySelector('a')).toBeNull();
    expect(primaryCard?.textContent).toContain('聯絡管理者');
  });
});
