import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantAudience, AssistantStatus } from '../../../../../core/domain/assistant.model';
import {
  StatusBadgeComponent,
  type StatusTone,
} from '../../../../../shared/ui/status-badge/status-badge.component';
import { AssistantProfileFormComponent } from '../../../components/assistant-profile-form/assistant-profile-form.component';
import { AssistantSettingsStore } from '../../assistant-settings.store';

const STATUS: Record<AssistantStatus, { readonly label: string; readonly tone: StatusTone }> = {
  draft: { label: '草稿', tone: 'neutral' },
  ready: { label: '可發布', tone: 'info' },
  published: { label: '已發布', tone: 'success' },
  paused: { label: '已暫停', tone: 'warning' },
};

const AUDIENCE_LABELS: Record<AssistantAudience, string> = {
  'account-members': '內部員工',
  'authorized-external-customers': '外部客戶',
  'members-and-external-customers': '內部員工與外部客戶',
};

/**
 * 建立後的「概覽」：和建立精靈步驟一同一組欄位，外加助理目前的狀態與其餘設定摘要。
 * 這裡沒有模板選擇——模板只是建立當下的預填，對已存在的助理套用會把使用者改過的內容蓋掉。
 */
@Component({
  selector: 'app-assistant-overview-tab',
  imports: [RouterLink, StatusBadgeComponent, AssistantProfileFormComponent],
  templateUrl: './assistant-overview-tab.component.html',
  styleUrls: ['../../../components/assistant-form.scss', './assistant-overview-tab.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantOverviewTabComponent {
  protected readonly store = inject(AssistantSettingsStore);

  protected readonly profile = computed(() => {
    const settings = this.store.settings();
    return settings === null
      ? null
      : {
          name: settings.configuration.name,
          purpose: settings.configuration.purpose,
          tone: settings.tone,
          roleInstructions: settings.roleInstructions,
          audience: settings.configuration.audience,
        };
  });

  protected readonly errors = computed(() => ({
    name: this.store.fieldError('name'),
    purpose: this.store.fieldError('purpose'),
  }));

  protected readonly status = computed(() => {
    const settings = this.store.settings();
    return settings === null ? null : STATUS[settings.configuration.status];
  });

  protected readonly summary = computed(() => {
    const settings = this.store.settings();
    if (settings === null) return null;

    const knowledge = settings.sources.filter(
      (source) => source.type === 'knowledge-base',
    ).length;
    return {
      audience: AUDIENCE_LABELS[settings.configuration.audience],
      sources: `${knowledge} 個知識庫、${settings.sources.length - knowledge} 個資料庫`,
      scope:
        settings.rules.knowledgeScope === 'company-data-only'
          ? '只依據我的資料回答'
          : '允許補充一般知識',
      conversations: settings.rules.keepOwnConversations
        ? '保存使用者自己的對話'
        : '不保存使用者的對話',
    };
  });
}
