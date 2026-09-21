import { Directive, TemplateRef, inject, input } from '@angular/core';

/** dtCell template 的 context：let-row 取得該列資料。 */
export interface DataTableCellContext<T> {
  $implicit: T;
}

/**
 * 為單一欄位提供自訂儲存格內容。
 * 用法：<ng-template dtCell="status" let-row> ... </ng-template>
 */
@Directive({ selector: 'ng-template[dtCell]' })
export class DataTableCellDirective<T = unknown> {
  readonly dtCell = input.required<string>();
  readonly template = inject<TemplateRef<DataTableCellContext<T>>>(TemplateRef);
}
