import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantSummaryView } from '../../../../core/domain/assistant.model';
import { StatusBadgeComponent, type StatusTone } from '../../../../shared/ui/status-badge/status-badge.component';

const STATUS_LABELS: Record<AssistantSummaryView['status'], string> = {
  draft: '草稿',
  ready: '可發布',
  published: '已發布',
  paused: '已暫停',
};

const STATUS_TONES: Record<AssistantSummaryView['status'], StatusTone> = {
  draft: 'neutral',
  ready: 'info',
  published: 'success',
  paused: 'warning',
};

const AUDIENCE_LABELS: Record<AssistantSummaryView['audience'], string> = {
  'account-members': '內部團隊',
  'authorized-external-customers': '已授權外部客戶',
};

@Component({
  selector: 'app-assistant-card',
  imports: [RouterLink, StatusBadgeComponent],
  templateUrl: './assistant-card.component.html',
  styleUrl: './assistant-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantCardComponent {
  readonly assistant = input.required<AssistantSummaryView>();
  readonly channels = input<readonly string[]>([]);
  readonly recentActivity = input.required<string>();

  protected statusLabel(status: AssistantSummaryView['status']): string {
    return STATUS_LABELS[status];
  }

  protected statusTone(status: AssistantSummaryView['status']): StatusTone {
    return STATUS_TONES[status];
  }

  protected audienceLabel(audience: AssistantSummaryView['audience']): string {
    return AUDIENCE_LABELS[audience];
  }
}
