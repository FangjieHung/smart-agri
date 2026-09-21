import { Directive, Injectable, OnDestroy, TemplateRef, inject, signal } from '@angular/core';

/**
 * Topbar 的 toolbar 插槽：頁面把自己的 toolbar 模板登記進來，由 HeaderComponent 負責渲染，
 * 讓 toolbar 在視覺上與 topbar-title 同一列，內容與繫結仍屬於各自的頁面元件。
 */
@Injectable({ providedIn: 'root' })
export class HeaderToolbarSlot {
  readonly template = signal<TemplateRef<unknown> | null>(null);
}

@Directive({
  selector: '[appHeaderToolbar]',
})
export class HeaderToolbarDirective implements OnDestroy {
  private readonly slot = inject(HeaderToolbarSlot);
  private readonly template = inject(TemplateRef<unknown>);

  constructor() {
    this.slot.template.set(this.template);
  }

  ngOnDestroy(): void {
    if (this.slot.template() === this.template) {
      this.slot.template.set(null);
    }
  }
}
