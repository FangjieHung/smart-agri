import { ChangeDetectionStrategy, Component, ElementRef, computed, input, model, output, viewChild, type AfterViewInit } from '@angular/core';
import { QUESTION_MAX_LENGTH } from './widget.model';

const WARN_REMAINING = 200;

/** 輸入問題：Enter 送出、Shift + Enter 換行（輸入法組字中的 Enter 不送出）。 */
@Component({
  selector: 'app-composer',
  templateUrl: './composer.component.html',
  styleUrl: './composer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ComposerComponent implements AfterViewInit {
  readonly value = model('');
  /** 目前不能送出（回答中、倒數中）；輸入框本身仍可輸入，焦點才不會在送出後掉走。 */
  readonly sendDisabled = input(false);
  readonly submitted = output<string>();

  protected readonly max = QUESTION_MAX_LENGTH;
  protected readonly remaining = computed(() => this.max - this.value().length);
  protected readonly showCount = computed(() => this.remaining() <= WARN_REMAINING);
  protected readonly canSend = computed(() => !this.sendDisabled() && this.value().trim() !== '');

  private readonly field = viewChild.required<ElementRef<HTMLTextAreaElement>>('field');

  ngAfterViewInit(): void {
    // 手機上一開啟就跳出軟鍵盤會擋住畫面，只在有精確指標（桌機）時自動聚焦。
    const fine = globalThis.matchMedia?.('(pointer: fine)').matches ?? true;
    if (fine) this.focus();
  }

  focus(): void {
    this.field().nativeElement.focus();
  }

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLTextAreaElement).value);
  }

  protected onEnter(event: Event): void {
    if ((event as KeyboardEvent).isComposing) return;
    event.preventDefault();
    this.send();
  }

  protected send(event?: Event): void {
    event?.preventDefault();
    if (!this.canSend()) return;
    this.submitted.emit(this.value().trim());
  }
}
