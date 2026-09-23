import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AnswerRulesFormComponent } from '../../../components/answer-rules-form/answer-rules-form.component';
import { AssistantDraftStore } from '../../assistant-draft.store';

@Component({
  selector: 'app-rules-step',
  imports: [AnswerRulesFormComponent],
  templateUrl: './rules-step.component.html',
  styleUrl: '../../../components/assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RulesStepComponent {
  protected readonly store = inject(AssistantDraftStore);

  /** 只能寫入已連接到這個助理的資料庫。 */
  protected readonly writableDatabases = computed(() =>
    this.store.connectedSources().flatMap((source) =>
      source.type === 'database' ? [{ id: source.id, name: source.name }] : [],
    ),
  );

  protected readonly errors = computed(() => ({
    refusalMessage: this.store.fieldError('rules', 'refusalMessage'),
    dataWritePurpose: this.store.fieldError('rules', 'dataWritePurpose'),
  }));
}
