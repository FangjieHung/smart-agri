import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  TemplateRef,
  computed,
  contentChild,
  contentChildren,
  effect,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DataTableCellContext, DataTableCellDirective } from './data-table-cell.directive';
import { exportRows, exportTableElement } from './data-table-export';
import {
  DataTableBatchActionsDirective,
  DataTableBodyDirective,
  DataTableHeadDirective,
} from './data-table-slot.directives';
import { DataTableColumn, DataTableLabels, DataTableMobileMode } from './data-table.types';

type ResolvedColumn<T> = DataTableColumn<T> & { primary: boolean };

@Component({
  selector: 'lib-data-table',
  imports: [NgTemplateOutlet, MatButtonModule, MatTooltipModule],
  templateUrl: './data-table.component.html',
  styleUrl: './data-table.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableComponent<T> {
  readonly columns = input<DataTableColumn<T>[]>([]);
  readonly rows = input<readonly T[]>([]);
  // 下方拋出的字串是刻意保留的 developer diagnostics（開發期設定錯誤，不是使用者可見文案）：
  // 呼叫端忘記傳 [rowId] 或 dtHead/dtBody 沒有成對提供，此時應直接在開發環境炸出明確訊息，
  // 而不是靜默降級。這與「libs/ui 不得內建使用者可見字串」的約束不衝突，請勿把它們也搬進 labels。
  readonly rowId = input<(row: T) => unknown>((row) => {
    const id = (row as { id?: unknown }).id;
    if (id === undefined) {
      throw new Error('DataTable：資料列沒有 id 欄位，請傳入 [rowId] 指定識別欄位');
    }
    return id;
  });
  readonly mobile = input<DataTableMobileMode | null>(null);
  readonly exportName = input('export');
  readonly showExport = input(true);
  readonly emptyText = input('');
  readonly labels = input.required<DataTableLabels>();
  readonly selectable = input(false);
  /** 有些清單可批次選取／匯出，但不應提供通用的破壞性刪除操作。 */
  readonly showBatchDelete = input(true);
  readonly selection = input<readonly T[]>([]);
  readonly rowClickable = input(false);
  readonly rowClass = input<(row: T) => string>(() => '');

  readonly selectionChange = output<readonly T[]>();
  readonly batchDelete = output<readonly T[]>();
  readonly rowClick = output<T>();

  private readonly cellDirectives = contentChildren(DataTableCellDirective<T>);
  protected readonly headDirective = contentChild(DataTableHeadDirective);
  protected readonly bodyDirective = contentChild(DataTableBodyDirective);
  protected readonly batchActionsDirective = contentChild(DataTableBatchActionsDirective<T>);

  private readonly selectedIds = signal<ReadonlySet<unknown>>(new Set());

  protected readonly selectedRows = computed<readonly T[]>(() => {
    const ids = this.selectedIds();
    return this.rows().filter((row) => ids.has(this.rowId()(row)));
  });

  protected readonly selectedCount = computed(() => this.selectedRows().length);

  protected readonly allSelected = computed(() => {
    const rows = this.rows();
    return rows.length > 0 && this.selectedCount() === rows.length;
  });

  protected readonly partiallySelected = computed(() => {
    const count = this.selectedCount();
    return count > 0 && count < this.rows().length;
  });

  constructor() {
    effect(() => {
      const externalSelection = this.selection();
      const rows = untracked(() => this.rows());
      const rowIds = new Set(rows.map((row) => this.rowId()(row)));
      const nextIds = new Set(
        externalSelection
          .map((row) => this.rowId()(row))
          .filter((id) => rowIds.has(id)),
      );

      this.selectedIds.set(nextIds);
      const cleanedSelection = rows.filter((row) => nextIds.has(this.rowId()(row)));
      if (cleanedSelection.length !== externalSelection.length) {
        this.selectionChange.emit(cleanedSelection);
      }
    });

    effect(() => {
      const rows = this.rows();
      const previousIds = untracked(() => this.selectedIds());
      const rowIds = new Set(rows.map((row) => this.rowId()(row)));
      const nextIds = new Set([...previousIds].filter((id) => rowIds.has(id)));

      if (nextIds.size !== previousIds.size) {
        this.selectedIds.set(nextIds);
        this.selectionChange.emit(rows.filter((row) => nextIds.has(this.rowId()(row))));
      }
    });
  }

  /** dtHead 存在即進入逃生門模式：不讀 columns、不注入 data-label、不生卡片。 */
  protected readonly isCustom = computed(() => {
    const hasHead = this.headDirective() != null;
    const hasBody = this.bodyDirective() != null;
    if (hasHead !== hasBody) {
      throw new Error('DataTable：逃生門模式需同時提供 dtHead 與 dtBody');
    }
    return hasHead;
  });

  protected readonly mobileMode = computed<DataTableMobileMode>(
    () => this.mobile() ?? (this.isCustom() ? 'scroll' : 'cards'),
  );

  protected readonly cellTemplates = computed(() => {
    const map = new Map<string, TemplateRef<DataTableCellContext<T>>>();
    for (const directive of this.cellDirectives()) {
      map.set(directive.dtCell(), directive.template);
    }
    return map;
  });

  /** 沒有任何欄位標 primary 時，第一欄自動視為 primary，避免手機卡片整張空白。 */
  protected readonly resolvedColumns = computed<ResolvedColumn<T>[]>(() => {
    const cols = this.columns();
    const hasPrimary = cols.some((c) => c.primary);
    return cols.map((col, i) => ({ ...col, primary: hasPrimary ? !!col.primary : i === 0 }));
  });

  protected readonly isEmpty = computed(() => !this.isCustom() && this.rows().length === 0);

  protected cellContext(row: T): DataTableCellContext<T> {
    return { $implicit: row };
  }

  protected readonly hasSecondary = computed(() =>
    this.resolvedColumns().some((col) => !col.primary),
  );

  private readonly expandedIds = signal<ReadonlySet<unknown>>(new Set());

  protected isExpanded(row: T): boolean {
    return this.expandedIds().has(this.rowId()(row));
  }

  protected isSelected(row: T): boolean {
    return this.selectedIds().has(this.rowId()(row));
  }

  protected rowSelectionLabel(row: T): string {
    const firstColumn = this.resolvedColumns()[0];
    const visibleValue = firstColumn ? this.valueOf(row, firstColumn.key) : '';
    const rowIdentifier = String(this.rowId()(row));
    const parts = [this.labels().selectRow];
    if (visibleValue) parts.push(visibleValue);
    parts.push(rowIdentifier);
    return parts.join(' - ');
  }

  protected toggle(row: T): void {
    const id = this.rowId()(row);
    const next = new Set(this.expandedIds());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.expandedIds.set(next);
  }

  protected toggleRowSelection(row: T): void {
    const id = this.rowId()(row);
    const next = new Set(this.selectedIds());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.selectedIds.set(next);
    this.selectionChange.emit(this.rows().filter((current) => next.has(this.rowId()(current))));
  }

  protected onRowClick(row: T): void {
    if (!this.rowClickable()) return;
    this.rowClick.emit(row);
  }

  /**
   * 只處理「焦點在整列本身」時的 Enter／Space。列內的按鈕、選取 checkbox 也會把 keydown 冒泡上來——
   * 若一併當成整列點擊，既會多開一次詳情，preventDefault 還會吃掉按鈕自己的鍵盤觸發（Enter 按不動列內按鈕）。
   */
  protected onRowKeydown(row: T, event: Event): void {
    if (!this.rowClickable() || event.target !== event.currentTarget) return;
    event.preventDefault();
    this.rowClick.emit(row);
  }

  protected toggleAllSelection(): void {
    const rows = this.rows();
    const next = this.allSelected()
      ? new Set<unknown>()
      : new Set(rows.map((row) => this.rowId()(row)));
    this.selectedIds.set(next);
    this.selectionChange.emit(rows.filter((row) => next.has(this.rowId()(row))));
  }

  protected deleteSelected(): void {
    this.batchDelete.emit(this.selectedRows());
  }

  protected valueOf(row: T, key: string): string {
    const value = (row as Record<string, unknown>)[key];
    return value === null || value === undefined ? '' : String(value);
  }

  /** 匯出失敗（多半是 xlsx 動態載入失敗）時通知使用端顯示 snackbar。 */
  readonly exportFailed = output<Error>();

  private readonly tableEl = viewChild<ElementRef<HTMLTableElement>>('tableEl');

  protected readonly canExport = computed(() => this.showExport() && !this.isEmpty());

  protected async runExport(): Promise<void> {
    try {
      if (this.isCustom()) {
        const el = this.tableEl()?.nativeElement;
        if (!el) {
          throw new Error('DataTable：找不到 table 節點，無法匯出');
        }
        await exportTableElement(el, this.exportName());
      } else {
        await exportRows(this.columns(), this.rows(), this.exportName());
      }
    } catch (e) {
      this.exportFailed.emit(e as Error);
    }
  }

  protected async runSelectedExport(): Promise<void> {
    try {
      await exportRows(this.columns(), this.selectedRows(), `${this.exportName()}-selected`);
    } catch (e) {
      this.exportFailed.emit(e as Error);
    }
  }
}
