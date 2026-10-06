import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
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
 */
@Component({
  selector: 'app-proposable-case-types',
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

  protected isSelected(typeId: string): boolean {
    return this.selected().includes(typeId);
  }

  protected dueLabel(hours: number): string {
    return formatDueHours(hours);
  }
}
