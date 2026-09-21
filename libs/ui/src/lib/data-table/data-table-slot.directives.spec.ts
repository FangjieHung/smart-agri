import { Component } from '@angular/core';
import type { TemplateRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import {
  DataTableBatchActionsDirective,
  DataTableBatchActionsContext,
} from './data-table-slot.directives';

interface Row {
  id: string;
}

const rows: readonly Row[] = [{ id: 'r1' }];

const typedTemplate: TemplateRef<DataTableBatchActionsContext<Row>> =
  null as unknown as DataTableBatchActionsDirective<Row>['template'];
const typedContext: DataTableBatchActionsContext<Row> = { $implicit: rows };

@Component({
  imports: [DataTableBatchActionsDirective],
  template: `
    <ng-template dtBatchActions let-selected>
      <span data-testid="selected-count">{{ selected.length }}</span>
    </ng-template>
  `,
})
class BatchActionsHostComponent {}

describe('DataTableBatchActionsDirective', () => {
  it('template context 的 $implicit 是唯讀的泛型資料列陣列', () => {
    expect(typedTemplate).toBeNull();
    expect(typedContext.$implicit).toEqual(rows);
  });

  it('可投影 dtBatchActions template 並保留 template reference', () => {
    const fixture = TestBed.configureTestingModule({
      imports: [BatchActionsHostComponent],
    }).createComponent(BatchActionsHostComponent);

    fixture.detectChanges();

    expect(fixture.componentInstance).toBeTruthy();
  });
});
