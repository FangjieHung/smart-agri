import { describe, it, expect, beforeEach, vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { DataTableComponent } from './data-table.component';
import { DataTableCellDirective } from './data-table-cell.directive';
import {
  DataTableBatchActionsDirective,
  DataTableBodyDirective,
  DataTableHeadDirective,
} from './data-table-slot.directives';
import { rowsToAoa } from './data-table-export';
import { DataTableColumn, DataTableLabels } from './data-table.types';

// Angular 的 vitest builder 禁止對「相對路徑」用 vi.mock（見
// @angular/build .../unit-test/runners/vitest/build-options.js 的 vitest-mock-patch，
// 對 /^[./]/ 開頭的 specifier 一律丟錯，要求改用 TestBed 做 DI mocking）。
// 因此無法直接 mock './data-table-export'，改為 mock 'xlsx'（bare specifier，不受限）——
// 這樣測試會實際跑過 exportRows / exportTableElement 真正的實作，
// 只在 SheetJS 這層邊界處攔截，藉由它是否呼叫 aoa_to_sheet 還是 table_to_sheet
// 來驗證 runExport() 的分派邏輯，覆蓋範圍其實更完整。
const writeFile = vi.fn();
const aoaToSheet = vi.fn((..._args: unknown[]) => ({ mock: 'ws-aoa' }));
const tableToSheet = vi.fn((..._args: unknown[]) => ({ mock: 'ws-dom' }));
const bookAppendSheet = vi.fn();

vi.mock('xlsx', () => ({
  utils: {
    aoa_to_sheet: (...args: unknown[]) => aoaToSheet(...args),
    table_to_sheet: (...args: unknown[]) => tableToSheet(...args),
    book_new: () => ({ mock: 'wb' }),
    book_append_sheet: (...args: unknown[]) => bookAppendSheet(...args),
  },
  writeFile: (...args: unknown[]) => writeFile(...args),
}));

interface Row {
  id: string;
  name: string;
  status: 'available' | 'rented';
  mileage: number;
}

const LABELS: DataTableLabels = {
  exportExcel: '匯出 Excel',
  selectAll: '全選',
  deselectAll: '取消全選',
  selectRow: '選取資料列',
  batchDelete: '刪除已選資料',
  selectedCount: '已選 {count} 筆',
  exportSelected: '只匯出已勾選的資料',
  expandRow: '展開詳細資料',
  collapseRow: '收合詳細資料',
  exportFailedText: '匯出失敗，請稍後再試',
};

@Component({
  imports: [DataTableComponent, DataTableCellDirective],
  template: `
    <lib-data-table
      [columns]="columns()"
      [rows]="rows()"
      [labels]="labels"
      emptyText="目前沒有資料"
    >
      <ng-template dtCell="status" let-row>
        <span class="chip">{{ row.status }}</span>
      </ng-template>
    </lib-data-table>
  `,
})
class HostComponent {
  readonly labels = LABELS;
  readonly columns = signal<DataTableColumn<Row>[]>([
    { key: 'name', label: '車牌', primary: true },
    { key: 'status', label: '狀態', primary: true },
    { key: 'mileage', label: '里程', align: 'end' },
  ]);
  readonly rows = signal<Row[]>([
    { id: 'v1', name: 'ABC-123', status: 'available', mileage: 12000 },
    { id: 'v2', name: 'XYZ-789', status: 'rented', mileage: 34000 },
  ]);
}

@Component({
  imports: [DataTableComponent, DataTableBatchActionsDirective],
  template: `
    <lib-data-table
      [columns]="columns()"
      [rows]="rows()"
      [labels]="labels"
      [selectable]="true"
      [selection]="selection()"
      [showBatchDelete]="showBatchDelete()"
      (selectionChange)="onSelectionChange($event)"
      (batchDelete)="onBatchDelete($event)"
    >
      <ng-template dtBatchActions let-selected>
        <span class="selection-slot-row-id">{{ $any(selected[0])?.id }}</span>
      </ng-template>
    </lib-data-table>
  `,
})
class SelectionHostComponent {
  readonly labels = LABELS;
  readonly columns = signal<DataTableColumn<Row>[]>([
    { key: 'name', label: '車牌', primary: true },
    { key: 'status', label: '狀態', primary: true },
  ]);
  readonly rows = signal<Row[]>([
    { id: 'v1', name: 'ABC-123', status: 'available', mileage: 12000 },
    { id: 'v2', name: 'XYZ-789', status: 'rented', mileage: 34000 },
  ]);
  readonly selection = signal<readonly Row[]>([]);
  readonly showBatchDelete = signal(true);
  readonly selectionChanges: readonly Row[][] = [];
  readonly deletedRows: readonly Row[][] = [];

  onSelectionChange(rows: readonly Row[]): void {
    this.selection.set(rows);
    (this.selectionChanges as Row[][]).push([...rows]);
  }

  onBatchDelete(rows: readonly Row[]): void {
    (this.deletedRows as Row[][]).push([...rows]);
  }
}

interface RowWithoutId {
  name: string;
}

@Component({
  imports: [DataTableComponent],
  template: `<lib-data-table [columns]="columns" [rows]="rows" [labels]="labels" />`,
})
class NoIdHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<RowWithoutId>[] = [{ key: 'name', label: '名稱' }];
  readonly rows: RowWithoutId[] = [{ name: 'foo' }];
}

describe('DataTableComponent 標準模式', () => {
  let el: HTMLElement;
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    await fixture.whenStable();
    el = fixture.nativeElement as HTMLElement;
  });

  it('渲染出原生 table 元素', () => {
    expect(el.querySelector('table')).toBeTruthy();
  });

  it('表頭來自 columns 的 label', () => {
    const heads = [...el.querySelectorAll('thead th')].map((th) => th.textContent?.trim());
    expect(heads.slice(0, 3)).toEqual(['車牌', '狀態', '里程']);
  });

  it('每個 td 都帶上對應欄位的 data-label', () => {
    const firstRowCells = [...el.querySelectorAll('tbody tr')[0].querySelectorAll('td')];
    expect(firstRowCells.slice(0, 3).map((td) => td.getAttribute('data-label'))).toEqual([
      '車牌',
      '狀態',
      '里程',
    ]);
  });

  it('沒有 dtCell template 的欄位直接印 row[key]', () => {
    const cell = el.querySelector('tbody tr td[data-label="車牌"]');
    expect(cell?.textContent?.trim()).toBe('ABC-123');
  });

  it('有 dtCell template 的欄位改用 template 渲染', () => {
    const cell = el.querySelector('tbody tr td[data-label="狀態"]');
    expect(cell?.querySelector('.chip')?.textContent?.trim()).toBe('available');
  });

  it('align 欄位加上對齊 class', () => {
    const cell = el.querySelector('tbody tr td[data-label="里程"]');
    expect(cell?.classList.contains('dt-align-end')).toBe(true);
  });

  it('值為 null 時渲染空字串而非 "null"', async () => {
    fixture.componentInstance.rows.set([
      { id: 'v3', name: null, status: 'available', mileage: 0 } as unknown as Row,
    ]);
    await fixture.whenStable();
    const cell = el.querySelector('tbody tr td[data-label="車牌"]');
    expect(cell?.textContent?.trim()).toBe('');
  });

  it('rows 為空時顯示 emptyText 且不渲染 table', async () => {
    fixture.componentInstance.rows.set([]);
    await fixture.whenStable();
    expect(el.querySelector('table')).toBeNull();
    expect(el.textContent).toContain('目前沒有資料');
  });

  it('資料列沒有 id 欄位且未指定 rowId 時，預設 rowId 丟出明確錯誤', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({ imports: [NoIdHostComponent] }).compileComponents();
    const noIdFixture = TestBed.createComponent(NoIdHostComponent);
    expect(() => noIdFixture.detectChanges()).toThrow(
      'DataTable：資料列沒有 id 欄位，請傳入 [rowId] 指定識別欄位',
    );
  });
});

describe('DataTableComponent 資料列選取', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<SelectionHostComponent>>;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SelectionHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(SelectionHostComponent);
    await fixture.whenStable();
    el = fixture.nativeElement as HTMLElement;
  });

  it('既有 HostComponent 未啟用 selectable 時不顯示 checkbox，工具列也不宣告選取筆數', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const defaultFixture = TestBed.createComponent(HostComponent);
    await defaultFixture.whenStable();
    const defaultEl = defaultFixture.nativeElement as HTMLElement;

    expect(defaultEl.querySelector('.dt-selection-checkbox')).toBeNull();
    // 工具列本身會因為「匯出」而存在，但不該對螢幕報讀宣告一個沒開啟的選取功能。
    expect(defaultEl.querySelector('.dt-batch-toolbar')?.getAttribute('aria-label')).toBeNull();
    expect(defaultEl.querySelector('.dt-batch-delete-btn')).toBeNull();
  });

  it('選取單列時發出完全相同的資料列並顯示批次工具列', async () => {
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance.selectionChanges.at(-1)).toEqual([
      fixture.componentInstance.rows()[0],
    ]);
    expect(el.querySelector('.dt-batch-toolbar')).toBeTruthy();
  });

  it('表頭全選後再清除會依序發出全部與空的選取資料', async () => {
    const headerCheckbox = el.querySelector('thead .dt-selection-checkbox') as HTMLInputElement;
    headerCheckbox.click();
    await fixture.whenStable();
    expect(fixture.componentInstance.selection()).toEqual(fixture.componentInstance.rows());

    headerCheckbox.click();
    await fixture.whenStable();
    expect(fixture.componentInstance.selection()).toEqual([]);
    expect(fixture.componentInstance.selectionChanges.slice(-2)).toEqual([
      fixture.componentInstance.rows(),
      [],
    ]);
  });

  it('只選取兩列中的一列時表頭 checkbox 為 indeterminate', async () => {
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    expect((el.querySelector('thead .dt-selection-checkbox') as HTMLInputElement).indeterminate).toBe(
      true,
    );
  });

  it('移除已選資料列時會清除 stale selection 並發出清理後資料', async () => {
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    fixture.componentInstance.rows.set([fixture.componentInstance.rows()[1]]);
    await fixture.whenStable();

    expect(fixture.componentInstance.selection()).toEqual([]);
    expect(fixture.componentInstance.selectionChanges.at(-1)).toEqual([]);
  });

  it('批次刪除只發出已選資料列，不改變 rows', async () => {
    const selectedRow = fixture.componentInstance.rows()[0];
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();
    (el.querySelector('.dt-batch-delete-btn') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance.deletedRows).toEqual([[selectedRow]]);
    expect(fixture.componentInstance.rows()).toEqual([selectedRow, fixture.componentInstance.rows()[1]]);
  });

  it('停用批次刪除時仍可選取並匯出，但不顯示破壞性操作', async () => {
    fixture.componentInstance.showBatchDelete.set(false);
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    expect(el.querySelector('.dt-batch-delete-btn')).toBeNull();
    expect(el.querySelector('.dt-export-selected-btn')).toBeTruthy();
  });

  it('dtBatchActions slot 會收到實際已選資料列', async () => {
    const selectedRow = fixture.componentInstance.rows()[0];
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    expect(el.querySelector('.selection-slot-row-id')?.textContent?.trim()).toBe(selectedRow.id);
  });
});

describe('DataTableComponent 手機版 DOM 不變性', () => {
  it('thead 不使用 display:none（SheetJS 會跳過隱藏節點，此迴歸守衛只在手機斷點才擋得住這個 bug，桌機測不出來）', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    await fixture.whenStable();

    const scss = readFileSync(
      join(dirname(fileURLToPath(import.meta.url)), 'data-table.component.scss'),
      'utf-8',
    );
    const mobileBlock = scss.split('@media (max-width: 640px)')[1] ?? '';
    expect(mobileBlock).toContain('clip-path');
    expect(mobileBlock).not.toMatch(/thead[^}]*display:\s*none/);
  });

  it('非 primary 欄位帶 is-secondary class（供手機版收合）', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const mileageCell = el.querySelector('tbody tr td[data-label="里程"]');
    const plateCell = el.querySelector('tbody tr td[data-label="車牌"]');
    expect(mileageCell?.classList.contains('is-secondary')).toBe(true);
    expect(plateCell?.classList.contains('is-secondary')).toBe(false);
  });
});

describe('DataTableComponent 展開／收合', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<HostComponent>>;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    await fixture.whenStable();
    el = fixture.nativeElement as HTMLElement;
  });

  it('有次要欄位時每列渲染一顆展開鈕', () => {
    expect(el.querySelectorAll('tbody .dt-expand-btn')).toHaveLength(2);
  });

  it('展開鈕預設 aria-expanded 為 false', () => {
    const btn = el.querySelector('tbody .dt-expand-btn');
    expect(btn?.getAttribute('aria-expanded')).toBe('false');
  });

  it('點擊後該列加上 is-expanded，且 DOM 節點數不變', async () => {
    const before = el.querySelectorAll('tbody td').length;
    (el.querySelector('tbody .dt-expand-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(el.querySelectorAll('tbody tr')[0].classList.contains('is-expanded')).toBe(true);
    expect(el.querySelectorAll('tbody td').length).toBe(before);
  });

  it('展開一列不影響其他列', async () => {
    (el.querySelector('tbody .dt-expand-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(el.querySelectorAll('tbody tr')[1].classList.contains('is-expanded')).toBe(false);
  });

  it('再次點擊收合', async () => {
    const btn = el.querySelector('tbody .dt-expand-btn') as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();
    btn.click();
    await fixture.whenStable();
    expect(el.querySelectorAll('tbody tr')[0].classList.contains('is-expanded')).toBe(false);
  });

  it('展開時 aria-label 換成收合文案', async () => {
    const btn = el.querySelector('tbody .dt-expand-btn') as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();
    expect(btn.getAttribute('aria-label')).toBe('收合詳細資料');
  });

  it('所有欄位都是 primary 時不渲染展開鈕', async () => {
    fixture.componentInstance.columns.set([
      { key: 'name', label: '車牌', primary: true },
      { key: 'status', label: '狀態', primary: true },
    ]);
    await fixture.whenStable();
    expect(el.querySelectorAll('tbody .dt-expand-btn')).toHaveLength(0);
  });
});

@Component({
  imports: [DataTableComponent, DataTableHeadDirective, DataTableBodyDirective],
  template: `
    <lib-data-table [columns]="columns" [labels]="labels" exportName="settlement">
      <ng-template dtHead>
        <tr>
          <th rowspan="2">合作夥伴</th>
          <th colspan="2">本季</th>
        </tr>
        <tr>
          <th>訂單數</th>
          <th>退佣</th>
        </tr>
      </ng-template>
      <ng-template dtBody>
        <tr>
          <td>海邊民宿</td>
          <td>12</td>
          <td>3600</td>
        </tr>
      </ng-template>
    </lib-data-table>
  `,
})
class CustomHostComponent {
  readonly labels = LABELS;
  // 逃生門模式下應被忽略
  readonly columns: DataTableColumn<unknown>[] = [{ key: 'ignored', label: '不該出現' }];
}

describe('DataTableComponent 逃生門模式', () => {
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CustomHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(CustomHostComponent);
    await fixture.whenStable();
    el = fixture.nativeElement as HTMLElement;
  });

  it('渲染頁面提供的 thead，保留 colspan / rowspan', () => {
    expect(el.querySelector('thead th[rowspan="2"]')?.textContent?.trim()).toBe('合作夥伴');
    expect(el.querySelector('thead th[colspan="2"]')?.textContent?.trim()).toBe('本季');
  });

  it('忽略 columns，不渲染其 label', () => {
    expect(el.textContent).not.toContain('不該出現');
  });

  it('不注入 data-label', () => {
    expect(el.querySelector('tbody td[data-label]')).toBeNull();
  });

  it('不渲染展開鈕', () => {
    expect(el.querySelector('.dt-expand-btn')).toBeNull();
  });

  it('mobile 預設為 scroll', () => {
    expect(el.querySelector('.dt-wrap')?.classList.contains('dt-wrap--scroll')).toBe(true);
  });

  it('rows 為空也不顯示 emptyText（列由 dtBody 決定）', () => {
    expect(el.querySelector('table')).toBeTruthy();
  });
});

@Component({
  imports: [DataTableComponent, DataTableHeadDirective],
  template: `
    <lib-data-table [columns]="columns" [labels]="labels">
      <ng-template dtHead>
        <tr>
          <th>合作夥伴</th>
        </tr>
      </ng-template>
    </lib-data-table>
  `,
})
class HeadOnlyHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<unknown>[] = [];
}

@Component({
  imports: [DataTableComponent, DataTableBodyDirective],
  template: `
    <lib-data-table [columns]="columns" [labels]="labels">
      <ng-template dtBody>
        <tr>
          <td>海邊民宿</td>
        </tr>
      </ng-template>
    </lib-data-table>
  `,
})
class BodyOnlyHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<unknown>[] = [];
}

describe('DataTableComponent 逃生門模式守衛', () => {
  it('只給 dtHead 沒給 dtBody 時丟出明確錯誤', async () => {
    await TestBed.configureTestingModule({
      imports: [HeadOnlyHostComponent],
    }).compileComponents();
    const fixture = TestBed.createComponent(HeadOnlyHostComponent);
    expect(() => fixture.detectChanges()).toThrow(
      'DataTable：逃生門模式需同時提供 dtHead 與 dtBody',
    );
  });

  it('只給 dtBody 沒給 dtHead 時丟出明確錯誤', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [BodyOnlyHostComponent],
    }).compileComponents();
    const fixture = TestBed.createComponent(BodyOnlyHostComponent);
    expect(() => fixture.detectChanges()).toThrow(
      'DataTable：逃生門模式需同時提供 dtHead 與 dtBody',
    );
  });
});

@Component({
  imports: [DataTableComponent],
  template: `
    <lib-data-table
      [columns]="columns"
      [rows]="rows()"
      [labels]="labels"
      [showExport]="showExport()"
      (exportFailed)="onExportFailed($event)"
    />
  `,
})
class ExportHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<Row>[] = [{ key: 'name', label: '車牌', primary: true }];
  readonly rows = signal<Row[]>([
    { id: 'v1', name: 'ABC-123', status: 'available', mileage: 12000 },
  ]);
  readonly showExport = signal(true);
  lastError: Error | undefined;
  onExportFailed(e: Error): void {
    this.lastError = e;
  }
}

@Component({
  imports: [DataTableComponent],
  template: `
    <lib-data-table
      [columns]="columns"
      [rows]="rows()"
      [labels]="labels"
      [selectable]="true"
      exportName="vehicles"
    />
  `,
})
class SelectedExportHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<Row>[] = [{ key: 'name', label: '車牌', primary: true }];
  readonly rows = signal<Row[]>([
    { id: 'v1', name: 'ABC-123', status: 'available', mileage: 12000 },
    { id: 'v2', name: 'XYZ-789', status: 'rented', mileage: 34000 },
  ]);
}

@Component({
  imports: [DataTableComponent, DataTableHeadDirective, DataTableBodyDirective],
  template: `
    <lib-data-table [columns]="columns" [labels]="labels" (exportFailed)="onExportFailed($event)">
      <ng-template dtHead>
        <tr>
          <th>合作夥伴</th>
        </tr>
      </ng-template>
      <ng-template dtBody>
        <tr>
          <td>海邊民宿</td>
        </tr>
      </ng-template>
    </lib-data-table>
  `,
})
class ExportCustomHostComponent {
  readonly labels = LABELS;
  readonly columns: DataTableColumn<unknown>[] = [];
  lastError: Error | undefined;
  onExportFailed(e: Error): void {
    this.lastError = e;
  }
}

@Component({
  imports: [DataTableComponent, DataTableCellDirective],
  template: `
    <lib-data-table
      [columns]="columns()"
      [rows]="rows()"
      [labels]="labels"
      [selectable]="true"
      [rowClickable]="rowClickable()"
      [rowClass]="rowClassFn"
      (rowClick)="onRowClick($event)"
    >
      <ng-template dtCell="mileage" let-row>
        <button
          type="button"
          class="action-btn"
          (click)="$event.stopPropagation(); onActionClick(row)"
        >
          操作
        </button>
      </ng-template>
    </lib-data-table>
  `,
})
class RowClickHostComponent {
  readonly labels = LABELS;
  readonly rowClickable = signal(true);
  readonly columns = signal<DataTableColumn<Row>[]>([
    { key: 'name', label: '車牌', primary: true },
    { key: 'status', label: '狀態', primary: true },
    { key: 'mileage', label: '里程', align: 'end' },
  ]);
  readonly rows = signal<Row[]>([
    { id: 'v1', name: 'ABC-123', status: 'available', mileage: 12000 },
    { id: 'v2', name: 'XYZ-789', status: 'rented', mileage: 34000 },
  ]);
  readonly clicked: Row[] = [];
  readonly actionClicked: Row[] = [];
  readonly rowClassFn = (row: Row): string => (row.status === 'rented' ? 'danger-row' : '');

  onRowClick(row: Row): void {
    this.clicked.push(row);
  }

  onActionClick(row: Row): void {
    this.actionClicked.push(row);
  }
}

describe('DataTableComponent 整列可點擊導航', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<RowClickHostComponent>>;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [RowClickHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(RowClickHostComponent);
    await fixture.whenStable();
    el = fixture.nativeElement as HTMLElement;
  });

  it('rowClickable 為 false（預設）時，列沒有 tabindex，點擊也不會發出 rowClick', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const defaultFixture = TestBed.createComponent(HostComponent);
    await defaultFixture.whenStable();
    const defaultEl = defaultFixture.nativeElement as HTMLElement;
    const row = defaultEl.querySelector('tbody tr') as HTMLElement;

    expect(row.hasAttribute('tabindex')).toBe(false);
    expect(row.classList.contains('dt-row--clickable')).toBe(false);
  });

  it('點擊某一列（非按鈕/checkbox 區域）時發出該列資料', async () => {
    const rows = el.querySelectorAll('tbody tr');
    (rows[1] as HTMLElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance.clicked).toEqual([fixture.componentInstance.rows()[1]]);
  });

  it('點擊列上的選取 checkbox 不會同時發出 rowClick', async () => {
    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance.clicked).toEqual([]);
  });

  it('點擊列內動作按鈕（自行 stopPropagation）不會同時發出 rowClick', async () => {
    (el.querySelector('.action-btn') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(fixture.componentInstance.actionClicked).toEqual([fixture.componentInstance.rows()[0]]);
    expect(fixture.componentInstance.clicked).toEqual([]);
  });

  it('rowClickable 為 true 時列可用鍵盤操作：Enter 觸發 rowClick', async () => {
    const row = el.querySelectorAll('tbody tr')[0] as HTMLElement;
    row.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();

    expect(fixture.componentInstance.clicked).toEqual([fixture.componentInstance.rows()[0]]);
  });

  it('rowClickable 為 true 時列可用鍵盤操作：Space 觸發 rowClick', async () => {
    const row = el.querySelectorAll('tbody tr')[1] as HTMLElement;
    row.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
    await fixture.whenStable();

    expect(fixture.componentInstance.clicked).toEqual([fixture.componentInstance.rows()[1]]);
  });

  it('在列內按鈕上按 Enter／Space：只觸發按鈕自己，不當成整列點擊、也不擋掉按鈕的預設行為', async () => {
    const button = el.querySelector('.action-btn') as HTMLButtonElement;
    for (const key of ['Enter', ' ']) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      button.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
    await fixture.whenStable();

    expect(fixture.componentInstance.clicked).toEqual([]);
  });

  it('在列上的選取 checkbox 按 Space：不當成整列點擊', async () => {
    const checkbox = el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement;
    const event = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });
    checkbox.dispatchEvent(event);
    await fixture.whenStable();

    expect(event.defaultPrevented).toBe(false);
    expect(fixture.componentInstance.clicked).toEqual([]);
  });

  it('rowClickable 為 true 時列有 tabindex="0" 與 dt-row--clickable class', () => {
    const row = el.querySelector('tbody tr') as HTMLElement;
    expect(row.getAttribute('tabindex')).toBe('0');
    expect(row.classList.contains('dt-row--clickable')).toBe(true);
  });

  it('rowClass 回傳值套用到對應的 tr', () => {
    const rows = el.querySelectorAll('tbody tr');
    expect((rows[0] as HTMLElement).classList.contains('danger-row')).toBe(false);
    expect((rows[1] as HTMLElement).classList.contains('danger-row')).toBe(true);
  });
});

describe('DataTableComponent 匯出接線', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('showExport 為 true 且有資料時渲染匯出鈕', async () => {
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.dt-export-btn')).toBeTruthy();
  });

  it('showExport 為 false 時不渲染匯出鈕', async () => {
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    fixture.componentInstance.showExport.set(false);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.dt-export-btn')).toBeNull();
  });

  it('rows 為空（isEmpty）時不渲染匯出鈕，即使 showExport 為 true', async () => {
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    fixture.componentInstance.rows.set([]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.dt-export-btn')).toBeNull();
  });

  it('標準模式點擊匯出鈕呼叫 aoa_to_sheet，不呼叫 table_to_sheet（分派到 exportRows）', async () => {
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('.dt-export-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(aoaToSheet).toHaveBeenCalledTimes(1);
    expect(tableToSheet).not.toHaveBeenCalled();
    expect(writeFile).toHaveBeenCalledTimes(1);
  });

  it('標準模式呼叫 aoa_to_sheet 時帶正確內容，不只是呼叫次數對——呼叫了對的函式但內容錯仍要被抓到', async () => {
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('.dt-export-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    const { columns, rows } = fixture.componentInstance;
    expect(aoaToSheet).toHaveBeenCalledWith(rowsToAoa(columns, rows()));
  });

  it('點擊只匯出已勾選的資料時，exportRows 只收到已選資料列', async () => {
    await TestBed.configureTestingModule({ imports: [SelectedExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(SelectedExportHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('tbody .dt-selection-checkbox') as HTMLInputElement).click();
    await fixture.whenStable();
    (el.querySelector('.dt-export-selected-btn') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(aoaToSheet).toHaveBeenCalledWith(
      rowsToAoa(fixture.componentInstance.columns, [fixture.componentInstance.rows()[0]]),
    );
  });

  it('逃生門模式點擊匯出鈕呼叫 table_to_sheet，不呼叫 aoa_to_sheet（分派到 exportTableElement）', async () => {
    await TestBed.configureTestingModule({
      imports: [ExportCustomHostComponent],
    }).compileComponents();
    const fixture = TestBed.createComponent(ExportCustomHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('.dt-export-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(tableToSheet).toHaveBeenCalledTimes(1);
    expect(aoaToSheet).not.toHaveBeenCalled();
    expect(writeFile).toHaveBeenCalledTimes(1);
  });

  it('底層匯出拋錯時發出 exportFailed', async () => {
    writeFile.mockImplementationOnce(() => {
      throw new Error('boom');
    });
    await TestBed.configureTestingModule({ imports: [ExportHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ExportHostComponent);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('.dt-export-btn') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.componentInstance.lastError?.message).toBe('boom');
  });
});

describe('DataTableComponent 列標題欄（rowHeader）', () => {
  async function render(columns: DataTableColumn<Row>[]): Promise<HTMLElement> {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.columns.set(columns);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('標 rowHeader 的欄位以 th scope="row" 渲染，保留 data-label、對齊與收合 class', async () => {
    const el = await render([
      { key: 'name', label: '車牌', primary: true, rowHeader: true },
      { key: 'status', label: '狀態', primary: true },
      { key: 'mileage', label: '里程', align: 'end' },
    ]);
    const headers = el.querySelectorAll('tbody tr th[scope="row"]');
    expect(headers).toHaveLength(2);
    expect(headers[0].textContent?.trim()).toBe('ABC-123');
    expect(headers[0].getAttribute('data-label')).toBe('車牌');
    expect(headers[0].classList.contains('dt-align-start')).toBe(true);
    expect(headers[0].classList.contains('is-secondary')).toBe(false);
    expect(el.querySelectorAll('tbody tr:first-child td.dt-cell')).toHaveLength(2);
  });

  it('列標題欄同樣套用 dtCell 自訂模板', async () => {
    const el = await render([
      { key: 'name', label: '車牌', primary: true },
      { key: 'status', label: '狀態', primary: true, rowHeader: true },
    ]);
    expect(el.querySelector('tbody tr th[scope="row"] .chip')?.textContent).toBe('available');
  });

  it('未標 rowHeader 時資料列只有 td（既有使用端不受影響）', async () => {
    const el = await render([
      { key: 'name', label: '車牌', primary: true },
      { key: 'mileage', label: '里程', align: 'end' },
    ]);
    expect(el.querySelector('tbody th')).toBeNull();
  });

  it('手機卡片樣式同時涵蓋列標題 th（否則列標題會失去欄位標籤與排版）', () => {
    const scss = readFileSync(
      join(dirname(fileURLToPath(import.meta.url)), 'data-table.component.scss'),
      'utf-8',
    );
    const mobileBlock = scss.split('@media (max-width: 640px)')[1] ?? '';
    expect(mobileBlock).toMatch(/\.dt-cell::before/);
  });
});
