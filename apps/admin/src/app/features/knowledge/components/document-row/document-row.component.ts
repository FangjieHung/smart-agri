import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { fmtDateTime } from '../../../../core/date-utils';
import {
  isRetryableKnowledgeDocument,
  needsKnowledgeAttention,
  type KnowledgeDocumentView,
} from '../../../../core/domain/knowledge-base.model';
import { DOCUMENT_STATUS_LABELS, ITEM_KIND_LABELS } from '../knowledge-labels';

@Component({
  selector: 'app-document-row',
  templateUrl: './document-row.component.html',
  styleUrl: './document-row.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentRowComponent {
  readonly document = input.required<KnowledgeDocumentView>();
  readonly canManage = input(true);
  /** 這一列有請求進行中（重新處理或刪除）：按鈕停用，避免重複送出。 */
  readonly busy = input(false);
  /** 帶出整筆文件：重新處理要用它的 `latestVersionId`。 */
  readonly retry = output<KnowledgeDocumentView>();
  readonly remove = output<KnowledgeDocumentView>();

  protected readonly status = computed(() => DOCUMENT_STATUS_LABELS[this.document().status]);
  protected readonly kindLabel = computed(() => ITEM_KIND_LABELS[this.document().kind]);
  protected readonly needsAttention = computed(() => needsKnowledgeAttention(this.document().status));
  /** 只有處理失敗的可以重新處理；部分內容無法讀取的再處理一次，結果也一樣。 */
  protected readonly canRetry = computed(
    () => this.canManage() && isRetryableKnowledgeDocument(this.document().status),
  );
  protected readonly updatedAt = computed(() => fmtDateTime(this.document().updatedAt));
}
