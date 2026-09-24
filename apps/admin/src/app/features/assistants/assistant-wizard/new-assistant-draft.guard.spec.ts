import { TestBed } from '@angular/core/testing';
import { convertToParamMap, Router, type ActivatedRouteSnapshot } from '@angular/router';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { existingAssistantDraftGuard, newAssistantDraftGuard } from './new-assistant-draft.guard';

function routeWithStep(step: string | null): ActivatedRouteSnapshot {
  return { paramMap: convertToParamMap(step === null ? {} : { step }) } as ActivatedRouteSnapshot;
}

function configure(accountId: string | null) {
  TestBed.configureTestingModule({
    providers: [
      Router,
      {
        provide: DemoSessionService,
        useValue: { activeAccountId: () => accountId },
      },
      { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository() },
    ],
  });
}

describe('newAssistantDraftGuard', () => {
  it('creates an independent draft and lands on the requested step', () => {
    configure('account-smb-admin');
    const router = TestBed.inject(Router);

    const result = TestBed.runInInjectionContext(() =>
      newAssistantDraftGuard(routeWithStep('rules'), {} as never),
    );

    expect(result).toEqual(router.createUrlTree(['/app/assistants/drafts', 'draft-1', 'rules']));
  });

  it('falls back to the purpose step for an unknown or missing step value', () => {
    configure('account-smb-admin');
    const router = TestBed.inject(Router);

    const result = TestBed.runInInjectionContext(() =>
      newAssistantDraftGuard(routeWithStep('not-a-step'), {} as never),
    );

    expect(result).toEqual(
      router.createUrlTree(['/app/assistants/drafts', 'draft-1', 'purpose']),
    );
  });

  it('sends an unauthenticated visitor to login', () => {
    configure(null);
    const router = TestBed.inject(Router);

    const result = TestBed.runInInjectionContext(() =>
      newAssistantDraftGuard(routeWithStep('purpose'), {} as never),
    );

    expect(result).toEqual(router.createUrlTree(['/login']));
  });
});

describe('existingAssistantDraftGuard', () => {
  it('allows the owner of an existing draft through', () => {
    configure('account-smb-admin');
    const repository = TestBed.inject(DEMO_REPOSITORY) as MockDemoRepository;
    const created = repository.createNamedAssistantDraft('account-smb-admin');
    if (created.status !== 'ready') throw new Error('setup failed');

    const result = TestBed.runInInjectionContext(() =>
      existingAssistantDraftGuard(
        { paramMap: convertToParamMap({ draftId: created.data.id }) } as ActivatedRouteSnapshot,
        {} as never,
      ),
    );

    expect(result).toBe(true);
  });

  it('redirects to the list for a missing draft id', () => {
    configure('account-smb-admin');
    const router = TestBed.inject(Router);

    const result = TestBed.runInInjectionContext(() =>
      existingAssistantDraftGuard(
        { paramMap: convertToParamMap({ draftId: 'does-not-exist' }) } as ActivatedRouteSnapshot,
        {} as never,
      ),
    );

    expect(result).toEqual(router.createUrlTree(['/app/assistants']));
  });
});
