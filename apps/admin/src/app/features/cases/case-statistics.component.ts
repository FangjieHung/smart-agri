import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, type Params } from '@angular/router';
import { map, of, switchMap, throwError } from 'rxjs';
import {
  formatHandlingHours,
  resolveCaseStatisticsRange,
  type CaseStatisticsRange,
  type CaseStatisticsRowView,
  type CaseStatisticsView,
} from '../../core/domain/case.model';
import { CasesRepository } from '../../core/repositories/cases.repository';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';

/** 一列可以點開的四個數字。 */
export type CaseStatisticsMeasure = 'open' | 'overdue' | 'completed' | 'cancelled';

/**
 * 瓶頸統計（issue #251；M7 計畫第 3 節 F）：`/app/cases?view=statistics`，只有管理者看得到這個分頁。
 * 依「案件類型 × 目前承辦組」列出未結案、逾期、期間內完成與取消的件數與平均處理時間（建立 → 完成，
 * 沒有完成件數時顯示「—」）。期間放在網址（`from`／`to`，UTC 日），換頁回來仍是同一段期間。每個數字
 * 都能點開篩選後的案件清單，件數與該格相同（管理者看得到所有案件）。只透過 lazy 的案件頁載入。
 */
@Component({
  selector: 'app-case-statistics',
  imports: [RouterLink, StatePanelComponent],
  templateUrl: './case-statistics.component.html',
  styleUrl: './case-statistics.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CaseStatisticsComponent {
  private readonly cases = inject(CasesRepository);
  private readonly session = inject(DemoSessionService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly formatHours = formatHandlingHours;

  private readonly query = toSignal(
    this.route.queryParamMap.pipe(map((params) => ({ from: params.get('from') ?? '', to: params.get('to') ?? '' }))),
    { initialValue: { from: this.route.snapshot.queryParamMap.get('from') ?? '', to: this.route.snapshot.queryParamMap.get('to') ?? '' } },
  );

  /** 網址的期間不合法時（手動改壞），送出前就顯示訊息、不發出請求。 */
  protected readonly rangeError = computed(() => {
    const resolved = resolveCaseStatisticsRange(this.query().from, this.query().to, new Date());
    return 'message' in resolved ? resolved.message : '';
  });

  // 日期欄位的輸入（按「套用」才寫進網址）
  protected readonly fromInput = signal(this.query().from);
  protected readonly toInput = signal(this.query().to);
  protected readonly inputError = signal('');

  protected readonly resource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      const { from, to } = this.query();
      return accountId && !this.rangeError() ? { accountId, from, to } : undefined;
    },
    stream: (params) => this.cases.statistics({ from: params.from || undefined, to: params.to || undefined }).pipe(
      // 期間已在畫面上檢查過；後端仍拒絕（例如時鐘跨日）時當成讀取失敗。
      switchMap((result) => result.status === 'validation-failed'
        ? throwError(() => new Error(result.message))
        : of<RepositoryView<CaseStatisticsView>>(result)),
    ),
  });
  protected readonly view = this.resource.view;

  /** 套用日期：空白表示用預設（最近 30 天）。 */
  protected apply(): void {
    const from = this.fromInput();
    const to = this.toInput();
    const resolved = resolveCaseStatisticsRange(from, to, new Date());
    if ('message' in resolved) {
      this.inputError.set(resolved.message);
      return;
    }
    this.inputError.set('');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { view: 'statistics', from: from || null, to: to || null },
    });
  }

  /** 點開篩選後的清單：類型＋承辦組，再加上這一格的條件（未結案、逾期、期間內完成或取消）。 */
  protected listParams(row: CaseStatisticsRowView, measure: CaseStatisticsMeasure, range: CaseStatisticsRange): Params {
    const base: Params = { scope: 'all', typeId: row.type.id, groupId: row.group.id };
    switch (measure) {
      case 'open':
        return { ...base, status: 'open' };
      case 'overdue':
        return { ...base, status: 'open', overdue: 'true' };
      case 'completed':
        return { ...base, status: 'completed', closedFrom: range.from, closedTo: range.to };
      case 'cancelled':
        return { ...base, status: 'cancelled', closedFrom: range.from, closedTo: range.to };
    }
  }

  protected linkLabel(row: CaseStatisticsRowView, measure: CaseStatisticsMeasure, count: number): string {
    const names: Readonly<Record<CaseStatisticsMeasure, string>> = {
      open: '未結案',
      overdue: '逾期',
      completed: '期間內完成',
      cancelled: '期間內取消',
    };
    return `${count} 件，${row.type.name}・${row.group.name}的${names[measure]}案件，開啟清單`;
  }
}
