import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatDueHours } from '../../../../core/domain/case-due-time';
import type { CaseTypeView } from '../../../../core/domain/case-settings.model';
import { CaseSettingsRepository } from '../../../../core/repositories/case-settings.repository';
import { repositoryResource } from '../../../../core/repositories/repository-resource';

/** 清單上但現在讀不到的類型（已停用，或不是管理者看不到停用的類型）。 */
interface UnavailableType {
  readonly id: string;
}

/**
 * 「可提議的案件類型」（issue #254，決定 U）：助理擁有者從啟用中的案件類型挑，預設為空。勾選後，
 * 組織內部帳號在對話中描述需要處理的事時，助理會提議開一件這類案件，使用者確認後才建立。
 *
 * 「沒有表單」的警示（#346）：助理會提議開案、卻沒有指定寫入的資料庫（對話中就沒有表單可給）時，
 * 病蟲害回報這類該填表的事沒有表單可以接。#286 評測中這類題目 6/6 被提議成案件；#297 讓模型可以選
 * 「都不符合」後降到 0/6，但仍然沒有表單可以留下紀錄，所以警示說「可能」。
 */
@Component({
  selector: 'app-proposable-case-types',
  imports: [RouterLink],
  templateUrl: './proposable-case-types.component.html',
  styleUrls: ['../assistant-form.scss', './proposable-case-types.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProposableCaseTypesComponent {
  private readonly caseSettings = inject(CaseSettingsRepository);

  /** 目前在清單上的類型 id（伺服器回傳的設定）。 */
  readonly selected = input.required<readonly string[]>();
  readonly busy = input(false);
  readonly error = input<string | null>(null);
  /** 對話會保存（`rules.keepOwnConversations`）；不保存時後端與 mock 都不提議開案。 */
  readonly keepsConversations = input.required<boolean>();
  /** 有指定寫入的資料庫（`rules.dataWriteDatabaseId`），也就是助理在對話中有表單可以請同仁填。 */
  readonly hasFormTarget = input.required<boolean>();
  /** 指定寫入資料庫的地方（「回答與記錄」頁籤）。 */
  readonly formTargetLink = input.required<readonly string[]>();
  readonly toggled = output<string>();

  private readonly typesResource = repositoryResource({
    params: () => true,
    stream: () => this.caseSettings.listCaseTypes(),
  });

  /** 啟用中的類型；讀不到（例如沒有案件功能）時是 null。 */
  protected readonly types = computed<readonly CaseTypeView[] | null>(() => {
    const view = this.typesResource.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data.types.filter((type) => type.isActive) : null;
  });
  protected readonly loading = computed(() => this.typesResource.view().status === 'loading');

  protected readonly unavailable = computed<readonly UnavailableType[]>(() => {
    const active = new Set((this.types() ?? []).map((type) => type.id));
    return this.selected().filter((id) => !active.has(id)).map((id) => ({ id }));
  });

  /**
   * 助理現在真的會提議開案：對話會保存，而且清單上至少有一個啟用中的類型（與後端
   * `ChatCaseProposals.ProposableAsync` 只取啟用中的類型相同）。類型讀不到時不判斷。
   */
  private readonly proposesCases = computed(() => {
    const active = this.types();
    if (active === null || !this.keepsConversations()) return false;
    const selected = new Set(this.selected());
    return active.some((type) => selected.has(type.id));
  });

  /** 會提議開案、卻沒有表單可以給（#346）。 */
  protected readonly missingForm = computed(() => this.proposesCases() && !this.hasFormTarget());

  protected isSelected(typeId: string): boolean {
    return this.selected().includes(typeId);
  }

  protected dueLabel(hours: number): string {
    return formatDueHours(hours);
  }
}
