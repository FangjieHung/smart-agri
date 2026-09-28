import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SourceConnectionListComponent } from '../../../components/source-connection-list/source-connection-list.component';
import { StatePanelComponent } from '../../../../../shared/ui/state-panel/state-panel.component';
import { ApiSessionService } from '../../../../../core/session/api-session.service';
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
  /** API 模式只列出知識庫（資料庫屬於 M4）。 */
  protected readonly apiMode = inject(ApiSessionService).apiMode;
}
