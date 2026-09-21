/** 標準模式的欄位定義。label 是欄位標題的唯一來源，元件會自動寫進 td 的 data-label。 */
export interface DataTableColumn<T> {
  key: string;
  label: string;
  /** 手機版卡片收合時仍顯示。整份 columns 都沒標時，第一欄自動視為 primary。 */
  primary?: boolean;
  align?: 'start' | 'end';
  /** 匯出時的取值。未提供時取 row[key]。 */
  exportValue?: (row: T) => string | number;
  /** 不納入匯出（actions 欄用）。 */
  exportSkip?: boolean;
}

/** 元件本身不內建字串，全部由使用端傳入。 */
export interface DataTableLabels {
  exportExcel: string;
  selectAll: string;
  deselectAll: string;
  selectRow: string;
  batchDelete: string;
  selectedCount: string;
  exportSelected: string;
  expandRow: string;
  collapseRow: string;
  /**
   * 匯出失敗時要顯示給使用者看的文案（例如 xlsx 這個 lazy chunk 載入失敗）。
   * 元件本身不使用這個欄位——它只透過 exportFailed 把原始 Error 丟給使用端，
   * 由使用端的 onExportFailed 從這裡取用文案顯示，避免把 e.message 這種內部
   * 錯誤字串（甚至是英文的 dynamic import 失敗訊息）直接餵給使用者。
   */
  exportFailedText: string;
}

export type DataTableMobileMode = 'cards' | 'scroll';
