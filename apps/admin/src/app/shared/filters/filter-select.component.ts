import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { ZH_TW } from '../../core/i18n/zh-tw';

export interface FilterOption<T extends string> {
  value: T;
  label: string;
}

@Component({
  selector: 'app-filter-select',
  imports: [FormsModule, MatFormFieldModule, MatSelectModule],
  templateUrl: './filter-select.component.html',
  styleUrls: ['./filter-select.component.scss'],
})
export class FilterSelectComponent<T extends string> {
  protected readonly t = ZH_TW;
  readonly label = input.required<string>();
  readonly options = input.required<FilterOption<T>[]>();
  readonly value = model<T | null>(null);
}
