import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import {
  LINE_FIELDS,
  type LineCheckState,
  type LineField,
  type LineFieldCheckView,
  type LineSettingsInput,
  type LineSetupView,
} from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { focusErrorField } from '../../../shared/ui/error-summary';

const CHECK_TEXT: Readonly<Record<LineCheckState, { readonly symbol: string; readonly label: string }>> = {
  pending: { symbol: '○', label: '尚未檢查' },
  passed: { symbol: '✓', label: '通過' },
  failed: { symbol: '✕', label: '未通過' },
};

/** LINE 設定：逐欄檢查、模擬測試訊息與啟用；敏感欄位預設遮蔽。不會連接 LINE。 */
@Component({
  selector: 'app-line-setup',
  templateUrl: './line-setup.component.html',
  styleUrl: './line-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LineSetupComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly document = inject(DOCUMENT);

  protected focusField(event: Event, field: string): void {
    focusErrorField(this.document, `line-${field}`, event);
  }

  readonly assistantId = input.required<string>();
  readonly view = input.required<LineSetupView>();
  readonly changed = output<void>();

  protected readonly fields = LINE_FIELDS;
  protected readonly draft = linkedSignal<LineSettingsInput>(() => {
    const { officialAccountId, channelId, channelSecret, accessToken } = this.view();
    return { officialAccountId, channelId, channelSecret, accessToken };
  });
  protected readonly revealed = signal<ReadonlySet<LineField>>(new Set());
  protected readonly feedback = signal('');
  protected readonly failedChecks = computed(() => this.view().checks.filter((check) => check.state === 'failed'));

  protected checkFor(field: LineField): LineFieldCheckView | undefined {
    return this.view().checks.find((check) => check.field === field);
  }

  protected checkText(state: LineCheckState) {
    return CHECK_TEXT[state];
  }

  protected isFailed(field: LineField): boolean {
    return this.checkFor(field)?.state === 'failed';
  }

  protected describedBy(field: LineField): string {
    return this.isFailed(field) ? `line-${field}-hint line-${field}-error` : `line-${field}-hint`;
  }

  protected inputType(field: LineField, sensitive: boolean): string {
    return sensitive && !this.revealed().has(field) ? 'password' : 'text';
  }

  protected toggleReveal(field: LineField): void {
    this.revealed.update((current) => {
      const next = new Set(current);
      if (next.has(field)) next.delete(field);
      else next.add(field);
      return next;
    });
  }

  protected setValue(field: LineField, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.draft.update((draft) => ({ ...draft, [field]: value }));
  }

  protected save(event: Event): void {
    event.preventDefault();
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.saveLineSettings(accountId, this.assistantId(), this.draft());
    if (result.status === 'ready' || result.status === 'partial-failure') {
      const failed = result.data.checks.filter((check) => check.state === 'failed').length;
      this.feedback.set(failed > 0 ? `已儲存，${failed} 個欄位未通過檢查。` : '已儲存，所有欄位都通過檢查。下一步請傳送測試訊息。');
      this.changed.emit();
    } else if (result.status === 'permission-denied') {
      this.feedback.set(result.message);
    }
  }

  protected sendTest(): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    this.feedback.set('');
    this.repository.sendLineTestMessage(accountId, this.assistantId());
    this.changed.emit();
  }

  protected activate(): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.activateLineChannel(accountId, this.assistantId());
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.feedback.set('LINE 管道已啟用（模擬），LINE 使用者現在可以與助理對話。');
      this.changed.emit();
    } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.feedback.set(result.message);
    }
  }
}
