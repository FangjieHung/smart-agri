import { Directive, TemplateRef, inject } from '@angular/core';

export interface DataTableBatchActionsContext<T> {
  $implicit: readonly T[];
}

/** 逃生門模式：頁面自行提供 thead 內容（可用 colspan / rowspan）。 */
@Directive({ selector: 'ng-template[dtHead]' })
export class DataTableHeadDirective {
  readonly template = inject<TemplateRef<unknown>>(TemplateRef);
}

/** 逃生門模式：頁面自行提供 tbody 內容。 */
@Directive({ selector: 'ng-template[dtBody]' })
export class DataTableBodyDirective {
  readonly template = inject<TemplateRef<unknown>>(TemplateRef);
}

/** 批次操作工具列內容，template context 的 $implicit 為目前已選資料。 */
@Directive({ selector: 'ng-template[dtBatchActions]' })
export class DataTableBatchActionsDirective<T = unknown> {
  readonly template = inject<TemplateRef<DataTableBatchActionsContext<T>>>(TemplateRef);

  static ngTemplateContextGuard<T>(
    _directive: DataTableBatchActionsDirective<T>,
    _context: unknown,
  ): _context is DataTableBatchActionsContext<T> {
    return true;
  }
}
