import { TestBed } from '@angular/core/testing';
import type { DatabaseTrialAnswers } from '../../../core/domain/database.model';
import { orderForm } from '../assistant-use.testing';
import { InlineFormComponent } from './inline-form.component';

function render() {
  TestBed.configureTestingModule({ imports: [InlineFormComponent] });
  const fixture = TestBed.createComponent(InlineFormComponent);
  fixture.componentRef.setInput('form', orderForm());
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('InlineFormComponent', () => {
  it('renders every form field with a visible label', () => {
    const { host } = render();

    expect(host.querySelector('label[for="chat-field-field-order-number"]')?.textContent).toContain('訂單編號');
    expect(host.querySelector('#chat-field-field-reported-on')?.getAttribute('type')).toBe('date');
    expect(host.querySelectorAll('fieldset input[type="radio"]')).toHaveLength(3);
  });

  it('emits the answers for review', () => {
    const { fixture, host } = render();
    const reviewed: DatabaseTrialAnswers[] = [];
    fixture.componentInstance.review.subscribe((answers) => reviewed.push(answers));

    const input = host.querySelector<HTMLInputElement>('#chat-field-field-order-number');
    if (input === null) throw new Error('missing input');
    input.value = 'DEMO-2001';
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLInputElement>('input[type="radio"][value="配送延遲"]')?.click();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();

    expect(reviewed[0]).toMatchObject({ 'field-order-number': 'DEMO-2001', 'field-issue-type': '配送延遲' });
  });

  it('marks invalid fields and announces the errors', () => {
    const { fixture, host } = render();
    fixture.componentRef.setInput('errors', [{ fieldId: 'field-order-number', message: '「訂單編號」為必填。' }]);
    fixture.detectChanges();

    expect(host.querySelector('#chat-field-field-order-number')?.getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('「訂單編號」為必填。');
  });
});
