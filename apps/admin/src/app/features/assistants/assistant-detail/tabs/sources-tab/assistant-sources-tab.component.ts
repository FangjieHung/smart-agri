import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SourceConnectionListComponent } from '../../../components/source-connection-list/source-connection-list.component';
import { AssistantSettingsStore } from '../../assistant-settings.store';

/**
 * 建立後的「資料來源」：和建立精靈步驟二同一份清單，但每次切換就立刻套用到助理身上，
 * 所以多了兩條建立時不需要的規則——不能解除最後一個來源，解除的資料庫若是寫入對象會一併清掉。
 */
@Component({
  selector: 'app-assistant-sources-tab',
  imports: [SourceConnectionListComponent],
  templateUrl: './assistant-sources-tab.component.html',
  styleUrl: '../../../components/assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantSourcesTabComponent {
  protected readonly store = inject(AssistantSettingsStore);
}
