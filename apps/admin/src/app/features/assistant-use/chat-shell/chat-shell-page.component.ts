import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ChatConversationComponent } from '../conversation/chat-conversation.component';

/**
 * 終端使用者的助理對話頁（`/use/:assistantId`）：單欄、沒有對話紀錄側欄，
 * 因為這個網址會被嵌入客戶官網、也會從 LINE 開啟，不能出現工作區外框。
 * `?embed=1` 會再收起頁首與返回連結，只留下對話本身——這是匿名訪客的主要入口。
 *
 * 這一頁**不需要 Demo 身分**：沒有選過身分的人是未登入的官網訪客，由
 * `embeddedChatGuard` 發給這個分頁一個匿名訪客 id（`allowAnonymous`），
 * 並由 repository 決定這個助理有沒有對外發布。
 */
@Component({
  selector: 'app-chat-shell-page',
  imports: [ChatConversationComponent],
  template: `
    <app-chat-conversation
      [assistantId]="assistantId()"
      [header]="embedded() ? 'minimal' : 'full'"
      [allowAnonymous]="true"
    />
  `,
  styles: `
    :host { display: block; min-block-size: 100dvh; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatShellPageComponent {
  private readonly route = inject(ActivatedRoute);

  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  private readonly query = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  protected readonly assistantId = computed(() => this.params().get('assistantId') ?? '');
  /** 嵌入模式：供 iframe 使用，隱藏頁首與品牌外框。 */
  protected readonly embedded = computed(() => this.query().get('embed') === '1');
}
