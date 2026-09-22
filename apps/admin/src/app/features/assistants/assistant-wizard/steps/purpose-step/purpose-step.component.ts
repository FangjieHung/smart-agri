import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { AssistantTone } from '../../../../../core/domain/assistant-draft.model';
import { AssistantDraftStore } from '../../assistant-draft.store';

const TONES: readonly { readonly value: AssistantTone; readonly label: string }[] = [
  { value: 'friendly', label: '親切自然' },
  { value: 'professional', label: '專業正式' },
  { value: 'concise', label: '簡短直接' },
];

@Component({
  selector: 'app-purpose-step',
  templateUrl: './purpose-step.component.html',
  styleUrl: '../wizard-step.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurposeStepComponent {
  protected readonly store = inject(AssistantDraftStore);
  protected readonly tones = TONES;

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected toggleAudience(kind: 'internal' | 'external', event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.store.setAudience({ ...this.store.audienceFlags(), [kind]: checked });
  }

  protected tone(event: Event): AssistantTone {
    const value = (event.target as HTMLSelectElement).value;
    return TONES.find((tone) => tone.value === value)?.value ?? 'friendly';
  }
}
