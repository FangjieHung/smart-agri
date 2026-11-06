import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  inject,
  input,
  OnDestroy,
  output,
  viewChild,
} from '@angular/core';

/**
 * 底部面板（手機全螢幕或從下方升起，桌機置中）。用原生 `<dialog>` 的 `showModal()`：瀏覽器自己把焦點
 * 鎖在面板裡、讓後面的頁面無法操作、Esc 會觸發 `cancel`。存在期間就是開著：父元件用 `@if` 放進來，
 * 關閉就移除；移除時把焦點還給打開面板前的元素。
 *
 * 開啟後焦點放在有 `data-autofocus` 的元素，沒有就放在面板的標題（`tabindex="-1"`）。
 * Esc 不直接關閉，而是送出 `dismissed`，由父元件決定（例如第二層選單要還原選擇）。
 */
@Component({
  selector: 'app-sheet',
  template: `
    <dialog
      #dialog
      class="sheet"
      [class.sheet--compact]="compact()"
      [attr.aria-labelledby]="labelledBy()"
      (cancel)="onCancel($event)"
    >
      <ng-content />
    </dialog>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Sheet implements OnDestroy {
  /** 面板標題的 id。 */
  readonly labelledBy = input.required<string>();
  /** 小面板（確認、店家資訊）：手機上從下方升起、不滿版。 */
  readonly compact = input(false);
  /** 顧客按 Esc 想關閉面板。 */
  readonly dismissed = output<void>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly document = inject(DOCUMENT);
  private readonly returnFocus = this.document.activeElement as HTMLElement | null;

  constructor() {
    afterNextRender(() => {
      const dialog = this.dialog().nativeElement;
      if (typeof dialog.showModal === 'function') {
        if (!dialog.open) dialog.showModal();
      } else {
        dialog.setAttribute('open', '');
      }
      const target =
        dialog.querySelector<HTMLElement>('[data-autofocus]') ??
        dialog.querySelector<HTMLElement>(`[id="${this.labelledBy()}"]`);
      target?.focus({ preventScroll: true });
    });
  }

  protected onCancel(event: Event): void {
    event.preventDefault();
    this.dismissed.emit();
  }

  ngOnDestroy(): void {
    const dialog = this.dialog().nativeElement;
    if (typeof dialog.close === 'function' && dialog.open) {
      dialog.close();
    }
    // 打開前的元素還在就還給它；面板是一進頁面就自動打開的（焦點原本在 body）時，放到頁面標題。
    const target =
      this.returnFocus &&
      this.returnFocus !== this.document.body &&
      this.document.contains(this.returnFocus)
        ? this.returnFocus
        : this.document.getElementById('page-title');
    target?.focus({ preventScroll: true });
  }
}
