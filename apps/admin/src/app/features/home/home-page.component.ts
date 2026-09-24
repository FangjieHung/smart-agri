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

  /** 目前帳號可以直接開啟對話的助理（自己的、團隊分享的或開放給外部客戶的）。 */
  protected readonly usableAssistants = computed(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];
    const result = this.repository.listUsableAssistants(accountId);
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  /** 目前帳號尚未完成的建立草稿；其他帳號的草稿不會出現在這裡。 */
  protected readonly pendingDraft = computed(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return null;
    const result = this.repository.listNamedAssistantDrafts(accountId);
    if (result.status !== 'ready' || result.data.length === 0) return null;
    const latest = [...result.data].sort((a, b) => b.savedAt.localeCompare(a.savedAt))[0];
    return { id: latest.id, name: latest.draft.name.trim() || '未命名助理', step: latest.draft.currentStep, count: result.data.length };
  });
}
