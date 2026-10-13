import { TestBed } from '@angular/core/testing';
import { syncValue } from '../../../core/repositories/sync-value.testing';
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
    expect(host.textContent).toContain('/chat/assistant-customer-service');
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

  it('labels a deactivated account it is still shared with, and drops the label once removed (issue #363)', () => {
    const { fixture, host, repository } = render();
    syncValue(repository.deactivateMember('account-external-customer', null));
    fixture.componentRef.setInput('view', publishingOf(repository, 'assistant-customer-service').platform);
    fixture.detectChanges();

    const external = host.querySelector('label[for="platform-account-account-external-customer"]');
    expect(external?.querySelector('.choice__tag')?.textContent?.trim()).toBe('已停用');
    expect(external?.textContent).toContain('取消勾選並儲存，就會從名單移除');
    expect(host.querySelector('label[for="platform-account-account-internal-employee"]')?.textContent).not.toContain('已停用');
    expect(host.querySelector<HTMLInputElement>('#platform-account-account-external-customer')?.checked).toBe(true);

    host.querySelector<HTMLInputElement>('#platform-account-account-external-customer')?.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    fixture.detectChanges();
    const feedback = host.querySelector('[aria-live="polite"]')?.textContent ?? '';
    expect(feedback).toContain('已從分享名單移除 1 個停用的帳號。');
    expect(feedback).not.toContain('重新勾選就會回來');

    // The page reloads the publishing settings after a save (`changed`).
    fixture.componentRef.setInput('view', publishingOf(repository, 'assistant-customer-service').platform);
    fixture.detectChanges();
    expect(host.querySelector('#platform-account-account-external-customer')).toBeNull();
    expect(host.textContent).not.toContain('已停用');
  });
});
