import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NEVER, throwError } from 'rxjs';
import { createEmptyAssistantDraft, type AssistantDraft } from '../../../core/domain/assistant-draft.model';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { AssistantWizardPageComponent } from './assistant-wizard-page.component';
import { provideWizardTesting } from './assistant-wizard.testing';

@Component({ template: 'detail' })
class DetailStubComponent {}

async function openWizard(step: string, options: { readonly draft?: AssistantDraft } = {}) {
  const wizard = provideWizardTesting({ storage: createMemoryStorage(), draft: options.draft });
  TestBed.configureTestingModule({
    providers: [
      ...wizard.providers.filter((provider) => (provider as { provide?: unknown }).provide !== ActivatedRoute),
      provideRouter([
        { path: 'app/assistants/drafts/:draftId/:step', component: AssistantWizardPageComponent },
        { path: 'app/assistants/:id/:tab', component: DetailStubComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/app/assistants/drafts/${wizard.draftId}/${step}`);
  // 草稿以 `repositoryResource` 非同步讀取：等它落地、步驟同步完成後再檢查畫面。
  await harness.fixture.whenStable();
  harness.detectChanges();
  return { harness, repository: wizard.repository, router: TestBed.inject(Router), draftId: wizard.draftId };
}

describe('AssistantWizardPageComponent', () => {
  it('shows a four-step indicator with the current step marked for assistive tech', async () => {
    const { harness } = await openWizard('purpose');
    const page = harness.routeNativeElement as HTMLElement;

    const steps = Array.from(page.querySelectorAll('.wizard-steps li'));
    expect(steps.map((step) => step.textContent?.replace(/\s+/g, ''))).toEqual([
      '1用途',
      '2資料來源',
      '3回答規則',
      '4試問確認',
    ]);
    expect(page.querySelector('[aria-current="step"]')?.textContent).toContain('用途');
    expect(page.querySelector('[role="status"].autosave')?.textContent).toContain(
      '已載入先前的草稿',
    );
  });

  it('blocks leaving a step with missing required fields and ties errors to them', async () => {
    const { harness, router, draftId } = await openWizard('purpose');
    const page = harness.routeNativeElement as HTMLElement;

    (page.querySelector('button.wizard-next') as HTMLButtonElement).click();
    harness.detectChanges();

    const nameInput = page.querySelector('#assistant-name') as HTMLInputElement;
    expect(nameInput.getAttribute('aria-invalid')).toBe('true');
    const errorId = nameInput.getAttribute('aria-describedby') ?? '';
    expect(page.querySelector(`#${errorId.split(' ').pop()}`)?.textContent).toContain(
      '請輸入助理名稱',
    );
    expect(router.url).toBe(`/app/assistants/drafts/${draftId}/purpose`);
  });

  it('sends a deep link to a later step back to the first incomplete step', async () => {
    const { harness, router, draftId } = await openWizard('rules');
    await harness.fixture.whenStable();

    expect(router.url).toBe(`/app/assistants/drafts/${draftId}/purpose`);
  });

  it('resumes a saved draft on its saved step', async () => {
    const { harness } = await openWizard('sources', {
      draft: {
        ...createEmptyAssistantDraft(),
        name: '續寫助理',
        purpose: '回答客戶問題',
        audience: 'account-members',
        currentStep: 'sources',
      },
    });
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('[aria-current="step"]')?.textContent).toContain('資料來源');
    expect(page.querySelector('.autosave')?.textContent).toContain('已載入先前的草稿');
  });

  it('shows the loading state, not the wizard, while the draft is still being read', async () => {
    const wizard = provideWizardTesting();
    wizard.repository.getNamedAssistantDraft = () => NEVER;
    TestBed.configureTestingModule({
      providers: [
        ...wizard.providers.filter((provider) => (provider as { provide?: unknown }).provide !== ActivatedRoute),
        provideRouter([{ path: 'app/assistants/drafts/:draftId/:step', component: AssistantWizardPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/app/assistants/drafts/${wizard.draftId}/purpose`);
    harness.detectChanges();

    const page = harness.routeNativeElement as HTMLElement;
    expect(page.textContent).toContain('正在載入草稿');
    expect(page.querySelector('.wizard-steps')).toBeNull();
  });

  it('shows an error, not a permission problem, when the draft cannot be read', async () => {
    const wizard = provideWizardTesting();
    wizard.repository.getNamedAssistantDraft = () => throwError(() => new Error('offline'));
    TestBed.configureTestingModule({
      providers: [
        ...wizard.providers.filter((provider) => (provider as { provide?: unknown }).provide !== ActivatedRoute),
        provideRouter([{ path: 'app/assistants/drafts/:draftId/:step', component: AssistantWizardPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/app/assistants/drafts/${wizard.draftId}/purpose`);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement as HTMLElement;
    expect(page.textContent).toContain('目前無法載入這份草稿');
    expect(page.textContent).not.toContain('你沒有建立助理的權限');
  });

  it('disables 建立助理 while creating, so a second click does not create twice', async () => {
    const complete: AssistantDraft = {
      ...createEmptyAssistantDraft(),
      templateId: 'answer-customer-questions',
      name: '客戶問答助理',
      purpose: '回答客戶問題',
      audience: 'account-members',
      sources: [{ id: 'knowledge-refund-policy', type: 'knowledge-base' }],
      testedQuestionIds: ['trial-refund-window'],
      currentStep: 'test',
    };
    const { harness, repository } = await openWizard('test', { draft: complete });
    let calls = 0;
    repository.createAssistantFromDraft = () => {
      calls += 1;
      return NEVER;
    };
    const page = harness.routeNativeElement as HTMLElement;

    const button = page.querySelector('button.wizard-create') as HTMLButtonElement;
    button.click();
    await harness.fixture.whenStable();
    harness.detectChanges();
    button.click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(calls).toBe(1);
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('建立中');
  });

  it('shows another account’s or a missing draft as permission denied', async () => {
    const wizard = provideWizardTesting();
    TestBed.configureTestingModule({
      providers: [
        ...wizard.providers.filter((provider) => (provider as { provide?: unknown }).provide !== ActivatedRoute),
        provideRouter([{ path: 'app/assistants/drafts/:draftId/:step', component: AssistantWizardPageComponent }]),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/assistants/drafts/draft-999/purpose');
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement as HTMLElement;
    expect(page.textContent).toContain('你沒有建立助理的權限');
    expect(page.querySelector('.wizard-steps')).toBeNull();
  });
});
