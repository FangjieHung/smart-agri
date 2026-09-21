import { describe, it, expect } from 'vitest';
import type { DataTableColumn, DataTableLabels, DataTableMobileMode } from './data-table.types';

interface Row {
  id: string;
  amount: number;
}

describe('DataTableColumn', () => {
  it('未提供 exportValue 時，匯出取值回退到 row[key]', () => {
    const column: DataTableColumn<Row> = { key: 'amount', label: '金額' };
    const row: Row = { id: 'r1', amount: 1200 };
    expect(row[column.key as keyof Row]).toBe(1200);
  });

  it('提供 exportValue 時，匯出值由使用端函式決定', () => {
    const column: DataTableColumn<Row> = {
      key: 'amount',
      label: '金額',
      exportValue: (row) => `NT$${row.amount}`,
    };
    const row: Row = { id: 'r1', amount: 1200 };
    expect(column.exportValue?.(row)).toBe('NT$1200');
  });

  it('exportSkip 為 true 時，代表此欄不納入匯出（例如 actions 欄）', () => {
    const column: DataTableColumn<Row> = { key: 'actions', label: '操作', exportSkip: true };
    expect(column.exportSkip).toBe(true);
  });

  it('primary 未指定時預設為 undefined，由元件端決定第一欄是否視為 primary', () => {
    const column: DataTableColumn<Row> = { key: 'id', label: '編號' };
    expect(column.primary).toBeUndefined();
  });
});

describe('DataTableLabels', () => {
  it('選取與批次操作文案皆為必填字串，全部由使用端傳入', () => {
    const labels: DataTableLabels = {
      exportExcel: '匯出 Excel',
      expandRow: '展開',
      collapseRow: '收合',
      exportFailedText: '匯出失敗',
      selectAll: '全選',
      deselectAll: '取消全選',
      selectRow: '選取資料列',
      batchDelete: '刪除已選資料',
      selectedCount: '已選 {count} 筆',
      exportSelected: '只匯出已勾選的資料',
    };
    expect(labels.exportExcel).toBe('匯出 Excel');
    expect(labels.expandRow).toBe('展開');
    expect(labels.collapseRow).toBe('收合');
    expect(labels.exportFailedText).toBe('匯出失敗');
    expect(labels.selectAll).toBe('全選');
    expect(labels.deselectAll).toBe('取消全選');
    expect(labels.selectRow).toBe('選取資料列');
    expect(labels.batchDelete).toBe('刪除已選資料');
    expect(labels.selectedCount).toBe('已選 {count} 筆');
    expect(labels.exportSelected).toBe('只匯出已勾選的資料');
  });
});

describe('DataTableMobileMode', () => {
  it('只接受 cards 或 scroll 兩種字面值', () => {
    const cards: DataTableMobileMode = 'cards';
    const scroll: DataTableMobileMode = 'scroll';
    expect([cards, scroll]).toEqual(['cards', 'scroll']);
  });
});
