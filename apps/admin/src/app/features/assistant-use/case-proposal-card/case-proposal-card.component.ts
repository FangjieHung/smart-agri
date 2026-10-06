import { ChangeDetectionStrategy, Component, computed, input, linkedSignal, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ChatCaseProposalView } from '../../../core/domain/conversation.model';
import { formatDueHours } from '../../../core/domain/case-due-time';
import { CASE_PROPOSAL_TITLE_MAX_LENGTH } from '../../../core/domain/case-proposal';
import type { ChatCaseProposalConfirmation } from '../../../core/repositories/demo-repository';

/** 說明的長度上限，與後端 `Case.DescriptionMaxLength` 相同。 */
const DESCRIPTION_MAX_LENGTH = 4000;

/**
 * 助理提議開案的卡片（issue #254）：顯示類型、承辦組與處理時限，標題與說明可以修改；「建立案件」交給
 * 對話元件開確認視窗，「不用了」只記下來。已建立時顯示案件連結；類型已停用或已不在助理的清單上時顯示
 * 「無法建立」。只在 admin 的對話元件使用（lazy），不放進 `@smart-agri/chat`，官網 widget 的 bundle 不受影響。
 */
@Component({
  selector: 'app-case-proposal-card',
  imports: [RouterLink],
  templateUrl: './case-proposal-card.component.html',
  styleUrl: './case-proposal-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CaseProposalCardComponent {
  /** 訊息 id，用來組出欄位的 id（同一頁可能有多張卡片）。 */
  readonly messageId = input.required<string>();
  readonly proposal = input.required<ChatCaseProposalView | null>();
  readonly busy = input(false);
  /** 確認或「不用了」失敗時的說明（整張卡片）。 */
  readonly error = input('');
  readonly fieldErrors = input<Readonly<Partial<Record<'title' | 'description', string>>>>({});
  /** 使用者按了「建立案件」：送出目前輸入的標題與說明（確認視窗由對話元件負責）。 */
  readonly confirmRequested = output<ChatCaseProposalConfirmation>();
  readonly dismissed = output<void>();

  protected readonly titleMaxLength = CASE_PROPOSAL_TITLE_MAX_LENGTH;
  protected readonly descriptionMaxLength = DESCRIPTION_MAX_LENGTH;

  /** 輸入中的標題與說明：換一則提議（或伺服器回傳新的快照）時重設。 */
  protected readonly title = linkedSignal(() => this.proposal()?.title ?? '');
  protected readonly description = linkedSignal(() => this.proposal()?.description ?? '');

  protected readonly state = computed<'editable' | 'unavailable' | 'confirmed' | 'dismissed' | 'missing'>(() => {
    const proposal = this.proposal();
    if (proposal === null) return 'missing';
    if (proposal.status === 'confirmed') return 'confirmed';
    if (proposal.status === 'dismissed') return 'dismissed';
    return proposal.available ? 'editable' : 'unavailable';
  });

  protected readonly dueLabel = computed(() => {
    const proposal = this.proposal();
    return proposal === null ? '' : formatDueHours(proposal.dueHours);
  });

  protected fieldId(field: string): string {
    return `case-proposal-${this.messageId()}-${field}`;
  }

  protected updateTitle(event: Event): void {
    this.title.set((event.target as HTMLInputElement).value);
  }

  protected updateDescription(event: Event): void {
    this.description.set((event.target as HTMLTextAreaElement).value);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.busy()) return;
    this.confirmRequested.emit({ title: this.title(), description: this.description() });
  }
}
