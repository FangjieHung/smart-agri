import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'app-home-page',
  imports: [RouterLink, PageHeaderComponent],
  templateUrl: './home-page.component.html',
  styleUrl: './home-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomePageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  protected readonly assistantCount = computed(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return 0;
    const result = this.repository.listUsableAssistants(accountId);
    return result.status === 'ready' ? result.data.length : 0;
  });
}
