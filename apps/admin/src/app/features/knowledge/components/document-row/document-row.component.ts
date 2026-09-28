import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { fmtDateTime } from '../../../../core/date-utils';
import {
  isAwaitingApprovalKnowledgeDocument,
  isRetryableKnowledgeDocument,
  knowledgeDocumentInEffect,
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
  /** 詳情頁「批次確認生效」開啟時才顯示核取方塊（issue #47）。 */
  readonly selectable = input(false);
  readonly selected = input(false);
  /** 帶出整筆文件：重新處理要用它的 `latestVersionId`。 */
  readonly retry = output<KnowledgeDocumentView>();
  readonly remove = output<KnowledgeDocumentView>();
  /** 開啟版本與預覽對話框。 */
  readonly openReview = output<KnowledgeDocumentView>();
  readonly toggleSelect = output<KnowledgeDocumentView>();

  protected readonly status = computed(() => DOCUMENT_STATUS_LABELS[this.document().status]);
  protected readonly kindLabel = computed(() => ITEM_KIND_LABELS[this.document().kind]);
  protected readonly needsAttention = computed(() => needsKnowledgeAttention(this.document().status));
  /** 只有處理失敗的可以重新處理；部分內容無法讀取的再處理一次，結果也一樣。 */
  protected readonly canRetry = computed(
    () => this.canManage() && isRetryableKnowledgeDocument(this.document().status),
  );
  protected readonly updatedAt = computed(() => fmtDateTime(this.document().updatedAt));
  /**
   * 「可使用」是處理狀態，不代表助理已經在用它（issue #47）：一律同時顯示是否已生效，
   * 避免混淆——已停用、待確認／排程中、已生效三種各自有清楚的文字。
   */
  protected readonly inEffect = computed(() => knowledgeDocumentInEffect(this.document()));
  protected readonly awaitingApproval = computed(() => isAwaitingApprovalKnowledgeDocument(this.document()));
  protected readonly disabled = computed(() => this.document().disabled === true);
}
