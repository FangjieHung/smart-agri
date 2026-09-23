import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AnswerRulesFormComponent } from '../../../components/answer-rules-form/answer-rules-form.component';
import { AssistantSettingsStore } from '../../assistant-settings.store';

/**
 * 建立後的「回答與記錄」：和建立精靈步驟三同一組規則，差別在於這裡的說明文字講的是
 * 對「已經存在的對話」的影響，而不是對之後才會發生的對話。
 */
@Component({
  selector: 'app-assistant-rules-tab',
  imports: [AnswerRulesFormComponent],
  templateUrl: './assistant-rules-tab.component.html',
  styleUrl: '../../../components/assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantRulesTabComponent {
  protected readonly store = inject(AssistantSettingsStore);

  protected readonly errors = computed(() => ({
    refusalMessage: this.store.fieldError('refusalMessage'),
    dataWritePurpose: this.store.fieldError('dataWritePurpose'),
  }));
}
