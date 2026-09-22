import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import {
  WEBSITE_BRAND_COLORS,
  WEBSITE_LAUNCHER_POSITIONS,
  normalizeDomain,
  validateAllowedDomain,
  type PublishingFieldError,
  type WebsiteBrandColor,
  type WebsiteEmbedSettings,
  type WebsiteEmbedView,
  type WebsiteLauncherPosition,
} from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

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

/** 官網嵌入：外觀設定、桌機／手機預覽、允許網域與示範嵌入碼。不會連線到任何網站。 */
@Component({
  selector: 'app-website-embed',
  templateUrl: './website-embed.component.html',
  styleUrl: './website-embed.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WebsiteEmbedComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);

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
  protected readonly brandHex = computed(
    () => this.colors.find((color) => color.id === this.draft().brandColor)?.hex ?? this.colors[0].hex,
  );
  protected readonly installText = computed(() => {
    const view = this.view();
    if (view.allowedDomains.length === 0) return '請先設定並儲存允許嵌入的網域。';
    if (view.installCheck === 'detected') return `已在允許的網域偵測到嵌入碼（模擬結果）。`;
    if (view.installCheck === 'not-detected') {
      return '偵測不到嵌入碼（模擬結果）。請確認已放上嵌入碼後重新檢查；其他管道不受影響。';
    }
    return '尚未檢查安裝狀態（檢查為模擬結果，不會連線到網站）。';
  });

  protected anchorFor(field: string): string {
    return FIELD_ANCHORS[field] ?? 'website-display-name';
  }

  protected labelFor(field: string): string {
    return FIELD_LABELS[field] ?? field;
  }

  protected hasError(field: string): boolean {
    return this.errors().some((error) => error.field === field);
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
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const result = this.repository.updateWebsiteEmbed(accountId, this.assistantId(), this.draft());
    if (result.status === 'validation-failed') {
      this.errors.set(result.errors);
      this.saveStatus.set('');
      return;
    }
    this.errors.set([]);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.saveStatus.set(
        result.data.installCheck === 'not-checked' && result.data.allowedDomains.length > 0
          ? '已儲存官網設定。網域已變更，請重新檢查安裝狀態。'
          : '已儲存官網設定。',
      );
      this.changed.emit();
    }
  }

  protected checkInstallation(): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    this.repository.checkWebsiteInstallation(accountId, this.assistantId());
    this.changed.emit();
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.view().embedCode);
      this.copyStatus.set('已複製嵌入碼。提醒：這是 Demo 嵌入碼，不可用於正式環境。');
    } catch {
      this.copyStatus.set('無法自動複製，請選取上方嵌入碼後手動複製。');
    }
  }
}
