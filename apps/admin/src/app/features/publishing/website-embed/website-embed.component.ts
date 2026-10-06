import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import type { Observable } from 'rxjs';
import type { AssistantSettingsView } from '../../../core/domain/assistant-settings.model';
import {
  PUBLISHING_STATUS_META,
  WEBSITE_BRAND_COLORS,
  WEBSITE_LAUNCHER_POSITIONS,
  normalizeDomain,
  validateAllowedDomain,
  type PublishingFieldError,
  type WebsiteBrandColor,
  type WebsiteEmbedSettings,
  type WebsiteEmbedView,
  type WebsiteLauncherPosition,
  type WebsitePublishFailure,
  type WebsiteServingState,
} from '../../../core/domain/publishing.model';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { focusErrorField } from '../../../shared/ui/error-summary';

type PreviewDevice = 'desktop' | 'mobile';

const FIELD_ANCHORS: Readonly<Record<string, string>> = {
  displayName: 'website-display-name',
  welcomeMessage: 'website-welcome',
  brandColor: 'website-color-forest',
  position: 'website-position',
  allowedDomains: 'website-domain-input',
};

const FIELD_LABELS: Readonly<Record<string, string>> = {
  displayName: '顯示名稱',
  welcomeMessage: '歡迎語',
  brandColor: '品牌色',
  position: '顯示位置',
  allowedDomains: '允許嵌入的網域',
};

/** 實際服務狀態的文字（與管道卡的五種狀態並用；原因在 `statusDetail`）。 */
const SERVING_LABELS: Readonly<Record<WebsiteServingState, string>> = {
  'not-published': '尚未發布',
  paused: '擁有者暫停中',
  'suspended-acceptance': '自動暫停：驗收未通過',
  'suspended-knowledge': '自動暫停：連接了別人的知識庫',
  'suspended-quota': '自動暫停：本月用量已達上限',
  serving: '服務中',
};

const LAST_SEEN_FORMAT = new Intl.DateTimeFormat('zh-TW', {
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

/** 「10/06 14:03」：被動偵測到的時間，顯示在使用者的時區。 */
export function formatLastSeen(iso: string): string {
  return LAST_SEEN_FORMAT.format(new Date(iso)).replace(/\s+/g, ' ');
}

/** 官網嵌入：外觀設定、發布與暫停、允許網域、嵌入碼，以及被動偵測到的安裝狀況。 */
@Component({
  selector: 'app-website-embed',
  imports: [RouterLink],
  templateUrl: './website-embed.component.html',
  styleUrl: './website-embed.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WebsiteEmbedComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly publishDialog = viewChild<TemplateRef<unknown>>('publishDialog');
  private dialogRef: MatDialogRef<unknown> | null = null;

  readonly assistantId = input.required<string>();
  readonly view = input.required<WebsiteEmbedView>();
  readonly changed = output<void>();

  protected readonly colors = WEBSITE_BRAND_COLORS;
  protected readonly positions = WEBSITE_LAUNCHER_POSITIONS;
  protected readonly draft = linkedSignal<WebsiteEmbedSettings>(() => {
    const { displayName, welcomeMessage, brandColor, position, allowedDomains } = this.view();
    return { displayName, welcomeMessage, brandColor, position, allowedDomains };
  });
  protected readonly device = signal<PreviewDevice>('desktop');
  protected readonly domainInput = signal('');
  protected readonly domainError = signal('');
  protected readonly errors = signal<readonly PublishingFieldError[]>([]);
  protected readonly saveStatus = signal('');
  protected readonly copyStatus = signal('');
  /** 儲存、發布、暫停、取消發布送出中：擋重複送出。 */
  protected readonly busy = signal(false);
  /** 發布、暫停等動作的結果（`aria-live`）。 */
  protected readonly actionStatus = signal('');
  protected readonly actionError = signal('');
  /** 發布閘門 `422` 的每一項原因；空陣列表示沒有被拒絕。 */
  protected readonly publishFailures = signal<readonly WebsitePublishFailure[]>([]);
  protected readonly publishRefusedMessage = signal('');
  /** `409`：設定在別處被改過，請使用者重新載入。 */
  protected readonly conflictMessage = signal('');
  /** 發布確認對話框：訪客可以檢索的知識庫（決定 B，只有擁有者自己的）。 */
  protected readonly dialogKnowledge = signal<{ readonly status: 'loading' | 'ready' | 'error'; readonly names: readonly string[] }>({
    status: 'loading',
    names: [],
  });
  protected readonly dialogError = signal('');

  protected readonly brandHex = computed(
    () => this.colors.find((color) => color.id === this.draft().brandColor)?.hex ?? this.colors[0].hex,
  );
  protected readonly statusMeta = computed(() => PUBLISHING_STATUS_META[this.view().channel.status]);
  protected readonly servingLabel = computed(() => SERVING_LABELS[this.view().servingState]);
  protected readonly blocked = computed(() => this.view().nonOwnedKnowledgeBases.length > 0);
  protected readonly dirty = computed(() => {
    const view = this.view();
    const draft = this.draft();
    return (
      draft.displayName !== view.displayName ||
      draft.welcomeMessage !== view.welcomeMessage ||
      draft.brandColor !== view.brandColor ||
      draft.position !== view.position ||
      draft.allowedDomains.join('\n') !== view.allowedDomains.join('\n')
    );
  });
  protected readonly installRows = computed(() =>
    this.view().domains.map((domain) => ({
      domain: domain.domain,
      text:
        domain.lastSeenAt === null
          ? `${domain.domain}：尚未偵測到`
          : `最後一次在 ${domain.domain} 偵測到：${formatLastSeen(domain.lastSeenAt)}`,
    })),
  );

  protected focusField(event: Event, field: string): void {
    focusErrorField(this.document, this.anchorFor(field), event);
  }

  protected focusDomains(event: Event): void {
    focusErrorField(this.document, 'website-domain-input', event);
  }

  protected anchorFor(field: string): string {
    return FIELD_ANCHORS[field] ?? 'website-display-name';
  }

  protected labelFor(field: string): string {
    return FIELD_LABELS[field] ?? field;
  }

  protected hasError(field: string): boolean {
    return this.errors().some((error) => error.field === field);
  }

  protected hasFailure(reason: WebsitePublishFailure['reason']): boolean {
    return this.publishFailures().some((failure) => failure.reason === reason);
  }

  protected setText(field: 'displayName' | 'welcomeMessage', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;
    this.draft.update((draft) => ({ ...draft, [field]: value }));
  }

  protected setColor(color: WebsiteBrandColor): void {
    this.draft.update((draft) => ({ ...draft, brandColor: color }));
  }

  protected setPosition(event: Event): void {
    const position = (event.target as HTMLSelectElement).value as WebsiteLauncherPosition;
    this.draft.update((draft) => ({ ...draft, position }));
  }

  protected setDomainInput(event: Event): void {
    this.domainInput.set((event.target as HTMLInputElement).value);
  }

  protected addDomain(event?: Event): void {
    event?.preventDefault();
    const error = validateAllowedDomain(this.domainInput(), this.draft().allowedDomains);
    this.domainError.set(error ?? '');
    if (error !== null) return;
    const domain = normalizeDomain(this.domainInput());
    this.draft.update((draft) => ({ ...draft, allowedDomains: [...draft.allowedDomains, domain] }));
    this.domainInput.set('');
    this.saveStatus.set(`已加入 ${domain}，儲存後生效。`);
  }

  protected removeDomain(domain: string): void {
    this.draft.update((draft) => ({
      ...draft,
      allowedDomains: draft.allowedDomains.filter((candidate) => candidate !== domain),
    }));
    this.saveStatus.set(`已移除 ${domain}，儲存後生效。`);
  }

  protected save(event: Event): void {
    event.preventDefault();
    if (this.busy()) return;
    this.busy.set(true);
    this.conflictMessage.set('');
    this.repository
      .updateWebsiteEmbed(this.assistantId(), this.draft(), this.view().revision)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.status === 'validation-failed') {
            this.errors.set(result.errors);
            this.saveStatus.set('');
          } else if (result.status === 'conflict') {
            this.conflictMessage.set(result.message);
            this.saveStatus.set('');
          } else if (result.status === 'permission-denied') {
            this.saveStatus.set(result.message);
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.errors.set([]);
            this.saveStatus.set('已儲存官網設定。');
            this.changed.emit();
          }
        },
        error: () => {
          this.busy.set(false);
          this.saveStatus.set('目前無法儲存官網設定，請稍後再試。');
        },
      });
  }

  /** 重新載入最新的設定（放棄這次的修改）。 */
  protected reload(): void {
    this.conflictMessage.set('');
    this.errors.set([]);
    this.saveStatus.set('');
    this.changed.emit();
  }

  /** 發布前一定要確認：會讓哪些知識庫的內容被訪客問到（決定 B）。 */
  protected confirmPublish(): void {
    const content = this.publishDialog();
    if (!content || this.busy()) return;
    this.dialogError.set('');
    this.dialogKnowledge.set({ status: 'loading', names: [] });
    this.dialogRef = this.dialog.open(content, {
      width: 'min(34rem, calc(100vw - 2rem))',
      autoFocus: 'dialog',
      restoreFocus: true,
      ariaLabelledBy: 'website-publish-title',
      ariaDescribedBy: 'website-publish-detail',
      role: 'alertdialog',
    });
    this.repository
      .getAssistantSettings(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => this.loadOwnKnowledgeBases(settings),
        error: () => this.dialogKnowledge.set({ status: 'error', names: [] }),
      });
  }

  /** 連接的來源裡，助理擁有者自己的知識庫（`permission === 'owner'`）名稱；資料庫訪客問不到，不列。 */
  private loadOwnKnowledgeBases(settings: RepositoryView<AssistantSettingsView>): void {
    this.repository
      .listConnectableSources()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (sources) => {
          if (settings.status !== 'ready' || sources.status !== 'ready') {
            this.dialogKnowledge.set({ status: 'error', names: [] });
            return;
          }
          const connected = new Set(settings.data.sources.filter((source) => source.type === 'knowledge-base').map((source) => source.id));
          const names = sources.data
            .filter((source) => source.type === 'knowledge-base' && source.permission === 'owner' && connected.has(source.id))
            .map((source) => source.name);
          this.dialogKnowledge.set({ status: 'ready', names });
        },
        error: () => this.dialogKnowledge.set({ status: 'error', names: [] }),
      });
  }

  protected closeDialog(): void {
    this.dialogRef?.close();
    this.dialogRef = null;
  }

  protected publishConfirmed(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.dialogError.set('');
    this.repository
      .publishWebsite(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          this.actionError.set('');
          if (result.status === 'publish-refused') {
            this.publishFailures.set(result.failures);
            this.publishRefusedMessage.set(result.message);
            this.actionStatus.set('');
            this.closeDialog();
          } else if (result.status === 'permission-denied') {
            this.actionError.set(result.message);
            this.closeDialog();
          } else if (result.status === 'ready' || result.status === 'partial-failure') {
            this.publishFailures.set([]);
            this.actionStatus.set('已發布官網嵌入。');
            this.closeDialog();
            this.changed.emit();
          }
        },
        error: () => {
          this.busy.set(false);
          this.dialogError.set('目前無法發布，請稍後再試。');
        },
      });
  }

  protected togglePause(): void {
    const pause = this.view().state !== 'paused';
    this.runAction(
      this.repository.setPublishingChannelPaused(this.assistantId(), 'website', pause),
      pause ? '已暫停官網嵌入，訪客目前看到「暫停服務」。' : '已恢復官網嵌入。',
      '目前無法變更官網嵌入的狀態，請稍後再試。',
    );
  }

  protected unpublish(): void {
    this.runAction(
      this.repository.unpublishWebsite(this.assistantId()),
      '已取消發布，設定與允許網域都保留；之後可以再發布。',
      '目前無法取消發布，請稍後再試。',
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

  protected async copy(): Promise<void> {
    const code = this.view().embedCode;
    if (code === null) return;
    try {
      await navigator.clipboard.writeText(code);
      this.copyStatus.set('已複製嵌入碼。');
    } catch {
      this.copyStatus.set('無法自動複製，請選取上方嵌入碼後手動複製。');
    }
  }
}
