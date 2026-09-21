import { Component, computed, input } from '@angular/core';
import { StatusKey, toneOf } from '@smart-agri/theme-pack';

@Component({
  selector: 'app-status-chip',
  templateUrl: './status-chip.component.html',
  styleUrls: ['./status-chip.component.scss'],
})
export class StatusChipComponent {
  readonly status = input.required<StatusKey>();
  readonly label = input.required<string>();
  readonly tone = computed(() => toneOf(this.status()));
}
