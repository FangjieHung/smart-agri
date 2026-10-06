import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { CasesRepository } from '../../core/repositories/cases.repository';
import { createEmptyAssistantDraft } from '../../core/domain/assistant-draft.model';
import { createMemoryStorage } from '../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import type { AccountId } from '../../core/domain/account.model';
import type { DemoScenario } from '../../core/repositories/demo-repository';
import { HomePageComponent } from './home-page.component';

/** mock 的非同步契約由 `viewer` 推導目前帳號，要與假工作階段一致。 */
function mockRepository(viewer: AccountId, seed = DEMO_SEED): MockDemoRepository {
  return new MockDemoRepository(seed, { storage: createMemoryStorage(), viewer: () => viewer });
}

/** 清單以 `repositoryResource` 非同步讀取：等它落地再檢查畫面。 */
async function render<T>(fixture: import('@angular/core/testing').ComponentFixture<T>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

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
    await render(fixture);

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.page-header__actions a[href="/app/assistants/new/purpose"]')?.textContent).toContain(
      '建立新助理',
    );
    expect(page.textContent).toContain('建立新助理');
    expect(page.textContent).toContain('繼續設定');
    expect(page.textContent).toContain('待處理事項');
  });

  it('offers to resume the signed-in account\'s unfinished assistant draft at its saved step', async () => {
    const repository = mockRepository('account-smb-admin');
    let draftId = '';
    repository.createNamedAssistantDraft().subscribe((result) => {
      if (result.status === 'ready') draftId = result.data.id;
    });
    repository
      .saveNamedAssistantDraft(draftId, { ...createEmptyAssistantDraft(), name: '客戶問答助理', currentStep: 'rules' }, 1)
      .subscribe();

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
    await render(fixture);

    const page = fixture.nativeElement as HTMLElement;
    const link = Array.from(page.querySelectorAll('a')).find((anchor) =>
      anchor.textContent?.includes('繼續最近的設定'),
    );
    expect(page.textContent).toContain('客戶問答助理');
    expect(link?.getAttribute('href')).toBe(`/app/assistants/drafts/${draftId}/rules`);
  });

  it('lists the assistants an external customer can use with a link into the chat', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: mockRepository('account-external-customer') },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    await render(fixture);

    const section = (fixture.nativeElement as HTMLElement).querySelector('section[aria-labelledby="usable-title"]');
    const link = Array.from(section?.querySelectorAll('a') ?? []).find((anchor) =>
      anchor.textContent?.includes('客服助理'),
    );
    expect(section?.textContent).toContain('我的助理');
    expect(link?.getAttribute('href')).toBe('/app/chat/assistant-customer-service');
    expect(section?.textContent).not.toContain('內部教育訓練助理');
  });

  it('hides 建立新助理 from an account without manage-assistants and offers a usable assistant instead', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: mockRepository('account-external-customer') },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    await render(fixture);

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('a[href="/app/assistants/new/purpose"]')).toBeNull();
    expect(page.textContent).not.toContain('建立新助理');

    const primaryCard = page.querySelector('.home-card--primary');
    expect(primaryCard?.textContent).toContain('客服助理');
    expect(primaryCard?.querySelector('a')?.getAttribute('href')).toBe('/app/chat/assistant-customer-service');
  });

  it('tells an account without manage-assistants and without any usable assistant who to contact', async () => {
    const seed = { ...DEMO_SEED, assistants: [] };

    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: mockRepository('account-external-customer', seed) },
        { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-external-customer' } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    await render(fixture);

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('a[href="/app/assistants/new/purpose"]')).toBeNull();
    expect(page.textContent).not.toContain('建立新助理');

    const primaryCard = page.querySelector('.home-card--primary');
    expect(primaryCard?.querySelector('a')).toBeNull();
    expect(primaryCard?.textContent).toContain('聯絡管理者');
  });
  describe('monthly usage banner (issue #203)', () => {
    async function renderFor(account: AccountId, scenario?: DemoScenario): Promise<HTMLElement> {
      const repository = mockRepository(account);
      if (scenario !== undefined) repository.setScenario(scenario);
      await TestBed.configureTestingModule({
        imports: [HomePageComponent],
        providers: [
          provideRouter([]),
          { provide: DEMO_REPOSITORY, useValue: repository },
          { provide: DemoSessionService, useValue: { activeAccountId: () => account } },
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(HomePageComponent);
      await render(fixture);
      return fixture.nativeElement as HTMLElement;
    }

    it('shows the near banner to an account with manage-publishing', async () => {
      const page = await renderFor('account-smb-admin');

      expect(page.querySelector('.usage--banner[role="status"]')?.textContent).toContain('接近上限');
    });

    it('shows the exceeded banner, and nothing when usage is normal', async () => {
      const exceeded = await renderFor('account-smb-admin', 'usage-exceeded');
      expect(exceeded.querySelector('.usage--banner')?.textContent).toContain('對外回覆已暫停');

      TestBed.resetTestingModule();
      const normal = await renderFor('account-smb-admin', 'usage-normal');
      expect(normal.querySelector('.usage--banner')).toBeNull();
    });

    it('shows no banner to an account without manage-publishing', async () => {
      const page = await renderFor('account-internal-employee');

      expect(page.querySelector('.usage--banner')).toBeNull();
      expect(page.textContent).not.toContain('接近上限');
    });
  });

  describe('案件 card (issue #250)', () => {
    async function renderCases(accountId: AccountId) {
      const attention = vi.fn(() => of({
        status: 'ready' as const,
        data: { overdueCount: 3, ownedOverdueCount: 2, groupPendingOverdueCount: 1, pendingForMeCount: 4 },
      }));
      await TestBed.configureTestingModule({
        imports: [HomePageComponent],
        providers: [
          provideRouter([]),
          { provide: DemoSessionService, useValue: { activeAccountId: () => accountId } },
          { provide: CasesRepository, useValue: { attention } },
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(HomePageComponent);
      await render(fixture);
      return { page: fixture.nativeElement as HTMLElement, attention };
    }

    it('shows the overdue and 待我受理 numbers, each linking to the matching list filter', async () => {
      const { page, attention } = await renderCases('account-internal-employee');

      expect(attention).toHaveBeenCalledOnce();
      const card = page.querySelector('[data-home-cases]') as HTMLElement;
      expect(card.querySelector('h2')?.textContent).toBe('案件');
      expect(card.querySelector('[data-home-cases-summary]')?.textContent?.trim()).toBe('已逾期 3 件，待我受理 4 件。');
      const link = (name: string) => card.querySelector(`[data-home-cases-link="${name}"]`) as HTMLAnchorElement;
      expect(link('owned-overdue').textContent).toContain('我負責的逾期案件 2 件');
      expect(link('owned-overdue').getAttribute('href')).toBe('/app/cases?scope=owned&overdue=true');
      expect(link('group-overdue').textContent).toContain('承辦組待受理的逾期案件 1 件');
      expect(link('group-overdue').getAttribute('href')).toBe('/app/cases?scope=my-groups&status=pending&overdue=true');
      expect(link('pending').textContent).toContain('待我受理 4 件');
      expect(link('pending').getAttribute('href')).toBe('/app/cases?scope=my-groups&status=pending');
    });

    it('is not shown to an external customer, who never asks for it', async () => {
      const { page, attention } = await renderCases('account-external-customer');

      expect(page.querySelector('[data-home-cases]')).toBeNull();
      expect(attention).not.toHaveBeenCalled();
      expect(page.textContent).toContain('待處理事項');
    });
  });
});
