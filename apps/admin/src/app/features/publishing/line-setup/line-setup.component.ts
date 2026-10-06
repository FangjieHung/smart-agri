import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import type { Observable } from 'rxjs';
import {
  LINE_FIELDS,
  MAX_LINE_WELCOME_LENGTH,
  PUBLISHING_STATUS_META,
  type LineConnectionCheckState,
  type LineErrorField,
  type LineField,
  type LinePublishFailure,
  type LineSetupView,
  type LineTestRefusal,
  type PublishingFieldError,
  type WebsiteServingState,
} from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { focusErrorField } from '../../../shared/ui/error-summary';
import { formatLastSeen } from '../website-embed/website-embed.component';

type SecretField = 'channelSecret' | 'accessToken';

const CHECK_TEXT: Readonly<Record<LineConnectionCheckState, { readonly symbol: string; readonly label: string }>> = {
  pending: { symbol: '○', label: '尚未測試' },
  passed: { symbol: '✓', label: '通過' },
  failed: { symbol: '✕', label: '未通過' },
  skipped: { symbol: '–', label: '略過' },
};

/** 實際服務狀態的文字（與管道卡的五種狀態並用；原因在 `statusDetail`）。 */
const SERVING_LABELS: Readonly<Record<WebsiteServingState, string>> = {
  'not-published': '尚未啟用',
  paused: '擁有者暫停中',
  'suspended-acceptance': '自動暫停：驗收未通過',
  'suspended-knowledge': '自動暫停：連接了別人的知識庫',
  'suspended-quota': '自動暫停：本月用量已達上限',
  serving: '服務中',
};

const FIELD_LABELS: Readonly<Record<LineErrorField, string>> = {
  officialAccountId: '官方帳號 ID',
  channelId: 'Channel ID',
  channelSecret: 'Channel secret',
  accessToken: 'Channel access token',
  welcomeMessage: '歡迎訊息',
};

/**
 * LINE 設定：儲存連接資訊（Secret 與 Token 只寫，已設定時只顯示末四碼）、測試連線（三項檢查）、
 * 啟用、暫停與取消啟用。Webhook 網址由系統設定到 LINE，這裡只唯讀顯示。
 */
@Component({
  selector: 'app-line-setup',
  imports: [RouterLink],
  templateUrl: './line-setup.component.html',
  styleUrl: './line-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LineSetupComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  readonly assistantId = input.required<string>();
  readonly view = input.required<LineSetupView>();
  readonly changed = output<void>();

  protected readonly fields = LINE_FIELDS;
  protected readonly maxWelcome = MAX_LINE_WELCOME_LENGTH;
  protected readonly draft = linkedSignal(() => {
    const { officialAccountId, channelId, welcomeMessage } = this.view();
    return { officialAccountId, channelId, welcomeMessage };
  });
  /** 新的 Secret／Token：只存在於這個元件的輸入中，儲存後（或取消更換時）立刻清掉；從不從 `view` 帶回。 */
  protected readonly secrets = signal<Readonly<Record<SecretField, string>>>({ channelSecret: '', accessToken: '' });
  /** 已設定的憑證按「更換」後才出現輸入框。 */
  protected readonly replacing = signal<ReadonlySet<SecretField>>(new Set());
  protected readonly errors = signal<readonly PublishingFieldError[]>([]);
  protected readonly saveStatus = signal('');
  protected readonly testStatus = signal('');
  protected readonly testRefusals = signal<readonly LineTestRefusal[]>([]);
  protected readonly copyStatus = signal('');
  /** 儲存、測試連線、啟用、暫停、取消啟用送出中：擋重複送出。 */
  protected readonly busy = signal(false);
  protected readonly actionStatus = signal('');
  protected readonly actionError = signal('');
  /** 啟用閘門 `422` 的每一項原因；空陣列表示沒有被拒絕。 */
  protected readonly publishFailures = signal<readonly LinePublishFailure[]>([]);
  protected readonly publishRefusedMessage = signal('');
  /** `409`：設定在別處被改過，請使用者重新載入。 */
  protected readonly conflictMessage = signal('');

  protected readonly statusMeta = computed(() => PUBLISHING_STATUS_META[this.view().channel.status]);
  protected readonly servingLabel = computed(() => SERVING_LABELS[this.view().servingState]);
  protected readonly blocked = computed(() => this.view().nonOwnedKnowledgeBases.length > 0);
  protected readonly saved = computed(() => this.view().revision > 0);
  protected readonly checkedAt = computed(() => {
    const at = this.view().connectionCheckedAt;
    return at === null ? '' : formatLastSeen(at);
  });
  protected readonly dirty = computed(() => {
    const view = this.view();
    const draft = this.draft();
    const secrets = this.secrets();
    return (
      draft.officialAccountId !== view.officialAccountId ||
      draft.channelId !== view.channelId ||
      draft.welcomeMessage !== view.welcomeMessage ||
      secrets.channelSecret.length > 0 ||
      secrets.accessToken.length > 0
    );
  });

  protected checkText(state: LineConnectionCheckState) {
    return CHECK_TEXT[state];
  }

  protected labelFor(field: string): string {
    return FIELD_LABELS[field as LineErrorField] ?? field;
  }

  protected focusField(event: Event, field: string): void {
    focusErrorField(this.document, `line-${field}`, event);
  }

  protected focusTest(event: Event): void {
    focusErrorField(this.document, 'line-test', event);
  }

  protected hasError(field: LineErrorField): boolean {
    return this.errors().some((error) => error.field === field);
  }

  protected errorOf(field: LineErrorField): string {
    return this.errors().find((error) => error.field === field)?.message ?? '';
  }

  protected describedBy(field: LineErrorField): string {
    return this.hasError(field) ? `line-${field}-hint line-${field}-error` : `line-${field}-hint`;
  }

  protected hasFailure(reason: LinePublishFailure['reason']): boolean {
    return this.publishFailures().some((failure) => failure.reason === reason);
  }

  protected setPlain(field: 'officialAccountId' | 'channelId' | 'welcomeMessage', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;
    this.draft.update((draft) => ({ ...draft, [field]: value }));
  }

  protected setSecret(field: SecretField, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.secrets.update((secrets) => ({ ...secrets, [field]: value }));
  }

  /** 憑證欄位只有在「還沒設定」或按了「更換」時才是輸入框。 */
  protected showsInput(field: SecretField): boolean {
    return !this.view()[field].configured || this.replacing().has(field);
  }

  /** 憑證只顯示狀態時沒有可以關聯的輸入框，標籤就不指向任何元素。 */
  protected labelTarget(field: LineField): string | null {
    const secret = field === 'channelSecret' || field === 'accessToken';
    return secret && !this.showsInput(field) ? null : `line-${field}`;
  }

  protected statusText(field: SecretField): string {
    const { lastFour } = this.view()[field];
    return lastFour === null ? '已設定' : `已設定・末四碼 ${lastFour}`;
  }

  protected startReplace(field: SecretField): void {
    this.replacing.update((current) => new Set(current).add(field));
    afterNextRender(() => this.document.getElementById(`line-${field}`)?.focus(), { injector: this.injector });
  }

  protected cancelReplace(field: SecretField): void {
    this.replacing.update((current) => {
      const next = new Set(current);
      next.delete(field);
      return next;
    });
    this.secrets.update((secrets) => ({ ...secrets, [field]: '' }));
    this.errors.update((errors) => errors.filter((error) => error.field !== field));
    afterNextRender(() => this.document.getElementById(`line-${field}-replace`)?.focus(), { injector: this.injector });
  }

  protected save(event: Event): void {
    event.preventDefault();
    if (this.busy()) return;
    this.busy.set(true);
    this.conflictMessage.set('');
    this.saveStatus.set('');
    const { channelSecret, accessToken } = this.secrets();
    this.repository
      .saveLineSettings(this.assistantId(), { ...this.draft(), channelSecret, accessToken }, this.view().revision)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.status === 'validation-failed') {
            this.errors.set(result.errors);
          } else if (result.status === 'conflict') {
            this.conflictMessage.set(result.message);
          } else if (result.status === 'permission-denied') {
            this.saveStatus.set(result.message);
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.errors.set([]);
            this.clearSecrets();
            this.testRefusals.set([]);
            this.testStatus.set('');
            this.publishFailures.set([]);
            this.saveStatus.set(
              result.data.checks.every((check) => check.state === 'pending')
                ? '已儲存。下一步請按「測試連線」。'
                : '已儲存。',
            );
            this.changed.emit();
          }
        },
        error: () => {
          this.busy.set(false);
          this.saveStatus.set('目前無法儲存 LINE 設定，請稍後再試。');
        },
      });
  }

  private clearSecrets(): void {
    this.secrets.set({ channelSecret: '', accessToken: '' });
    this.replacing.set(new Set());
  }

  /** 重新載入最新的設定（放棄這次的修改）。 */
  protected reload(): void {
    this.conflictMessage.set('');
    this.errors.set([]);
    this.saveStatus.set('');
    this.testStatus.set('');
    this.clearSecrets();
    this.changed.emit();
  }

  protected testConnection(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.conflictMessage.set('');
    this.testRefusals.set([]);
    this.testStatus.set('正在測試連線…');
    this.repository
      .testLineConnection(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.status === 'test-refused') {
            this.testRefusals.set(result.failures);
            this.testStatus.set('');
          } else if (result.status === 'conflict') {
            this.conflictMessage.set(result.message);
            this.testStatus.set('');
          } else if (result.status === 'permission-denied') {
            this.testStatus.set(result.message);
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.publishFailures.set([]);
            this.testStatus.set(
              result.data.checks.every((check) => check.state === 'passed')
                ? '三項檢查都通過，可以啟用。'
                : '連線測試未通過，請依下列原因處理後再測一次。',
            );
            this.changed.emit();
          }
        },
        error: () => {
          this.busy.set(false);
          this.testStatus.set('目前無法測試連線，請稍後再試。');
        },
      });
  }

  protected publish(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.actionStatus.set('');
    this.actionError.set('');
    this.repository
      .publishLine(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.status === 'publish-refused') {
            this.publishFailures.set(result.failures);
            this.publishRefusedMessage.set(result.message);
          } else if (result.status === 'permission-denied') {
            this.actionError.set(result.message);
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.publishFailures.set([]);
            this.actionStatus.set('已啟用 LINE 頻道，LINE 使用者現在可以和助理對話。');
            this.changed.emit();
          }
        },
        error: () => {
          this.busy.set(false);
          this.actionError.set('目前無法啟用，請稍後再試。');
        },
      });
  }

  protected togglePause(): void {
    const pause = this.view().state !== 'paused';
    this.runAction(
      this.repository.setPublishingChannelPaused(this.assistantId(), 'line', pause),
      pause ? '已暫停 LINE，使用者目前會收到「暫停服務」。' : '已恢復 LINE。',
      '目前無法變更 LINE 的狀態，請稍後再試。',
    );
  }

  protected unpublish(): void {
    this.runAction(
      this.repository.unpublishLine(this.assistantId()),
      '已取消啟用，設定與憑證都保留；之後可以再啟用。',
      '目前無法取消啟用，請稍後再試。',
    );
  }

  private runAction(
    request: Observable<{ readonly status: string; readonly message?: string }>,
    success: string,
    failure: string,
  ): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.actionStatus.set('');
    this.actionError.set('');
    this.publishFailures.set([]);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        this.busy.set(false);
        if (result.status === 'ready' || result.status === 'partial-failure') {
          this.actionStatus.set(success);
          this.changed.emit();
        } else {
          this.actionError.set(result.message ?? failure);
        }
      },
      error: () => {
        this.busy.set(false);
        this.actionError.set(failure);
        this.changed.emit();
      },
    });
  }

  protected async copyWebhookUrl(): Promise<void> {
    const url = this.view().webhookUrl;
    if (url === null) return;
    try {
      await navigator.clipboard.writeText(url);
      this.copyStatus.set('已複製 Webhook 網址。');
    } catch {
      this.copyStatus.set('無法自動複製，請選取上方網址後手動複製。');
    }
  }
}
