import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantSummaryView } from '../../core/domain/assistant.model';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import { CasesRepository } from '../../core/repositories/cases.repository';
import { DEMO_ACCOUNT_BY_ROLE } from '../../core/session/api-mode/demo-identity-bridge';

/**
 * 側欄的逾期數字由 shell 以動態 `import()` 載入這個 chunk 後呼叫（`App.refreshCaseOverdueCount`）：
 * 首頁本來就帶著 `CasesRepository`，共用這個 chunk 才不會在初始 bundle 多切一塊。
 */
export { refreshCaseOverdueCount } from '../cases/case-attention.loader';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { AssistantPermissionsService } from '../../core/session/assistant-permissions.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { UsageNoticeComponent } from '../publishing/usage-notice/usage-notice.component';

@Component({
  selector: 'app-home-page',
  imports: [RouterLink, PageHeaderComponent, UsageNoticeComponent],
  templateUrl: './home-page.component.html',
  styleUrl: './home-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomePageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly cases = inject(CasesRepository);
  private readonly assistantPermissions = inject(AssistantPermissionsService);

  /** 與助理清單共用同一條判斷（`AssistantPermissionsService`）：沒有權限就不顯示建立入口。 */
  protected readonly canCreateAssistant = this.assistantPermissions.canCreateAssistant;

  /** 非同步契約（issue #81）：換身分就重新讀取；API 模式是真實的助理 GUID。 */
  private readonly usable = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listUsableAssistants(),
  });

  private readonly drafts = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listNamedAssistantDrafts(),
  });

  private readonly issueSummary = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.issues.summary(),
  });
  protected readonly issueSummaryView = this.issueSummary.view;

  /** 案件卡片（issue #250）：只給內部帳號；外部客戶不顯示、也不發請求。 */
  protected readonly showCases = computed(() => {
    const accountId = this.session.activeAccountId();
    return accountId !== null && accountId !== DEMO_ACCOUNT_BY_ROLE['external-customer'];
  });

  private readonly caseAttention = repositoryResource({
    params: () => (this.showCases() ? this.session.activeAccountId() ?? undefined : undefined),
    stream: () => this.cases.attention(),
  });
  protected readonly caseAttentionView = this.caseAttention.view;

  /** 目前帳號可以直接開啟對話的助理（自己的，以及團隊分享給自己的）。 */
  protected readonly usableAssistants = computed<readonly AssistantSummaryView[]>(() => {
    const result = this.usable.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  protected readonly assistantCount = computed(() => this.usableAssistants().length);

  /** 沒有建立權限時，主要操作改指向第一個可用的助理，讓使用者能直接開始對話。 */
  protected readonly primaryUsableAssistant = computed(() => this.usableAssistants()[0] ?? null);

  /**
   * 目前帳號尚未完成的建立草稿；其他帳號的草稿不會出現在這裡。沒有建立權限
   * （`assistant-draft`）、讀取中或讀取失敗時都當成沒有草稿，不擋住首頁其餘內容。
   */
  protected readonly pendingDraft = computed(() => {
    const result = this.drafts.view();
    if (result.status !== 'ready' || result.data.length === 0) return null;
    const latest = [...result.data].sort((a, b) => b.savedAt.localeCompare(a.savedAt))[0];
    return { id: latest.id, name: latest.draft.name.trim() || '未命名助理', step: latest.draft.currentStep, count: result.data.length };
  });
}
