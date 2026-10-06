import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';

/**
 * 共用的確認對話框（M7-8）：背景遮罩＋`role="dialog"`／`aria-modal`、以標題與說明命名（`aria-labelledby`／
 * `aria-describedby`）、CDK 焦點鎖定、Escape 等同「取消」，出現時焦點先落在「取消」。
 *
 * 只負責對話框本身；開關與關閉後把焦點還給觸發按鈕，仍由使用的元件決定（它知道觸發者是誰）。
 * 額外內容（例如轉交預覽、錯誤訊息）以 content projection 放在說明與按鈕之間。
 * class 名稱與 DOM 結構沿用原本三處各寫一份的標記（`.confirm`、`.confirm-title`、`.confirm-cancel`…），
 * 既有的 Cypress 與單元測試選擇器照樣命中。
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [A11yModule],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialogComponent {
  /** 元素 id 的前綴：標題是 `<name>-confirm-title`，說明是 `<name>-confirm-detail`。 */
  readonly name = input.required<string>();
  readonly heading = input.required<string>();
  readonly detail = input.required<string>();
  /** 標題的層級，配合所在頁面的標題結構。 */
  readonly headingLevel = input<2 | 3>(2);
  readonly cancelLabel = input('取消');
  readonly confirmLabel = input.required<string>();
  /** 加在確認按鈕上的 class（既有選擇器，例如 `confirm-withdraw`；外觀規則由使用的元件或全域樣式決定）。 */
  readonly confirmClass = input('');
  /** 送出中：兩個按鈕都停用（Escape 仍會發出 `cancelled`，由使用的元件決定要不要忽略）。 */
  readonly busy = input(false);

  /** 按「取消」或 Escape。 */
  readonly cancelled = output<void>();
  readonly confirmed = output<void>();

  private readonly cancelButton = viewChild.required<ElementRef<HTMLButtonElement>>('cancelButton');

  constructor() {
    // 對話框一出現就把焦點帶到「取消」：鍵盤使用者不會誤按不可復原的動作。
    effect(() => this.cancelButton().nativeElement.focus());
  }
}
