import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import {
  PURGE_ACKNOWLEDGEMENT,
  PURGE_ISSUES_NOTE,
  PurgeConversationsDialogComponent,
} from './purge-conversations-dialog.component';

function render(busy = false) {
  TestBed.configureTestingModule({ imports: [PurgeConversationsDialogComponent] });
  const fixture = TestBed.createComponent(PurgeConversationsDialogComponent);
  fixture.componentRef.setInput('assistantName', '客服助理');
  fixture.componentRef.setInput('threadCount', 12);
  fixture.componentRef.setInput('accountCount', 3);
  fixture.componentRef.setInput('busy', busy);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const confirm = () => host.querySelector<HTMLButtonElement>('.confirm-purge');
  const acknowledge = () => host.querySelector<HTMLInputElement>('#purge-acknowledge');
  return { fixture, host, confirm, acknowledge };
}

describe('PurgeConversationsDialogComponent (issue #242)', () => {
  it('names the assistant, the thread and member counts and the issues note', () => {
    const { host } = render();

    expect(host.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
    expect(host.querySelector('.confirm-title')?.textContent).toBe('立即刪除「客服助理」已保存的對話？');
    const detail = host.querySelector('.confirm-detail')?.textContent ?? '';
    expect(detail).toContain('3 位成員');
    expect(detail).toContain('12 串對話');
    expect(detail).toContain('無法復原');
    expect(detail).toContain('不會收到通知');
    expect(host.querySelector('[data-purge-issues-note]')?.textContent).toBe(PURGE_ISSUES_NOTE);
    expect(PURGE_ISSUES_NOTE).toContain('已轉給專人的問答會保留在處理事項中');
    expect(host.querySelector('label')?.textContent).toContain(PURGE_ACKNOWLEDGEMENT);
    // 不用輸入助理名稱。
    expect(host.querySelector('input[type="text"]')).toBeNull();
  });

  it('cannot delete until 我了解刪除後無法復原 is checked', () => {
    const { fixture, confirm, acknowledge } = render();
    const confirmed = vi.fn();
    fixture.componentInstance.confirmed.subscribe(confirmed);

    expect(confirm()?.disabled).toBe(true);
    confirm()?.click();
    expect(confirmed).not.toHaveBeenCalled();

    const box = acknowledge();
    if (!box) throw new Error('missing checkbox');
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(confirm()?.disabled).toBe(false);
    confirm()?.click();
    expect(confirmed).toHaveBeenCalledTimes(1);

    // 取消勾選後又不能刪除。
    box.checked = false;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(confirm()?.disabled).toBe(true);
  });

  it('starts on 取消, and Escape and 取消 both cancel', () => {
    const { fixture, host } = render();
    const cancelled = vi.fn();
    fixture.componentInstance.cancelled.subscribe(cancelled);

    expect(document.activeElement?.classList.contains('confirm-cancel')).toBe(true);
    host.querySelector('.confirm')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    host.querySelector<HTMLButtonElement>('.confirm-cancel')?.click();
    expect(cancelled).toHaveBeenCalledTimes(2);
  });

  it('disables both buttons and the checkbox while deleting', () => {
    const { host, confirm, acknowledge } = render(true);

    expect(confirm()?.textContent).toBe('刪除中…');
    expect(confirm()?.disabled).toBe(true);
    expect(host.querySelector<HTMLButtonElement>('.confirm-cancel')?.disabled).toBe(true);
    expect(acknowledge()?.disabled).toBe(true);
  });
});
