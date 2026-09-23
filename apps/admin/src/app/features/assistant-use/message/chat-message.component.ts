import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type {
  ChatCitationView,
  ChatFormView,
  ChatMessageView,
  ChatReplyKind,
} from '../../../core/domain/conversation.model';
import type { DatabaseRecordId } from '../../../core/domain/database.model';

export interface CitationRequest {
  readonly citations: readonly ChatCitationView[];
  readonly trigger: HTMLElement;
}

/** 撤回是破壞性動作，所以只送出請求，由外層頁面負責確認對話框與焦點。 */
export interface WithdrawRequest {
  readonly recordId: DatabaseRecordId;
  readonly trigger: HTMLElement;
}

/** 三種回答狀態使用不同標示，公司資料、一般知識與查無資料不混寫。 */
const REPLY_LABELS: Readonly<Record<ChatReplyKind, string>> = {
  'company-data': '根據你的資料',
  'general-knowledge': '一般知識補充',
  'no-result': '查無資料',
  'form-request': '需要填寫資料',
  'submission-receipt': '資料已送出',
};

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

  readonly openCitations = output<CitationRequest>();
  readonly startForm = output<ChatFormView>();
  readonly withdrawSubmission = output<WithdrawRequest>();

  protected label(kind: ChatReplyKind): string {
    return REPLY_LABELS[kind];
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
