import { describe, it, expect, vi, beforeEach } from 'vitest';
import { exportFileName, rowsToAoa } from './data-table-export';
import { DataTableColumn } from './data-table.types';

interface Row {
  id: string;
  name: string;
  price: number;
  unit: 'day' | 'trip';
}

const rows: Row[] = [
  { id: 'a1', name: '兒童座椅', price: 200, unit: 'day' },
  { id: 'a2', name: '接送服務', price: 500, unit: 'trip' },
];

const columns: DataTableColumn<Row>[] = [
  { key: 'name', label: '名稱' },
  { key: 'price', label: '單價' },
  { key: 'unit', label: '計價單位', exportValue: (r) => (r.unit === 'day' ? '每日' : '每趟') },
  { key: 'actions', label: '操作', exportSkip: true },
];

describe('exportFileName', () => {
  it('檔名為 {name}-{YYYYMMDD}.xlsx', () =>
    expect(exportFileName('vehicles', new Date(2026, 7, 4))).toBe('vehicles-20260804.xlsx'));

  it('月與日補零', () =>
    expect(exportFileName('x', new Date(2026, 0, 9))).toBe('x-20260109.xlsx'));
});

describe('rowsToAoa', () => {
  it('第一列為欄位標題，且不含 exportSkip 欄位', () =>
    expect(rowsToAoa(columns, rows)[0]).toEqual(['名稱', '單價', '計價單位']));

  it('有 exportValue 時優先於 row[key]', () =>
    expect(rowsToAoa(columns, rows)[1]).toEqual(['兒童座椅', 200, '每日']));

  it('數字保持數字型別，不轉字串', () =>
    expect(typeof rowsToAoa(columns, rows)[1][1]).toBe('number'));

  it('null/undefined 轉為空字串', () => {
    const sparse = [{ id: 'x', name: null, price: 1, unit: 'day' }] as unknown as Row[];
    expect(rowsToAoa(columns, sparse)[1][0]).toBe('');
  });

  it('rows 為空時只有標題列', () =>
    expect(rowsToAoa(columns, [])).toHaveLength(1));
});

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

describe('exportRows（標準模式：從資料匯出）', () => {
  beforeEach(() => vi.clearAllMocks());

  it('用 aoa_to_sheet，內容為 rowsToAoa 的結果', async () => {
    const { exportRows } = await import('./data-table-export');
    await exportRows(columns, rows, 'add-ons');
    expect(aoaToSheet).toHaveBeenCalledWith(rowsToAoa(columns, rows));
  });

  it('檔名帶入 exportName 與日期', async () => {
    const { exportRows } = await import('./data-table-export');
    await exportRows(columns, rows, 'add-ons');
    expect(writeFile.mock.calls[0][1]).toMatch(/^add-ons-\d{8}\.xlsx$/);
  });
});

describe('exportTableElement（逃生門模式：從 DOM 匯出）', () => {
  beforeEach(() => vi.clearAllMocks());

  it('用 table_to_sheet 並傳入該 table 節點', async () => {
    const { exportTableElement } = await import('./data-table-export');
    const table = document.createElement('table');
    await exportTableElement(table, 'settlement');
    expect(tableToSheet).toHaveBeenCalledWith(table);
  });
});
