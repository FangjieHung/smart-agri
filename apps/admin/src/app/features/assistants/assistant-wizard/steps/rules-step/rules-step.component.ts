import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { PeriodicReportSchedule } from '../../../../../core/domain/assistant-draft.model';
import type { DatabaseId } from '../../../../../core/domain/database.model';
import { AssistantDraftStore } from '../../assistant-draft.store';

const REPORT_OPTIONS: readonly { readonly value: PeriodicReportSchedule; readonly label: string }[] = [
  { value: 'off', label: '不需要' },
  { value: 'weekly', label: '每週一次' },
  { value: 'monthly', label: '每月一次' },
];

@Component({
  selector: 'app-rules-step',
  templateUrl: './rules-step.component.html',
  styleUrl: '../wizard-step.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RulesStepComponent {
  protected readonly store = inject(AssistantDraftStore);
  protected readonly reportOptions = REPORT_OPTIONS;

  /** 只能寫入已連接到這個助理的資料庫。 */
  protected readonly writableDatabases = computed(() =>
    this.store.connectedSources().flatMap((source) =>
      source.type === 'database' ? [{ id: source.id, name: source.name }] : [],
    ),
  );

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected selectWriteTarget(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const target: DatabaseId | null =
      this.writableDatabases().find((database) => database.id === value)?.id ?? null;
    this.store.updateRules({ dataWriteDatabaseId: target });
  }

  protected selectReport(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.store.updateRules({
      periodicReport: REPORT_OPTIONS.find((option) => option.value === value)?.value ?? 'off',
    });
  }
}
