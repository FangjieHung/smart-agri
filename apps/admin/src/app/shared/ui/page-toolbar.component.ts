import { Component, ElementRef, input, model, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { ZH_TW } from '../../core/i18n/zh-tw';

let nextInstanceId = 0;

@Component({
  selector: 'app-page-toolbar',
  imports: [FormsModule, MatButtonModule],
  templateUrl: './page-toolbar.component.html',
  styleUrls: ['./page-toolbar.component.scss'],
})
export class PageToolbarComponent {
  protected readonly t = ZH_TW;
  readonly query = model<string>('');
  readonly placeholder = input<string>(ZH_TW.common.search);
  readonly activeFilterCount = input<number>(0);
  readonly showSearch = input<boolean>(true);
  readonly clearAll = output<void>();
  readonly searchSubmit = output<string>();

  readonly expanded = signal(false);
  protected readonly searchInputId = `page-toolbar-search-${nextInstanceId++}`;

  private readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private readonly toggleRef = viewChild<ElementRef<HTMLButtonElement>>('toggleBtn');

  toggle(): void {
    if (this.expanded()) {
      this.collapse();
      return;
    }
    this.expanded.set(true);
    this.inputRef()?.nativeElement.focus();
  }

  onEscape(): void {
    this.collapse();
    this.toggleRef()?.nativeElement.focus();
  }

  private collapse(): void {
    this.query.set('');
    this.expanded.set(false);
    this.inputRef()?.nativeElement.blur();
  }

  clearKeepFocus(): void {
    this.query.set('');
    this.inputRef()?.nativeElement.focus();
  }

  submitQuery(): void {
    const query = this.query().trim();
    if (query) {
      this.searchSubmit.emit(query);
    }
  }

  collapseIfEmpty(): void {
    if (!this.query().trim()) {
      this.expanded.set(false);
    }
  }
}
