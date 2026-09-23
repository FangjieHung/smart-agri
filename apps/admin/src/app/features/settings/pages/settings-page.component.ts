import { Component, inject } from '@angular/core';
import { COLOR_THEMES, ThemeService, texture } from '@smart-agri/theme-pack';
import { TeamPanelComponent } from '../components/team-panel/team-panel.component';

@Component({
  selector: 'app-settings-page',
  standalone: true,
  imports: [TeamPanelComponent],
  templateUrl: './settings-page.component.html',
  styleUrl: './settings-page.component.scss',
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
