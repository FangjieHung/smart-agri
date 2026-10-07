import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { AssistantId } from '../../../core/domain/assistant.model';
import type { ChatThreadSummaryView } from '../../../core/domain/conversation.model';

export interface RecentChatThreadView extends ChatThreadSummaryView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
}

/**
 * 側欄的「Chats」：目前帳號最近的對話（跨助理），只顯示標題與助理名稱。
 * 原本是 `ConversationRailComponent` 的一個模式；側欄在初始 bundle，獨立出來後，對話頁才用到的
 * 改名、刪除與確認框不再跟著進入初始載入（issue #308）。標記與 class 名稱維持原樣。
 */
@Component({
  selector: 'app-recent-chats',
  templateUrl: './recent-chats.component.html',
  styleUrl: './recent-chats.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecentChatsComponent {
  readonly threads = input.required<readonly RecentChatThreadView[]>();
  readonly selectThread = output<RecentChatThreadView>();
}
