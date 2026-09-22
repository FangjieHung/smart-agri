import { TestBed } from '@angular/core/testing';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { PurposeStepComponent } from './purpose-step.component';

function render() {
  TestBed.configureTestingModule({
    imports: [PurposeStepComponent],
    providers: [AssistantDraftStore, ...provideWizardTesting().providers],
  });
  const fixture = TestBed.createComponent(PurposeStepComponent);
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store: TestBed.inject(AssistantDraftStore) };
}

describe('PurposeStepComponent', () => {
  it('prefills the form from the 回答客戶問題 template', () => {
    const { fixture, page } = render();

    const template = Array.from(page.querySelectorAll<HTMLInputElement>('input[name="assistant-template"]'))
      .find((input) => input.closest('label')?.textContent?.includes('回答客戶問題'));
    template?.click();
    fixture.detectChanges();

    expect((page.querySelector('#assistant-name') as HTMLInputElement).value).toBe('客戶問答助理');
    expect((page.querySelector('#assistant-purpose') as HTMLTextAreaElement).value).not.toBe('');
  });

  it('maps the internal and external checkboxes to one audience value', () => {
    const { fixture, page, store } = render();

    (page.querySelector('#audience-internal') as HTMLInputElement).click();
    (page.querySelector('#audience-external') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(store.draft().audience).toBe('members-and-external-customers');
  });

  it('keeps advanced role instructions collapsed by default', () => {
    const { page } = render();

    const details = page.querySelector('details.advanced') as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(details.querySelector('summary')?.textContent).toContain('進階角色指令');
  });
});
