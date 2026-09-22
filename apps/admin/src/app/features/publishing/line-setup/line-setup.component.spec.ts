import { TestBed } from '@angular/core/testing';
import { providePublishingTesting, publishingOf } from '../publishing.testing';
import { LineSetupComponent } from './line-setup.component';

const VALID_TOKEN = 'demo-token-not-for-production-0123456789abcdefghij';

function render() {
  const { providers, repository } = providePublishingTesting();
  TestBed.configureTestingModule({ imports: [LineSetupComponent], providers });
  const fixture = TestBed.createComponent(LineSetupComponent);
  const refresh = () => {
    fixture.componentRef.setInput('view', publishingOf(repository, 'assistant-customer-service').line);
    fixture.detectChanges();
  };
  fixture.componentRef.setInput('assistantId', 'assistant-customer-service');
  fixture.componentInstance.changed.subscribe(refresh);
  refresh();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function button(host: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(host.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(text));
  if (!found) throw new Error(`button ${text} not found`);
  return found;
}

describe('LineSetupComponent', () => {
  it('masks the secret and token by default and reveals them with a labelled toggle', () => {
    const { fixture, host } = render();
    const secret = host.querySelector<HTMLInputElement>('#line-channelSecret');
    const token = host.querySelector<HTMLInputElement>('#line-accessToken');

    expect(secret?.type).toBe('password');
    expect(token?.type).toBe('password');
    expect(host.querySelector<HTMLInputElement>('#line-channelId')?.type).toBe('text');

    const toggle = host.querySelector<HTMLButtonElement>('button[aria-controls="line-channelSecret"]');
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');
    toggle?.click();
    fixture.detectChanges();
    expect(secret?.type).toBe('text');
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');
    expect(token?.type).toBe('password');
  });

  it('shows the field checklist with the failing token and an error summary', () => {
    const { host } = render();

    const items = Array.from(host.querySelectorAll('.checklist li'));
    expect(items).toHaveLength(4);
    const token = items.find((item) => item.textContent?.includes('Channel access token'));
    expect(token?.getAttribute('data-state')).toBe('failed');
    expect(token?.textContent).toContain('未通過');
    expect(host.querySelector('.error-summary[role="alert"] a')?.getAttribute('href')).toBe('#line-accessToken');
    expect(host.querySelector('#line-accessToken')?.getAttribute('aria-invalid')).toBe('true');
    expect(button(host, '傳送測試訊息').disabled).toBe(true);
  });

  it('re-checks fields, sends a simulated test message announced in a live region and enables the channel', () => {
    const { fixture, host } = render();
    const token = host.querySelector<HTMLInputElement>('#line-accessToken');
    if (!token) throw new Error('token input missing');
    token.value = VALID_TOKEN;
    token.dispatchEvent(new Event('input'));
    button(host, '儲存並檢查').click();
    fixture.detectChanges();

    expect(Array.from(host.querySelectorAll('.checklist li')).every((item) => item.getAttribute('data-state') === 'passed')).toBe(true);
    expect(host.querySelector('.error-summary')).toBeNull();

    button(host, '傳送測試訊息').click();
    fixture.detectChanges();
    const live = host.querySelector('.test-result[aria-live="polite"]');
    expect(live?.textContent).toContain('測試訊息已送達');
    expect(live?.textContent).toContain('模擬');

    button(host, '確認啟用').click();
    fixture.detectChanges();
    expect(host.textContent).toContain('LINE 管道已啟用');
  });
});
