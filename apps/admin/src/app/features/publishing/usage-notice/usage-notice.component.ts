import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { OrganizationUsageState } from '../../../core/domain/organization-usage.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 狀態用文字加符號表示，不單靠顏色（issue #203）。 */
const STATE_META: Readonly<Record<OrganizationUsageState, { readonly label: string; readonly symbol: string }>> = {
  normal: { label: '用量正常', symbol: '✓' },
  near: { label: '接近上限', symbol: '!' },
  exceeded: { label: '已超過上限', symbol: '✕' },
};

/**
 * 組織本月 token 用量（M5a 計畫第 3 節 F）。同一個小元件兩種呈現：
 * - `summary`：「發布管道」頁與助理的「發布」頁，三種狀態都顯示月份、已用／上限與說明；
 * - `banner`：首頁，只在 `near`／`exceeded` 顯示。
 *
 * 只有 `manage-publishing` 的帳號會讀取，也只有他們看得到；沒有權限（含後端 `403`）一律什麼都不顯示，
 * 不是錯誤。元件只被 lazy 路由引用，不進初始 bundle。
 */
@Component({
  selector: 'app-usage-notice',
  imports: [RouterLink],
  templateUrl: './usage-notice.component.html',
  styleUrl: './usage-notice.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsageNoticeComponent {
  private readonly session = inject(DemoSessionService);
  private readonly apiSession = inject(ApiSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  readonly variant = input<'summary' | 'banner'>('summary');

  private readonly usage = repositoryResource({
    params: () =>
      this.session.activeAccountId() !== null && this.apiSession.permissions().includes('manage-publishing')
        ? this.session.activeAccountId()
        : undefined,
    stream: () => this.repository.getOrganizationUsage(),
  });
  protected readonly result = this.usage.view;

  /** 讀取成功的用量；沒有權限、讀取中或沒有資料都是 null。 */
  protected readonly data = computed(() => {
    const result = this.result();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });
  /**
   * 「已使用 / 上限」的千分位文字。用 `toLocaleString` 而不是 `DecimalPipe`：後者會把 Angular 的數字格式化
   * 程式（約 4 kB）拉進初始 bundle 的共用 chunk，而初始 bundle 只剩約 10 kB 空間。
   */
  protected readonly amounts = computed(() => {
    const usage = this.data();
    return usage === null ? '' : `${usage.usedTokens.toLocaleString('en-US')} / ${usage.limitTokens.toLocaleString('en-US')} tokens`;
  });
  protected readonly failed = computed(() => this.result().status === 'error');
  protected readonly meta = computed(() => STATE_META[this.data()?.state ?? 'normal']);
  protected readonly percent = computed(() => {
    const usage = this.data();
    return usage === null || usage.limitTokens <= 0 ? 0 : Math.floor((usage.usedTokens * 100) / usage.limitTokens);
  });
  /** 進度條寬度：超過上限也只填滿。 */
  protected readonly barWidth = computed(() => `${Math.min(this.percent(), 100)}%`);
}
