import { TestBed } from '@angular/core/testing';
import { orderForm } from '../assistant-use.testing';
import { ConsentConfirmationComponent } from './consent-confirmation.component';

function render() {
  const form = orderForm();
  TestBed.configureTestingModule({ imports: [ConsentConfirmationComponent] });
  const fixture = TestBed.createComponent(ConsentConfirmationComponent);
  fixture.componentRef.setInput('consent', form.consent);
  fixture.componentRef.setInput('entries', [{ fieldId: 'field-order-number', label: '訂單編號', display: 'DEMO-2001' }]);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('ConsentConfirmationComponent', () => {
  it('shows the recipient, purpose, who can view, the sensitive-data hint and the answers', () => {
    const text = render().host.textContent ?? '';

    expect(text).toContain('接收單位');
    expect(text).toContain('安心商行');
    expect(text).toContain('收集目的');
    expect(text).toContain('收集訂單問題回報');
    expect(text).toContain('可查看者');
    expect(text).toContain('安心商行管理者');
    expect(text).toContain('敏感');
    expect(text).toContain('撤回');
    expect(text).toContain('DEMO-2001');
  });

  it('cannot be submitted until consent is ticked', () => {
    const { fixture, host } = render();
    let confirmed = 0;
    fixture.componentInstance.confirmed.subscribe(() => (confirmed += 1));
    const submit = host.querySelector<HTMLButtonElement>('button.consent-submit');

    expect(submit?.disabled).toBe(true);
    submit?.click();
    expect(confirmed).toBe(0);

    host.querySelector<HTMLInputElement>('#consent-agree')?.click();
    fixture.detectChanges();
    expect(submit?.disabled).toBe(false);
    submit?.click();
    expect(confirmed).toBe(1);
  });
});
