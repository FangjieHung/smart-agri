import { Component, inject, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { HeaderToolbarSlot } from './header-toolbar-slot';

@Component({
  selector: 'app-header',
  imports: [NgTemplateOutlet, MatButtonModule],
  templateUrl: './header.component.html',
  styleUrl: './header.component.scss',
})
export class HeaderComponent {
  readonly currentTitle = input.required<string>();
  readonly currentGroupLabel = input.required<string | null>();
  readonly menuToggle = output<void>();

  protected readonly toolbarTemplate = inject(HeaderToolbarSlot).template;
}
