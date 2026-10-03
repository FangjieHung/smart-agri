import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';

@Component({
  selector: 'app-activity-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent],
  templateUrl: './activity-page.component.html',
  styleUrl: './activity-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ActivityPageComponent {
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly session = inject(DemoSessionService);
  protected readonly resource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.issues.list({ scope: 'forwarded' }),
  });
  protected readonly view = this.resource.view;
  protected readonly items = computed(() => {
    const result = this.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  protected statusLabel(status: string): string {
    return status === 'open' ? '待處理' : status === 'in-progress' ? '處理中' : '已解決';
  }
}
