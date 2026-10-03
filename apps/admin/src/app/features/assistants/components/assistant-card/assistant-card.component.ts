import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantSummaryView } from '../../../../core/domain/assistant.model';
import type { AssistantAcceptanceStatus } from '../../../../core/domain/assistant-acceptance.model';
import {
  StatusBadgeComponent,
  type StatusTone,
} from '../../../../shared/ui/status-badge/status-badge.component';

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
  'members-and-external-customers': '內部團隊與外部客戶',
};

const ACCEPTANCE_LABELS: Record<AssistantAcceptanceStatus, string> = {
  'not-accepted': '尚未驗收',
  passed: '驗收通過',
  failed: '驗收未通過',
  outdated: '驗收已過期',
};

const ACCEPTANCE_TONES: Record<AssistantAcceptanceStatus, StatusTone> = {
  'not-accepted': 'neutral',
  passed: 'success',
  failed: 'error',
  outdated: 'warning',
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

  protected acceptanceLabel(status: AssistantAcceptanceStatus | undefined): string {
    return ACCEPTANCE_LABELS[status ?? 'not-accepted'];
  }

  protected acceptanceTone(status: AssistantAcceptanceStatus | undefined): StatusTone {
    return ACCEPTANCE_TONES[status ?? 'not-accepted'];
  }

  protected audienceLabel(audience: AssistantSummaryView['audience']): string {
    return AUDIENCE_LABELS[audience];
  }
}
