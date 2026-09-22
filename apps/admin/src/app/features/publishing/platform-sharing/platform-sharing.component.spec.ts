import { TestBed } from '@angular/core/testing';
import { providePublishingTesting, publishingOf } from '../publishing.testing';
import { PlatformSharingComponent } from './platform-sharing.component';

function render() {
  const { providers, repository } = providePublishingTesting();
  TestBed.configureTestingModule({ imports: [PlatformSharingComponent], providers });
  const fixture = TestBed.createComponent(PlatformSharingComponent);
  fixture.componentRef.setInput('assistantId', 'assistant-customer-service');
  fixture.componentRef.setInput('view', publishingOf(repository, 'assistant-customer-service').platform);
  fixture.detectChanges();
  let changed = 0;
  fixture.componentInstance.changed.subscribe(() => (changed += 1));
  return { fixture, host: fixture.nativeElement as HTMLElement, repository, changes: () => changed };
}

describe('PlatformSharingComponent', () => {
  it('lists who may use the assistant as labelled checkboxes and explains conversation privacy', () => {
    const { host } = render();

    expect(host.querySelector('fieldset legend')?.textContent).toContain('可使用的帳號');
    const boxes = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
    expect(boxes).toHaveLength(2);
    expect(boxes.every((box) => box.checked)).toBe(true);
    expect(host.querySelector(`label[for="${boxes[0].id}"]`)?.textContent).toContain('安心商行客服同仁');
    expect(host.textContent).toContain('/use/assistant-customer-service');
    expect(host.textContent).toContain('獨立對話紀錄');
    expect(host.textContent).toContain('不能查看設定');
  });

  it('saves the account restriction and announces the result', () => {
    const { fixture, host, repository, changes } = render();
    const external = host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[1];
    external.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    fixture.detectChanges();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已更新可使用的帳號');
    expect(changes()).toBe(1);
    expect(publishingOf(repository, 'assistant-customer-service').platform.allowedAccountIds).toEqual([
      'account-internal-employee',
    ]);
  });
});
