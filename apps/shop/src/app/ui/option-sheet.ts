import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { MenuOption } from '../api/types';
import {
  chosenOf,
  extraPrice,
  removeOption,
  setSub,
  unmetGroupIds,
  type Choices,
} from '../domain/selection';
import { ntd } from './format';
import { OptionGroups, type SubRequest } from './option-groups';
import { Sheet } from './sheet';

/**
 * 第二層以下的選單（例如套餐裡選小菜與飲料）：在原本的面板上再疊一層。按「確定」把選擇交回上一層，
 * 按「返回」或 Esc 放棄這次的修改（剛選上的選項由上一層還原成沒選）。可以再往下一層。
 */
@Component({
  selector: 'app-option-sheet',
  imports: [Sheet, OptionGroups],
  template: `
    <app-sheet [labelledBy]="titleId()" (dismissed)="dismissed.emit()">
      <div class="sheet__head">
        <button
          type="button"
          class="sheet__close"
          aria-label="返回，不儲存"
          (click)="dismissed.emit()"
        >
          ‹
        </button>
        <h2 class="sheet__title" [id]="titleId()" tabindex="-1">
          <span class="crumb">{{ parentName() }}</span>
          {{ option().name }}
        </h2>
      </div>
      <div class="sheet__scroll">
        <app-option-groups
          [groups]="option().optionGroups"
          [choices]="choices()"
          [idPrefix]="titleId()"
          [highlight]="attempted() ? unmet() : []"
          (choicesChange)="change($event)"
          (openSub)="sub.set({ request: $event, initial: subInitial($event) })"
        />
      </div>
      <div class="sheet__foot">
        <p class="visually-hidden" aria-live="polite">{{ live() }}</p>
        <button
          type="button"
          class="btn btn--primary btn--block btn--split"
          [attr.aria-disabled]="unmet().length > 0"
          (click)="confirmChoices()"
        >
          <span>確定</span>
          @if (extra() > 0) {
            <span class="num">每份 +{{ ntd(extra()) }}</span>
          }
        </button>
      </div>
    </app-sheet>
    @if (sub(); as open) {
      <app-option-sheet
        [option]="open.request.option"
        [parentName]="open.request.group.name"
        [initial]="open.initial"
        [level]="level() + 1"
        (confirmed)="subConfirmed(open.request, $event)"
        (dismissed)="subDismissed(open.request)"
      />
    }
  `,
  styles: `
    .crumb {
      display: block;
      font-size: var(--fs-small);
      font-weight: 400;
      color: var(--c-ink-2);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OptionSheet {
  readonly option = input.required<MenuOption>();
  /** 上一層群組的名稱（「升級套餐」）。 */
  readonly parentName = input.required<string>();
  readonly initial = input.required<Choices>();
  /** 第幾層（2 起），用來產生不重複的 id。 */
  readonly level = input(2);

  readonly confirmed = output<Choices>();
  readonly dismissed = output<void>();

  protected readonly ntd = ntd;
  protected readonly choices = linkedSignal(() => this.initial());
  protected readonly attempted = signal(false);
  protected readonly live = signal('');
  protected readonly sub = signal<{ request: SubRequest; initial: Choices } | null>(null);
  protected readonly titleId = computed(() => `opt-l${this.level()}-${this.option().id}`);
  protected readonly unmet = computed(() =>
    unmetGroupIds(this.option().optionGroups, this.choices()),
  );
  protected readonly extra = computed(
    () => this.option().price + extraPrice(this.option().optionGroups, this.choices()),
  );

  protected change(choices: Choices): void {
    this.choices.set(choices);
    if (this.attempted() && this.unmet().length === 0) this.live.set('都選好了，可以按確定');
  }

  protected subInitial(request: SubRequest): Choices {
    return chosenOf(this.choices(), request.group.id, request.option.id)?.sub ?? {};
  }

  protected subConfirmed(request: SubRequest, sub: Choices): void {
    this.choices.set(setSub(this.choices(), request.group.id, request.option.id, sub));
    this.sub.set(null);
  }

  protected subDismissed(request: SubRequest): void {
    if (request.isNew) {
      this.choices.set(removeOption(this.choices(), request.group.id, request.option.id));
    }
    this.sub.set(null);
  }

  protected confirmChoices(): void {
    const unmet = this.unmet();
    if (unmet.length > 0) {
      this.attempted.set(true);
      const names = this.option()
        .optionGroups.filter((group) => unmet.includes(group.id))
        .map((group) => group.name);
      this.live.set(`還沒選完：${names.join('、')}`);
      focusGroup(`${this.titleId()}-${unmet[0]}`);
      return;
    }
    this.confirmed.emit(this.choices());
  }
}

/** 把沒選完的群組捲進畫面並放上焦點。 */
export function focusGroup(id: string): void {
  const element = globalThis.document?.getElementById(id);
  if (!element) return;
  const reduce = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  element.scrollIntoView?.({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
  element.focus({ preventScroll: true });
}
