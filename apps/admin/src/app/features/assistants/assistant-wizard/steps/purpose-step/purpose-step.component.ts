import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import {
  AssistantProfileFormComponent,
  type AssistantProfileChange,
} from '../../../components/assistant-profile-form/assistant-profile-form.component';
import { AssistantDraftStore } from '../../assistant-draft.store';

@Component({
  selector: 'app-purpose-step',
  imports: [AssistantProfileFormComponent],
  templateUrl: './purpose-step.component.html',
  styleUrls: ['../../../components/assistant-form.scss', '../wizard-step.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurposeStepComponent {
  protected readonly store = inject(AssistantDraftStore);

  protected readonly profile = computed(() => {
    const draft = this.store.draft();
    return {
      name: draft.name,
      purpose: draft.purpose,
      tone: draft.tone,
      roleInstructions: draft.roleInstructions,
      audience: draft.audience,
    };
  });

  protected readonly errors = computed(() => ({
    name: this.store.fieldError('purpose', 'name'),
    purpose: this.store.fieldError('purpose', 'purpose'),
    audience: this.store.fieldError('purpose', 'audience'),
  }));

  protected apply(change: AssistantProfileChange): void {
    if ('audience' in change) {
      this.store.setAudienceValue(change.audience ?? null);
      return;
    }

    this.store.update(change);
  }
}
