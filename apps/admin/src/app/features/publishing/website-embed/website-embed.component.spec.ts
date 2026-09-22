import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { providePublishingTesting, publishingOf } from '../publishing.testing';
import { WebsiteEmbedComponent } from './website-embed.component';

function render() {
  const { providers, repository } = providePublishingTesting();
  TestBed.configureTestingModule({ imports: [WebsiteEmbedComponent], providers });
  const fixture = TestBed.createComponent(WebsiteEmbedComponent);
  fixture.componentRef.setInput('assistantId', 'assistant-customer-service');
  fixture.componentRef.setInput('view', publishingOf(repository, 'assistant-customer-service').website);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository };
}

function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(host.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(text));
  if (!found) throw new Error(`button ${text} not found`);
  return found;
}

describe('WebsiteEmbedComponent', () => {
  it('toggles between desktop and mobile previews with pressed-state buttons', () => {
    const { fixture, host } = render();
    const preview = () => host.querySelector('.embed-preview');

    expect(preview()?.getAttribute('data-device')).toBe('desktop');
    expect(button(host, '桌機').getAttribute('aria-pressed')).toBe('true');
    button(host, '手機').click();
    fixture.detectChanges();

    expect(preview()?.getAttribute('data-device')).toBe('mobile');
    expect(button(host, '手機').getAttribute('aria-pressed')).toBe('true');
    expect(preview()?.textContent).toContain('安心商行線上客服');
  });

  it('validates a new allowed domain before adding it to the list', () => {
    const { fixture, host } = render();
    const input = host.querySelector<HTMLInputElement>('#website-domain-input');
    if (!input) throw new Error('domain input missing');

    type(input, 'https://shop.example.com/about');
    button(host, '加入網域').click();
    fixture.detectChanges();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector('#website-domain-error')?.textContent).toContain('不要包含 https://');

    type(input, 'blog.anxin-demo.example');
    button(host, '加入網域').click();
    fixture.detectChanges();
    const domains = Array.from(host.querySelectorAll('.domain-list li'), (item) => item.textContent ?? '');
    expect(domains.some((text) => text.includes('blog.anxin-demo.example'))).toBe(true);
    expect(input.getAttribute('aria-invalid')).toBeNull();
  });

  it('shows an error summary when required settings are missing on save', () => {
    const { fixture, host } = render();
    const name = host.querySelector<HTMLInputElement>('#website-display-name');
    if (!name) throw new Error('name input missing');
    type(name, '');
    button(host, '儲存官網設定').click();
    fixture.detectChanges();

    const summary = host.querySelector('.error-summary[role="alert"]');
    expect(summary?.textContent).toContain('顯示名稱');
    expect(summary?.querySelector('a')?.getAttribute('href')).toBe('#website-display-name');
    expect(name.getAttribute('aria-invalid')).toBe('true');
  });

  it('marks the embed code as demo-only and copies it with an announced result', async () => {
    const { fixture, host } = render();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    expect(host.querySelector('.embed-code')?.textContent).toContain('不可用於正式環境');
    button(host, '複製嵌入碼').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('data-demo-only="true"'));
    expect(host.querySelector('.copy-status[aria-live="polite"]')?.textContent).toContain('已複製');
  });
});
