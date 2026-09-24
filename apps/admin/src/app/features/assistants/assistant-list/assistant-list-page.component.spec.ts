import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DEMO_SEED, type DemoSeed } from '../../../core/repositories/demo-seed';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantListPageComponent } from './assistant-list-page.component';

describe('AssistantListPageComponent', () => {
  it('separates assistants owned by the account from company assistants it may use', async () => {
    const seed: DemoSeed = {
      ...DEMO_SEED,
      assistants: [
        ...DEMO_SEED.assistants,
        {
          id: 'assistant-internal-onboarding',
          ownerAccountId: 'account-internal-employee',
          name: '內部新手助理',
          purpose: '協助新同仁熟悉客服處理流程',
          status: 'draft',
          audience: 'account-members',
          sharedWithAccountIds: [],
          knowledgeBaseIds: [],
          databaseIds: [],
          keepOwnConversations: true,
        },
      ],
    };

    await TestBed.configureTestingModule({
      imports: [AssistantListPageComponent],
      providers: [
        provideRouter([]),
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-internal-employee' },
        },
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(seed) },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AssistantListPageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('內部新手助理');
    expect(page.textContent).toContain('我建立的');
    expect(page.textContent).toContain('公司建立的');
    expect(page.textContent).toContain('客服助理');
    expect(page.querySelector('a[href="/app/chat/assistant-customer-service"]')?.textContent).toContain('對話');
  });
});
