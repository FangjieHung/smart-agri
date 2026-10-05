import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { ChatFormView } from '../../../core/domain/conversation.model';

/**
 * 對話輸入區的「回報資料」固定入口（issue #171 ③）。表單清單由伺服器決定（`listChatForms`），
 * 外層只在清單不為空時放這個元件。只有一份表單時直接開啟；多份時桌面版是 menu（方向鍵移動、
 * Enter 開啟、Esc 關閉並把焦點還給按鈕），`compact`（手機寬度）是底部面板（`role="dialog"`、
 * 焦點鎖在面板內，關閉後還給按鈕）。選項只列表單名稱。
 */
@Component({
  selector: 'app-form-entry',
  imports: [A11yModule],
  templateUrl: './form-entry.component.html',
  styleUrl: './form-entry.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:click)': 'closeOnOutsideClick($event)' },
})
export class FormEntryComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly forms = input.required<readonly ChatFormView[]>();
  /** 手機寬度：按鈕放在輸入框上方一列，多份表單時改用底部面板。 */
  readonly compact = input(false);
  readonly selected = output<ChatFormView>();

  protected readonly open = signal(false);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');

  /** 外層需要把焦點還給入口時使用（例如關閉表單後）。 */
  focus(): void {
    this.trigger().nativeElement.focus();
  }

  protected toggle(): void {
    const forms = this.forms();
    if (forms.length === 1) {
      this.selected.emit(forms[0]);
      return;
    }
    if (this.open()) {
      this.close();
      return;
    }
    this.openAt('first');
  }

  protected onTriggerKeydown(event: KeyboardEvent): void {
    if (this.forms().length < 2 || this.compact()) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.openAt(event.key === 'ArrowDown' ? 'first' : 'last');
    }
  }

  protected onMenuKeydown(event: KeyboardEvent): void {
    const items = this.menuItems();
    const index = items.indexOf(document.activeElement as HTMLElement);
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        items[(index + 1) % items.length]?.focus();
        return;
      case 'ArrowUp':
        event.preventDefault();
        items[(index - 1 + items.length) % items.length]?.focus();
        return;
      case 'Home':
        event.preventDefault();
        items[0]?.focus();
        return;
      case 'End':
        event.preventDefault();
        items[items.length - 1]?.focus();
        return;
      case 'Escape':
        event.preventDefault();
        this.close();
        return;
      case 'Tab':
        // 離開選單：收起，不搶焦點。
        this.open.set(false);
        return;
    }
  }

  protected choose(form: ChatFormView): void {
    this.open.set(false);
    this.selected.emit(form);
  }

  /** Esc、「關閉」或點選單外面：收起並把焦點還給「回報資料」。 */
  protected close(): void {
    this.open.set(false);
    this.focus();
  }

  protected closeOnOutsideClick(event: MouseEvent): void {
    if (!this.open() || this.compact()) return;
    if (!this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }

  private openAt(position: 'first' | 'last'): void {
    this.open.set(true);
    afterNextRender(
      () => {
        // 面板：焦點進到第一個選項（之後由 cdkTrapFocus 鎖在面板內）；選單：第一個或最後一個項目。
        const items = this.compact()
          ? Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('.sheet-options button'))
          : this.menuItems();
        (position === 'first' ? items[0] : items[items.length - 1])?.focus();
      },
      { injector: this.injector },
    );
  }

  private menuItems(): HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[role="menuitem"]'));
  }
}
