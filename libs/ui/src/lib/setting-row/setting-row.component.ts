import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** Title, explanation, and a right-aligned projected control. */
@Component({
  selector: 'lib-setting-row',
  templateUrl: './setting-row.component.html',
  styleUrl: './setting-row.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingRowComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly controlId = input('');
  readonly controlValue = input('');
  readonly toggleable = input(false);
  readonly checked = input(false);
  readonly disabled = input(false);
  readonly toggled = output<boolean>();

  protected change(event: Event): void {
    this.toggled.emit((event.target as HTMLInputElement).checked);
  }
}
