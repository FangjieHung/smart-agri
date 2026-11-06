import { Injectable, signal } from '@angular/core';

/**
 * 給螢幕報讀器的即時通知（購物車、金額、訂單狀態的變化）。App 根元件把 {@link message} 放在
 * `aria-live="polite"` 的區塊裡；同一句話連續通知時先清空再寫入，報讀器才會再念一次。
 */
@Injectable({ providedIn: 'root' })
export class Announcer {
  readonly message = signal('');
  private timer: ReturnType<typeof setTimeout> | undefined;

  announce(text: string): void {
    clearTimeout(this.timer);
    this.message.set('');
    this.timer = setTimeout(() => this.message.set(text), 60);
  }
}
