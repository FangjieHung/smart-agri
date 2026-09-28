import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { REPLY_KIND_LABELS, type ChatReplyKind } from '../../../../../core/domain/conversation.model';
import type { AssistantAudience } from '../../../../../core/domain/assistant.model';
import { AssistantDraftStore } from '../../assistant-draft.store';

const AUDIENCE_LABELS: Record<AssistantAudience, string> = {
  'account-members': '內部員工',
  'authorized-external-customers': '外部客戶',
  'members-and-external-customers': '內部員工與外部客戶',
};

/**
 * 精靈的試問（issue #82，M3 計畫 Slice 12）：與正式對話用同一套回覆類型（`ChatReplyView`
 * 的子集），問題可以自由輸入，固定題組保留作為建議按鈕；另外顯示檢索到的段落、分數與門檻，
 * 讓建立者判斷門檻是否合適。
 */
@Component({
  selector: 'app-test-step',
  templateUrl: './test-step.component.html',
  styleUrls: ['../../../components/assistant-form.scss', '../wizard-step.scss', './test-step.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TestStepComponent {
  protected readonly store = inject(AssistantDraftStore);

  /** 自由輸入的問題；不屬於草稿內容，只存在這個畫面。 */
  protected readonly questionDraft = signal('');

  protected readonly summary = computed(() => {
    const draft = this.store.draft();
    const connected = this.store.connectedSources();
    const knowledge = connected.filter((source) => source.type === 'knowledge-base').length;
    return {
      name: draft.name,
      audience: draft.audience === null ? '尚未選擇' : AUDIENCE_LABELS[draft.audience],
      sources: `${knowledge} 個知識庫、${connected.length - knowledge} 個資料庫`,
      scope:
        draft.rules.knowledgeScope === 'company-data-only'
          ? '只依據我的資料回答'
          : '允許補充一般知識（分區標示）',
    };
  });

  protected answerLabel(kind: ChatReplyKind): string {
    return REPLY_KIND_LABELS[kind];
  }

  protected formatScore(value: number): string {
    return value.toFixed(2);
  }

  /** 點選建議問題：直接用固定題組的文字試問，不需要先填進輸入框。 */
  protected askSuggested(text: string): void {
    void this.store.runTrial(text);
  }

  /**
   * 只有成功取得回覆時才清空輸入框（issue #114）：`runTrial` 失敗（422／503／網路錯誤）
   * 回傳 `null`，這時保留使用者輸入的內容，讓使用者不用重打一次問題。
   */
  protected submitQuestion(event: Event): void {
    event.preventDefault();
    const question = this.questionDraft().trim();
    if (question === '' || this.store.trialing()) return;
    void this.store.runTrial(question).then((result) => {
      if (result !== null) this.questionDraft.set('');
    });
  }

  protected onQuestionInput(event: Event): void {
    this.questionDraft.set((event.target as HTMLInputElement).value);
  }
}
