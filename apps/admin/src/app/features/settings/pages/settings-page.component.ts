import { Component, inject } from '@angular/core';
import { COLOR_THEMES, ThemeService, texture } from '@smart-agri/theme-pack';
import { SettingRowComponent } from '@smart-agri/ui';
import { ChatModelPanelComponent } from '../components/chat-model-panel/chat-model-panel.component';
import { ConversationRetentionComponent } from '../components/conversation-retention/conversation-retention.component';
import { TeamPanelComponent } from '../components/team-panel/team-panel.component';
import { OrganizationSettingsChanges } from '../organization-settings-changes.service';

@Component({
  selector: 'app-settings-page',
  standalone: true,
  imports: [TeamPanelComponent, ChatModelPanelComponent, ConversationRetentionComponent, SettingRowComponent],
  templateUrl: './settings-page.component.html',
  styleUrl: './settings-page.component.scss',
  // 對話模型與保存期限共用 revision：一個區塊存好後，其他區塊重新讀取（issue #243）。
  providers: [OrganizationSettingsChanges],
})
export class SettingsPageComponent {
  protected readonly theme = inject(ThemeService);
  protected readonly texture = texture;
  protected readonly colorThemes = COLOR_THEMES;

  protected onParadigm(id: string): void {
    this.theme.setParadigm(id);
  }

  protected onTheme(id: string): void {
    this.theme.setTheme(id);
  }
}
