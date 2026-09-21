import { Component } from '@angular/core';
import { ZH_TW } from '../../core/i18n/zh-tw';

@Component({
  selector: 'app-footer',
  imports: [],
  templateUrl: './footer.component.html',
  styleUrl: './footer.component.scss',
})
export class FooterComponent {
  protected readonly t = ZH_TW;
  protected readonly year = new Date().getFullYear();
  protected readonly version = '0.0.1';
}
