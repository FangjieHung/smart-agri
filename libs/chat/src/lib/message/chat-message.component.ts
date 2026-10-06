import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  REPLY_KIND_LABELS,
  type ChatCitationView,
  type ChatFormView,
  type ChatMessageView,
  type ChatReplyKind,
  type DatabaseRecordId,
} from '../chat-view.model';

export interface CitationRequest {
  readonly citations: readonly ChatCitationView[];
  readonly trigger: HTMLElement;
}

/** 撤回是破壞性動作，所以只送出請求，由外層頁面負責確認對話框與焦點。 */
export interface WithdrawRequest {
  readonly recordId: DatabaseRecordId;
  readonly trigger: HTMLElement;
}

@Component({
  selector: 'app-chat-message',
  templateUrl: './chat-message.component.html',
  styleUrl: './chat-message.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatMessageComponent {
  readonly message = input.required<ChatMessageView>();
  /** 此訊息的表單已在下方開啟時，隱藏開始按鈕。 */
  readonly formActive = input(false);
  /** 使用者已用「不用了」或 × 關閉這則訊息開啟的表單（issue #171）：訊息改成已關閉的說明。 */
  readonly formDismissed = input(false);

  readonly openCitations = output<CitationRequest>();
  readonly startForm = output<ChatFormView>();
  readonly withdrawSubmission = output<WithdrawRequest>();

  protected label(kind: ChatReplyKind): string {
    return REPLY_KIND_LABELS[kind];
  }

  protected showCitations(citations: readonly ChatCitationView[], event: Event): void {
    this.openCitations.emit({ citations, trigger: event.currentTarget as HTMLElement });
  }

  /** 指認不到紀錄的舊收據沒有按鈕，這裡再擋一次，不讓 null 進到 repository。 */
  protected requestWithdrawal(recordId: DatabaseRecordId | null, event: Event): void {
    if (recordId === null) return;
    this.withdrawSubmission.emit({ recordId, trigger: event.currentTarget as HTMLElement });
  }
}
