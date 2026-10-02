import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';

@Component({
  selector: 'app-operations-summary-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent],
  templateUrl: './operations-summary-page.component.html',
  styleUrl: './operations-summary-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsSummaryPageComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly summaryResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.getOperationsSummary(),
  });
  protected readonly summary = computed(() => {
    const result = this.summaryResource.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });
  protected readonly status = computed(() => this.summaryResource.view().status);
  protected percent = (value: number) => `${Math.round(value * 100)}%`;
}
