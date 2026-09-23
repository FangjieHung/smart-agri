import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import { fmtDateTime } from '../../../../core/date-utils';
import type {
  ConnectableSourcePermission,
  ConnectableSourceStatus,
  ConnectableSourceView,
} from '../../../../core/domain/assistant-draft.model';
import type { AssistantSourceReference } from '../../../../core/domain/assistant.model';
import { SourceTypeIconComponent } from '../../../../shared/ui/source-type-icon/source-type-icon.component';
import {
  StatusBadgeComponent,
  type StatusTone,
} from '../../../../shared/ui/status-badge/status-badge.component';

type SourceTypeFilter = 'all' | ConnectableSourceView['type'];

const TYPE_FILTERS: readonly { readonly value: SourceTypeFilter; readonly label: string }[] = [
  { value: 'all', label: '全部' },
  { value: 'knowledge-base', label: '知識庫' },
  { value: 'database', label: '資料庫' },
];

const STATUS_LABELS: Record<ConnectableSourceStatus, string> = {
  ready: '可使用',
  processing: '處理中',
  'needs-attention': '需要處理',
};

const STATUS_TONES: Record<ConnectableSourceStatus, StatusTone> = {
  ready: 'success',
  processing: 'info',
  'needs-attention': 'warning',
};

const PERMISSION_LABELS: Record<ConnectableSourcePermission, string> = {
  owner: '我建立的・可管理',
  'read-only': '唯讀連接',
};

/**
 * 知識庫與資料庫混合的連接清單。清單內容一律由呼叫端提供，元件不自己讀 repository，
 * 所以只會顯示呼叫端已經過濾過、該帳號看得到的來源。
 */
@Component({
  selector: 'app-source-connection-list',
  imports: [SourceTypeIconComponent, StatusBadgeComponent],
  templateUrl: './source-connection-list.component.html',
  styleUrls: ['../assistant-form.scss', './source-connection-list.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SourceConnectionListComponent {
  readonly sources = input.required<readonly ConnectableSourceView[]>();
  /** 已連接的來源，只有 id 與類型。 */
  readonly connected = input.required<readonly AssistantSourceReference[]>();
  readonly toggled = output<ConnectableSourceView>();

  protected readonly typeFilters = TYPE_FILTERS;
  protected readonly typeFilter = signal<SourceTypeFilter>('all');
  protected readonly query = signal('');

  protected readonly visibleSources = computed(() => {
    const type = this.typeFilter();
    const query = this.query().trim().toLowerCase();
    return this.sources()
      .filter((source) => type === 'all' || source.type === type)
      .filter(
        (source) =>
          query === '' ||
          `${source.name} ${source.summary}`.toLowerCase().includes(query),
      );
  });

  protected readonly summary = computed(() => {
    const connected = this.connected();
    const knowledge = connected.filter((source) => source.type === 'knowledge-base').length;
    return `已連接 ${knowledge} 個知識庫、${connected.length - knowledge} 個資料庫`;
  });

  protected isConnected(source: ConnectableSourceView): boolean {
    return this.connected().some(
      (candidate) => candidate.id === source.id && candidate.type === source.type,
    );
  }

  protected statusLabel(status: ConnectableSourceStatus): string {
    return STATUS_LABELS[status];
  }

  protected statusTone(status: ConnectableSourceStatus): StatusTone {
    return STATUS_TONES[status];
  }

  protected permissionLabel(permission: ConnectableSourcePermission): string {
    return PERMISSION_LABELS[permission];
  }

  protected updatedAt(iso: string): string {
    return fmtDateTime(iso);
  }

  protected setQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
