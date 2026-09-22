import { TestBed } from '@angular/core/testing';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { TestStepComponent } from './test-step.component';

function render() {
  TestBed.configureTestingModule({
    imports: [TestStepComponent],
    providers: [AssistantDraftStore, ...provideWizardTesting().providers],
  });
  const store = TestBed.inject(AssistantDraftStore);
  store.applyTemplate('answer-customer-questions');
  store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
  const fixture = TestBed.createComponent(TestStepComponent);
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store };
}

function ask(page: HTMLElement, text: string): void {
  const button = Array.from(page.querySelectorAll<HTMLButtonElement>('.trial-question')).find(
    (candidate) => candidate.textContent?.includes(text),
  );
  button?.click();
}

describe('TestStepComponent', () => {
  it('previews a cited company-data answer from fixtures', () => {
    const { fixture, page, store } = render();

    ask(page, '退貨');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.textContent).toContain('根據你的資料');
    expect(answer.textContent).toContain('退換貨政策');
    expect(store.draft().testedQuestionIds).toEqual(['trial-refund-window']);
  });

  it('shows the configured refusal when strict mode has no matching data', () => {
    const { fixture, page, store } = render();

    ask(page, '皮革');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.textContent).toContain('資料中沒有答案');
    expect(answer.textContent).toContain(store.draft().rules.refusalMessage);
  });
});
