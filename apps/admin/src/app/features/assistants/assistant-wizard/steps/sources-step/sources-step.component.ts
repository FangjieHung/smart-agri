import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SourceConnectionListComponent } from '../../../components/source-connection-list/source-connection-list.component';
import { StatePanelComponent } from '../../../../../shared/ui/state-panel/state-panel.component';
import { AssistantDraftStore } from '../../assistant-draft.store';

@Component({
  selector: 'app-sources-step',
  imports: [SourceConnectionListComponent, StatePanelComponent],
  templateUrl: './sources-step.component.html',
  styleUrl: '../../../components/assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SourcesStepComponent {
  protected readonly store = inject(AssistantDraftStore);
}
