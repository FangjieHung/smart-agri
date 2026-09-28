import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  KNOWLEDGE_RETRIEVAL_BELOW_THRESHOLD_MESSAGE,
  KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH,
  KNOWLEDGE_RETRIEVAL_QUESTION_REQUIRED_MESSAGE,
  KNOWLEDGE_VERSION_STATE_LABELS,
  type KnowledgeBaseId,
} from '../../../../core/domain/knowledge-base.model';
import type { PreviewKnowledgeRetrievalResult } from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';

/** `idle`：還沒送出過任何問題；`error`：讀取以外的錯誤（5xx、連線中斷）。 */
type RetrievalView = { readonly status: 'idle' } | { readonly status: 'error' } | PreviewKnowledgeRetrievalResult;

/**
 * 檢索試查（issue #48，M2 Slice 14）：知識庫詳情頁的「試查」頁籤。任何有權開啟這個
 * 知識庫的人都可以使用——權限完全沿用詳情頁本身，這裡不重複檢查。
 */
@Component({
  selector: 'app-knowledge-retrieval-panel',
  imports: [],
  templateUrl: './retrieval-preview-panel.component.html',
  styleUrl: './retrieval-preview-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RetrievalPreviewPanelComponent {
  readonly knowledgeBaseId = input.required<KnowledgeBaseId>();

  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly question = signal('');
  protected readonly includePending = signal(false);
  /** 問題空白時的前端擋（後端一定會再驗一次）。 */
  protected readonly clientError = signal('');
  protected readonly view = signal<RetrievalView>({ status: 'idle' });

  protected readonly versionStateLabels = KNOWLEDGE_VERSION_STATE_LABELS;
  protected readonly belowThresholdMessage = KNOWLEDGE_RETRIEVAL_BELOW_THRESHOLD_MESSAGE;
  protected readonly questionMaxLength = KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH;

  protected setQuestion(value: string): void {
    this.question.set(value);
  }

  protected toggleIncludePending(): void {
    this.includePending.update((value) => !value);
  }

  /** 送出中防重複：`loading` 期間再次送出直接忽略。 */
  protected submit(event: Event): void {
    event.preventDefault();
    if (this.view().status === 'loading') return;

    const question = this.question().trim();
    if (question.length === 0) {
      this.clientError.set(KNOWLEDGE_RETRIEVAL_QUESTION_REQUIRED_MESSAGE);
      return;
    }

    this.clientError.set('');
    this.view.set({ status: 'loading' });
    this.repository
      .previewKnowledgeRetrieval(this.knowledgeBaseId(), question, this.includePending())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.view.set(result),
        error: () => this.view.set({ status: 'error' }),
      });
  }

  protected formatScore(score: number): string {
    return score.toFixed(2);
  }
}
