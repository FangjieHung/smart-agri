import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { MenuOption, OptionGroup } from '../api/types';
import {
  chosenOf,
  controlOf,
  countIn,
  describe,
  issues,
  maxCountFor,
  requirementText,
  setCount,
  toggle,
  type Choices,
} from '../domain/selection';
import { plusNtd } from './format';

/** 要打開第二層選單的選項。 */
export interface SubRequest {
  readonly group: OptionGroup;
  readonly option: MenuOption;
  /** 剛選上（按取消要還原成沒選）。 */
  readonly isNew: boolean;
}

/**
 * 一層選項群組：圓鈕、核取方塊或份數加減，標示「必選 N 個／選填／最多 N 個」與「已選 N」。
 * 選了帶有下一層群組的選項時送出 {@link openSub}，由外層打開第二層選單；選好後這一列顯示所選內容與「修改」。
 */
@Component({
  selector: 'app-option-groups',
  template: `
    @for (group of groups(); track group.id) {
      @let control = controlOf(group);
      @let count = countIn(choices(), group.id);
      @let unmet = highlight().includes(group.id);
      <fieldset
        class="group"
        [id]="idPrefix() + '-' + group.id"
        [class.group--unmet]="unmet"
        tabindex="-1"
      >
        <legend class="group__legend">
          <span class="group__name">{{ group.name }}</span>
          <span class="group__meta">
            <span class="group__rule" [class.group__rule--required]="group.min > 0">{{
              requirementText(group)
            }}</span>
            <span class="group__count num">已選 {{ count }}</span>
          </span>
        </legend>
        @if (unmet) {
          <p class="group__error">{{ unmetText(group, count) }}</p>
        }
        <ul class="group__options">
          @for (option of group.options; track option.id) {
            @let chosen = chosenOf(choices(), group.id, option.id);
            @let inputId = idPrefix() + '-' + group.id + '-' + option.id;
            <li class="opt" [class.opt--chosen]="!!chosen">
              @if (control === 'stepper') {
                <div class="opt__row">
                  <span class="opt__label" [id]="inputId">
                    <span class="opt__name">{{ option.name }}</span>
                    @if (option.price > 0) {
                      <span class="price">{{ plusNtd(option.price) }}</span>
                    }
                  </span>
                  <span class="stepper" role="group" [attr.aria-labelledby]="inputId">
                    <button
                      type="button"
                      class="stepper__btn"
                      [disabled]="!chosen"
                      [attr.aria-label]="'減少' + option.name"
                      (click)="step(group, option, (chosen?.count ?? 0) - 1)"
                    >
                      −
                    </button>
                    <span class="stepper__value" aria-live="off">{{ chosen?.count ?? 0 }}</span>
                    <button
                      type="button"
                      class="stepper__btn"
                      [disabled]="(chosen?.count ?? 0) >= maxCountFor(group, choices(), option.id)"
                      [attr.aria-label]="'增加' + option.name"
                      (click)="step(group, option, (chosen?.count ?? 0) + 1)"
                    >
                      +
                    </button>
                  </span>
                </div>
              } @else {
                <label class="opt__row" [for]="inputId">
                  <input
                    class="opt__input"
                    [id]="inputId"
                    [type]="control"
                    [name]="idPrefix() + '-' + group.id"
                    [checked]="!!chosen"
                    [disabled]="
                      control === 'checkbox' && !chosen && group.max > 1 && count >= group.max
                    "
                    (change)="pick(group, option, $event)"
                  />
                  <span class="opt__label">
                    <span class="opt__name">{{ option.name }}</span>
                    @if (option.price > 0) {
                      <span class="price">{{ plusNtd(option.price) }}</span>
                    }
                  </span>
                </label>
              }
              @if (chosen && option.optionGroups.length > 0) {
                @let summary = describe(option.optionGroups, chosen.sub);
                @let incomplete = issues(option.optionGroups, chosen.sub).length > 0;
                <div class="opt__sub" [class.opt__sub--incomplete]="incomplete">
                  <span>{{ incomplete ? '還沒選完：' : '' }}{{ summary || '尚未選擇' }}</span>
                  <button
                    type="button"
                    class="btn btn--quiet"
                    (click)="openSub.emit({ group, option, isNew: false })"
                  >
                    修改<span class="visually-hidden">{{ option.name }}的選項</span>
                  </button>
                </div>
              }
            </li>
          }
        </ul>
      </fieldset>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-2);
    }
    .group {
      margin: 0;
      padding: var(--space-3) var(--space-4) var(--space-2);
      border: 0;
      border-top: 0.5rem solid var(--c-paper);
      scroll-margin-top: var(--space-4);
    }
    .group--unmet {
      background: var(--c-danger-wash);
      box-shadow: inset 0.25rem 0 0 var(--c-danger);
    }
    .group__legend {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--space-1) var(--space-3);
      width: 100%;
      float: left;
      padding: 0;
      margin-bottom: var(--space-2);
    }
    .group__legend + * {
      clear: both;
    }
    .group__name {
      font-size: var(--fs-lead);
      font-weight: 700;
    }
    .group__meta {
      display: inline-flex;
      gap: var(--space-2);
      font-size: var(--fs-small);
      color: var(--c-ink-2);
    }
    .group__rule {
      padding: 0 var(--space-2);
      border-radius: 999px;
      background: var(--c-paper);
    }
    .group__rule--required {
      background: var(--c-notice);
      color: var(--c-notice-ink);
      font-weight: 600;
    }
    .group__error {
      clear: both;
      color: var(--c-danger);
      font-weight: 600;
      font-size: var(--fs-small);
      margin-bottom: var(--space-1);
    }
    .group__options {
      clear: both;
    }
    .opt + .opt {
      border-top: 1px solid var(--c-line);
    }
    .opt__row {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      min-height: 3rem;
      padding-block: var(--space-1);
      cursor: pointer;
    }
    div.opt__row {
      cursor: default;
      justify-content: space-between;
    }
    .opt__input {
      flex: none;
      width: 1.375rem;
      height: 1.375rem;
      margin: 0;
      accent-color: var(--c-accent);
    }
    .opt__label {
      flex: 1 1 auto;
      display: flex;
      justify-content: space-between;
      gap: var(--space-3);
      min-width: 0;
    }
    .opt--chosen .opt__name {
      font-weight: 600;
    }
    .opt__sub {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
      margin: 0 0 var(--space-2) 2.125rem;
      padding: var(--space-1) 0 var(--space-1) var(--space-3);
      border-left: 2px solid var(--c-accent);
      color: var(--c-ink-2);
      font-size: var(--fs-small);
    }
    .opt__sub--incomplete {
      border-left-color: var(--c-danger);
      color: var(--c-danger);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OptionGroups {
  readonly groups = input.required<readonly OptionGroup[]>();
  readonly choices = input.required<Choices>();
  /** 這一層每個 input 的 id 前綴（同一頁有好幾層時不重複）。 */
  readonly idPrefix = input.required<string>();
  /** 要標示沒選完的群組 id（顧客按了加入購物車之後）。 */
  readonly highlight = input<readonly string[]>([]);

  readonly choicesChange = output<Choices>();
  readonly openSub = output<SubRequest>();

  protected readonly controlOf = controlOf;
  protected readonly countIn = countIn;
  protected readonly chosenOf = chosenOf;
  protected readonly requirementText = requirementText;
  protected readonly describe = describe;
  protected readonly issues = issues;
  protected readonly maxCountFor = maxCountFor;
  protected readonly plusNtd = plusNtd;

  protected unmetText(group: OptionGroup, count: number): string {
    if (count > group.max) return `${group.name}最多選 ${group.max} 個`;
    if (count < group.min) return `請選擇${group.name}`;
    return `請完成${group.name}的選項`;
  }

  protected pick(group: OptionGroup, option: MenuOption, event: Event): void {
    const wasChosen = !!chosenOf(this.choices(), group.id, option.id);
    const next = toggle(group, this.choices(), option.id);
    const nowChosen = !!chosenOf(next, group.id, option.id);
    (event.target as HTMLInputElement).checked = nowChosen;
    if (next === this.choices()) return;
    this.choicesChange.emit(next);
    if (!wasChosen && nowChosen && option.optionGroups.length > 0) {
      this.openSub.emit({ group, option, isNew: true });
    }
  }

  protected step(group: OptionGroup, option: MenuOption, count: number): void {
    const wasChosen = !!chosenOf(this.choices(), group.id, option.id);
    const next = setCount(group, this.choices(), option.id, count);
    if (next === this.choices()) return;
    this.choicesChange.emit(next);
    if (!wasChosen && option.optionGroups.length > 0 && chosenOf(next, group.id, option.id)) {
      this.openSub.emit({ group, option, isNew: true });
    }
  }
}
