import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { fmtDateTime } from '../../../../core/date-utils';
import {
  needsKnowledgeAttention,
  type KnowledgeDocumentId,
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
  readonly retry = output<KnowledgeDocumentId>();

  protected readonly status = computed(() => DOCUMENT_STATUS_LABELS[this.document().status]);
  protected readonly kindLabel = computed(() => ITEM_KIND_LABELS[this.document().kind]);
  protected readonly canRetry = computed(
    () => this.canManage() && needsKnowledgeAttention(this.document().status),
  );
  protected readonly updatedAt = computed(() => fmtDateTime(this.document().updatedAt));
}
