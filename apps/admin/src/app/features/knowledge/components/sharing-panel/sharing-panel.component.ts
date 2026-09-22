import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { AccountId } from '../../../../core/domain/account.model';
import type {
  KnowledgeShareTargetView,
  KnowledgeSharingScope,
  KnowledgeSharingView,
} from '../../../../core/domain/knowledge-base.model';
import { SHARING_SCOPE_LABELS } from '../knowledge-labels';

interface ScopeOption {
  readonly value: KnowledgeSharingScope;
  readonly label: string;
  readonly description: string;
}

const SCOPE_OPTIONS: readonly ScopeOption[] = [
  { value: 'private', label: SHARING_SCOPE_LABELS.private, description: '只有你可以查看、管理與連接。' },
  {
    value: 'specific-accounts',
    label: SHARING_SCOPE_LABELS['specific-accounts'],
    description: '只開放你勾選的帳號或團隊連接使用。',
  },
  { value: 'public', label: SHARING_SCOPE_LABELS.public, description: '任何帳號都能連接到自己的助理。' },
];

@Component({
  selector: 'app-sharing-panel',
  templateUrl: './sharing-panel.component.html',
  styleUrl: './sharing-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SharingPanelComponent {
  readonly sharing = input.required<KnowledgeSharingView>();
  readonly targets = input.required<readonly KnowledgeShareTargetView[]>();
  readonly save = output<KnowledgeSharingView>();

  protected readonly options = SCOPE_OPTIONS;
  protected readonly scope = linkedSignal(() => this.sharing().scope);
  protected readonly selected = linkedSignal<readonly AccountId[]>(
    () => this.sharing().sharedWithAccountIds,
  );
  protected readonly allowDownload = linkedSignal(() => this.sharing().allowOriginalDownload);
  protected readonly error = signal('');
  protected readonly selectedCount = computed(() => this.selected().length);

  protected chooseScope(scope: KnowledgeSharingScope): void {
    this.scope.set(scope);
    this.error.set('');
  }

  protected toggleAccount(id: AccountId, checked: boolean): void {
    this.selected.update((ids) =>
      checked ? [...ids.filter((existing) => existing !== id), id] : ids.filter((existing) => existing !== id),
    );
    this.error.set('');
  }

  protected isSelected(id: AccountId): boolean {
    return this.selected().includes(id);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const scope = this.scope();
    if (scope === 'specific-accounts' && this.selected().length === 0) {
      this.error.set('請至少選擇一個帳號或團隊，才能使用指定分享。');
      return;
    }

    this.save.emit({
      scope,
      sharedWithAccountIds: scope === 'specific-accounts' ? [...this.selected()] : [],
      allowOriginalDownload: scope === 'public' && this.allowDownload(),
    });
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }
}
