import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import type { DatabaseTrialAnswers } from '../../../core/domain/database.model';
import { orderForm } from '../assistant-use.testing';
import { InlineFormComponent } from './inline-form.component';

function render() {
  TestBed.configureTestingModule({ imports: [InlineFormComponent], providers: [provideRouter([])] });
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

  it('offers a labelled close button and 「不用了」, both closing without reviewing (#171)', () => {
    const { fixture, host } = render();
    let cancelled = 0;
    let reviewed = 0;
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.componentInstance.review.subscribe(() => reviewed++);

    const close = host.querySelector<HTMLButtonElement>('button[aria-label="關閉表單，不填寫"]');
    expect(close?.closest('.title-row')?.querySelector('h2')?.textContent).toContain(orderForm().title);
    close?.click();
    const buttons = Array.from(host.querySelectorAll<HTMLButtonElement>('.actions button')).map((button) => button.textContent?.trim());
    expect(buttons).toEqual(['下一步：確認同意', '不用了']);
    Array.from(host.querySelectorAll<HTMLButtonElement>('.actions button')).find((button) => button.textContent?.trim() === '不用了')?.click();

    expect(cancelled).toBe(2);
    expect(reviewed).toBe(0);
  });

  it('links to 我送出的資料 directly when nothing is filled in, and asks first otherwise (#171)', async () => {
    const { fixture, host } = render();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    let leaveRequests = 0;
    fixture.componentInstance.leaveRequested.subscribe(() => leaveRequests++);

    const link = host.querySelector<HTMLAnchorElement>('.withdraw-hint a');
    expect(host.querySelector('.withdraw-hint')?.textContent).toContain('想撤回已送出的資料？');
    expect(link?.textContent).toBe('前往我送出的資料');
    expect(link?.getAttribute('href')).toBe('/app/activity');
    link?.click();
    expect(leaveRequests).toBe(0);
    expect(navigate).toHaveBeenCalledWith('/app/activity');
    expect(navigate).toHaveBeenCalledTimes(1);

    const input = host.querySelector<HTMLInputElement>('#chat-field-field-order-number');
    if (input === null) throw new Error('missing input');
    input.value = 'DEMO-2001';
    input.dispatchEvent(new Event('input'));
    link?.click();
    expect(leaveRequests).toBe(1);
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('hides the activity link for anonymous visitors (#171)', () => {
    const { fixture, host } = render();
    fixture.componentRef.setInput('showActivityLink', false);
    fixture.detectChanges();
    expect(host.querySelector('.withdraw-hint')).toBeNull();
  });
});
