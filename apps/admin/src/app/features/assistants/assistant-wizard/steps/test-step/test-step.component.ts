import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { TrialAnswerView } from '../../../../../core/domain/assistant-draft.model';
import type { AssistantAudience } from '../../../../../core/domain/assistant.model';
import { AssistantDraftStore } from '../../assistant-draft.store';

const ANSWER_LABELS: Record<TrialAnswerView['kind'], string> = {
  'company-data': '根據你的資料',
  'general-knowledge': '一般知識補充',
  'no-answer': '資料中沒有答案',
};

const AUDIENCE_LABELS: Record<AssistantAudience, string> = {
  'account-members': '內部員工',
  'authorized-external-customers': '外部客戶',
  'members-and-external-customers': '內部員工與外部客戶',
};

@Component({
  selector: 'app-test-step',
  templateUrl: './test-step.component.html',
  styleUrls: ['../../../components/assistant-form.scss', '../wizard-step.scss', './test-step.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TestStepComponent {
  protected readonly store = inject(AssistantDraftStore);

  protected readonly questionText = computed(
    () => new Map(this.store.trialQuestions().map((question) => [question.id, question.text])),
  );

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

  protected answerLabel(kind: TrialAnswerView['kind']): string {
    return ANSWER_LABELS[kind];
  }
}
