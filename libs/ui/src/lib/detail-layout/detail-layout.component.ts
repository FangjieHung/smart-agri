import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

export interface DetailTab { readonly id: string; readonly label: string; }

/** Shared responsive navigation for assistant, knowledge, and database detail pages. */
@Component({
  selector: 'lib-detail-layout',
  imports: [RouterLink],
  templateUrl: './detail-layout.component.html',
  styleUrl: './detail-layout.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DetailLayoutComponent {
  readonly tabs = input.required<readonly DetailTab[]>();
  readonly activeId = input.required<string>();
  readonly baseRoute = input.required<readonly string[]>();
  readonly ariaLabel = input.required<string>();
  readonly preserveQuery = input(false);
}
