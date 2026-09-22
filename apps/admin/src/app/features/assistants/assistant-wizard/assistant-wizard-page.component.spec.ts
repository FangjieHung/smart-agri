import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { createEmptyAssistantDraft } from '../../../core/domain/assistant-draft.model';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { AssistantWizardPageComponent } from './assistant-wizard-page.component';
import { provideWizardTesting } from './assistant-wizard.testing';

@Component({ template: 'detail' })
class DetailStubComponent {}

async function openWizard(url: string, storage = createMemoryStorage()) {
  const wizard = provideWizardTesting({ storage });
  TestBed.configureTestingModule({
    providers: [
      ...wizard.providers,
      provideRouter([
        { path: 'app/assistants/new/:step', component: AssistantWizardPageComponent },
        { path: 'app/assistants/:id/:tab', component: DetailStubComponent },
      ]),
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  harness.detectChanges();
  return { harness, repository: wizard.repository, router: TestBed.inject(Router) };
}

describe('AssistantWizardPageComponent', () => {
  it('shows a four-step indicator with the current step marked for assistive tech', async () => {
    const { harness } = await openWizard('/app/assistants/new/purpose');
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
      '尚未開始編輯',
    );
  });

  it('blocks leaving a step with missing required fields and ties errors to them', async () => {
    const { harness, router } = await openWizard('/app/assistants/new/purpose');
    const page = harness.routeNativeElement as HTMLElement;

    (page.querySelector('button.wizard-next') as HTMLButtonElement).click();
    harness.detectChanges();

    const nameInput = page.querySelector('#assistant-name') as HTMLInputElement;
    expect(nameInput.getAttribute('aria-invalid')).toBe('true');
    const errorId = nameInput.getAttribute('aria-describedby') ?? '';
    expect(page.querySelector(`#${errorId.split(' ').pop()}`)?.textContent).toContain(
      '請輸入助理名稱',
    );
    expect(router.url).toBe('/app/assistants/new/purpose');
  });

  it('sends a deep link to a later step back to the first incomplete step', async () => {
    const { harness, router } = await openWizard('/app/assistants/new/rules');
    await harness.fixture.whenStable();

    expect(router.url).toBe('/app/assistants/new/purpose');
  });

  it('resumes a saved draft on its saved step', async () => {
    const storage = createMemoryStorage();
    const seeded = provideWizardTesting({ storage });
    seeded.repository.saveAssistantDraft('account-smb-admin', {
      ...createEmptyAssistantDraft(),
      name: '續寫助理',
      purpose: '回答客戶問題',
      audience: 'account-members',
      currentStep: 'sources',
    });

    const { harness } = await openWizard('/app/assistants/new/sources', storage);
    const page = harness.routeNativeElement as HTMLElement;

    expect(page.querySelector('[aria-current="step"]')?.textContent).toContain('資料來源');
    expect(page.querySelector('.autosave')?.textContent).toContain('已載入先前的草稿');
  });
});
