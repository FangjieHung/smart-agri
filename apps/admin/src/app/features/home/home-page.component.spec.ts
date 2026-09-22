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
      anchor.textContent?.includes('繼續未完成的設定'),
    );
    expect(page.textContent).toContain('客戶問答助理');
    expect(link?.getAttribute('href')).toBe('/app/assistants/new/rules');
  });
});
