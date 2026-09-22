import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  signal,
  viewChild,
  type AfterViewInit,
} from '@angular/core';
import type { ChatConsentView } from '../../../core/domain/conversation.model';
import type { DatabaseRecordEntryView } from '../../../core/domain/database.model';

/** 送出前的同意確認：清楚說明接收單位、用途、可查看者與敏感資料提示，未勾選同意不能送出。 */
@Component({
  selector: 'app-consent-confirmation',
  templateUrl: './consent-confirmation.component.html',
  styleUrl: './consent-confirmation.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConsentConfirmationComponent implements AfterViewInit {
  readonly consent = input.required<ChatConsentView>();
  readonly entries = input.required<readonly DatabaseRecordEntryView[]>();
  readonly error = input('');

  readonly confirmed = output<void>();
  readonly back = output<void>();

  protected readonly agreed = signal(false);
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  ngAfterViewInit(): void {
    this.heading().nativeElement.focus();
  }

  protected toggle(event: Event): void {
    this.agreed.set((event.target as HTMLInputElement).checked);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.agreed()) this.confirmed.emit();
  }
}
