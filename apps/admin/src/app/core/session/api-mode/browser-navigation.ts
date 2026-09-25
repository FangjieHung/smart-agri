import { DOCUMENT } from '@angular/common';
import { inject, Injectable } from '@angular/core';

/** 整頁導向（authorize、end-session）與目前網址；測試可替換，避免真的離開頁面。 */
@Injectable({ providedIn: 'root' })
export class BrowserNavigation {
  private readonly location = inject(DOCUMENT).location;

  origin(): string {
    return this.location.origin;
  }

  currentUrl(): string {
    return this.location.href;
  }

  assign(url: string): void {
    this.location.assign(url);
  }
}
