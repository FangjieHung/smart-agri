import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'app-assistant-wizard-placeholder',
  imports: [RouterLink, PageHeaderComponent],
  template: `
    <section class="workspace-page">
      <app-page-header
        title="建立新助理"
        description="建立流程會從用途、資料來源、回答規則到試用確認，逐步帶你完成。"
      />
      <section class="placeholder-panel" aria-labelledby="wizard-placeholder-title">
        <p>下一個畫面任務會在此提供四步驟建立精靈。</p>
        <h2 id="wizard-placeholder-title">你可以先回到助理列表整理既有設定</h2>
        <a class="ui-button" routerLink="/app/assistants">返回我的助理</a>
      </section>
    </section>
  `,
  styles: `
    :host, .workspace-page { display: block; }
    .placeholder-panel { display: grid; gap: var(--space-4); max-width: var(--reading-width); padding: var(--space-6); border: var(--border-default); border-radius: var(--radius-container); background: var(--color-surface); }
    h2, p { margin: 0; } h2 { color: var(--color-text); font-size: var(--font-size-heading); } p { color: var(--color-text-secondary); line-height: var(--line-height-body); }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantWizardPlaceholderComponent {}
