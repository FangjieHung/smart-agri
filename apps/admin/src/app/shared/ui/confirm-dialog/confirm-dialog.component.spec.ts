import { Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ConfirmDialogComponent, type ConfirmTone } from './confirm-dialog.component';

@Component({
  imports: [ConfirmDialogComponent],
  template: `
    <button type="button" class="trigger">開啟</button>
    @if (open()) {
      <app-confirm-dialog
        name="withdraw"
        heading="撤回這筆資料？"
        detail="這個動作無法復原。"
        [headingLevel]="headingLevel()"
        [cancelLabel]="cancelLabel()"
        confirmLabel="撤回"
        confirmClass="confirm-withdraw"
        [confirmTone]="tone()"
        [busy]="busy()"
        (cancelled)="events.push('cancelled')"
        (confirmed)="events.push('confirmed')"
      >
        <p class="projected" role="alert">額外內容</p>
      </app-confirm-dialog>
    }
  `,
})
class HostComponent {
  readonly open = signal(false);
  readonly headingLevel = signal<2 | 3>(2);
  readonly cancelLabel = signal('取消');
  readonly busy = signal(false);
  readonly tone = signal<ConfirmTone | undefined>(undefined);
  readonly events: string[] = [];
}

async function setup(): Promise<{ fixture: ComponentFixture<HostComponent>; host: HTMLElement }> {
  TestBed.configureTestingModule({ imports: [HostComponent] });
  const fixture = TestBed.createComponent(HostComponent);
  document.body.appendChild(fixture.nativeElement);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

async function open(fixture: ComponentFixture<HostComponent>): Promise<HTMLElement> {
  fixture.componentInstance.open.set(true);
  fixture.detectChanges();
  await fixture.whenStable();
  const dialog = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[role="dialog"]');
  if (dialog === null) throw new Error('dialog not rendered');
  return dialog;
}

describe('ConfirmDialogComponent', () => {
  afterEach(() => document.body.querySelectorAll('ng-component').forEach((node) => node.remove()));

  it('is a modal dialog named by its title and described by its detail', async () => {
    const { fixture, host } = await setup();
    const dialog = await open(fixture);

    expect(dialog.classList).toContain('confirm');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-labelledby')).toBe('withdraw-confirm-title');
    expect(dialog.getAttribute('aria-describedby')).toBe('withdraw-confirm-detail');
    expect(host.querySelector('h2.confirm-title#withdraw-confirm-title')?.textContent).toBe('撤回這筆資料？');
    expect(host.querySelector('p.confirm-detail#withdraw-confirm-detail')?.textContent).toBe('這個動作無法復原。');
    expect(host.querySelector('.confirm-backdrop')).not.toBeNull();
  });

  it('keeps the existing DOM order: title, detail, projected content, then the actions', async () => {
    const { fixture } = await setup();
    const dialog = await open(fixture);

    expect([...dialog.children].map((child) => child.className)).toEqual([
      'confirm-title',
      'confirm-detail',
      'projected',
      'confirm-actions',
    ]);
    const buttons = dialog.querySelectorAll<HTMLButtonElement>('.confirm-actions button');
    expect([...buttons].map((button) => button.textContent)).toEqual(['取消', '撤回']);
    expect(buttons[0].classList).toContain('confirm-cancel');
    expect(buttons[1].classList).toContain('confirm-withdraw');
  });

  it('moves focus to cancel when it opens and traps focus inside the dialog', async () => {
    const { fixture, host } = await setup();
    host.querySelector<HTMLButtonElement>('.trigger')?.focus();
    const dialog = await open(fixture);

    expect(document.activeElement).toBe(host.querySelector('button.confirm-cancel'));
    // CDK 焦點鎖定的兩個錨點緊貼在對話框前後。
    const anchors = host.querySelectorAll('.cdk-focus-trap-anchor');
    expect(anchors.length).toBe(2);
    expect(dialog.previousElementSibling).toBe(anchors[0]);
    expect(dialog.nextElementSibling).toBe(anchors[1]);
  });

  it('treats Escape and the cancel button as cancel, and the other button as confirm', async () => {
    const { fixture, host } = await setup();
    const dialog = await open(fixture);
    const events = fixture.componentInstance.events;

    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(events).toEqual(['cancelled']);

    host.querySelector<HTMLButtonElement>('button.confirm-cancel')?.click();
    host.querySelector<HTMLButtonElement>('button.confirm-withdraw')?.click();
    expect(events).toEqual(['cancelled', 'cancelled', 'confirmed']);
  });

  it('disables both buttons while busy and renders the chosen heading level and cancel label', async () => {
    const { fixture, host } = await setup();
    fixture.componentInstance.busy.set(true);
    fixture.componentInstance.headingLevel.set(3);
    fixture.componentInstance.cancelLabel.set('繼續填寫');
    await open(fixture);

    const buttons = host.querySelectorAll<HTMLButtonElement>('.confirm-actions button');
    expect([...buttons].every((button) => button.disabled)).toBe(true);
    expect(host.querySelector('h3.confirm-title#withdraw-confirm-title')).not.toBeNull();
    expect(host.querySelector('h2.confirm-title')).toBeNull();
    expect(host.querySelector('button.confirm-cancel')?.textContent).toBe('繼續填寫');
  });

  // issue #261：危險／強調的外觀由這個元件提供（以 data-tone 選取），保留使用者給的 class 當 Cypress 選擇器。
  it('marks the confirm button with its tone and keeps the caller’s class', async () => {
    const { fixture, host } = await setup();
    await open(fixture);
    const confirm = (): HTMLButtonElement | null => host.querySelector<HTMLButtonElement>('.confirm-actions button:not(.confirm-cancel)');
    expect(confirm()?.hasAttribute('data-tone')).toBe(false);

    for (const tone of ['danger', 'accent'] as const) {
      fixture.componentInstance.tone.set(tone);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(confirm()?.dataset['tone']).toBe(tone);
      expect(confirm()?.classList).toContain('confirm-withdraw');
    }
    expect(host.querySelector('button.confirm-cancel')?.hasAttribute('data-tone')).toBe(false);
  });
});
