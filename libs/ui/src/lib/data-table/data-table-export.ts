import { DataTableColumn } from './data-table.types';

export function exportFileName(name: string, date: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp = `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}`;
  return `${name}-${stamp}.xlsx`;
}

/** 把資料列轉成 SheetJS 的 aoa（array of arrays）：第一列是標題。 */
export function rowsToAoa<T>(
  columns: DataTableColumn<T>[],
  rows: readonly T[],
): (string | number)[][] {
  const cols = columns.filter((c) => !c.exportSkip);
  const header = cols.map((c) => c.label);
  const body = rows.map((row) =>
    cols.map((col) => {
      const raw = col.exportValue
        ? col.exportValue(row)
        : (row as Record<string, unknown>)[col.key];
      if (raw === null || raw === undefined) return '';
      return typeof raw === 'number' ? raw : String(raw);
    }),
  );
  return [header, ...body];
}

type XlsxModule = typeof import('xlsx');

async function writeWorkbook(XLSX: XlsxModule, sheet: unknown, name: string): Promise<void> {
  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, sheet as never, 'Sheet1');
  XLSX.writeFile(workbook, exportFileName(name));
}

/**
 * 標準模式：從資料匯出，與畫面完全脫鉤。
 * 日後若加上分頁或虛擬捲動也不會變成「只匯出當前頁」。
 */
export async function exportRows<T>(
  columns: DataTableColumn<T>[],
  rows: readonly T[],
  name: string,
): Promise<void> {
  const XLSX = await import('xlsx');
  await writeWorkbook(XLSX, XLSX.utils.aoa_to_sheet(rowsToAoa(columns, rows)), name);
}

/**
 * 逃生門模式：從 DOM 匯出，colspan / rowspan 會轉為 Excel 的合併儲存格。
 * 這是 exportRows 做不到的，代價是與畫面耦合。
 */
export async function exportTableElement(el: HTMLTableElement, name: string): Promise<void> {
  const XLSX = await import('xlsx');
  await writeWorkbook(XLSX, XLSX.utils.table_to_sheet(el), name);
}
