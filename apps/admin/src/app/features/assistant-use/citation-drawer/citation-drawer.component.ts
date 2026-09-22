import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChild,
  type AfterViewInit,
} from '@angular/core';
import type { ChatCitationView } from '../../../core/domain/conversation.model';

/**
 * 引用來源抽屜：手機為底部面板、桌機為右側抽屜。
 * 開啟時焦點移入並限制在抽屜內，Esc 或關閉按鈕關閉；焦點歸還由開啟者負責。
 */
@Component({
  selector: 'app-citation-drawer',
  imports: [A11yModule],
  templateUrl: './citation-drawer.component.html',
  styleUrl: './citation-drawer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CitationDrawerComponent implements AfterViewInit {
  readonly citations = input.required<readonly ChatCitationView[]>();
  readonly closed = output<void>();

  private readonly closeButton = viewChild.required<ElementRef<HTMLButtonElement>>('closeButton');

  ngAfterViewInit(): void {
    this.closeButton().nativeElement.focus();
  }
}
