import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { MenuItem } from '../api/types';
import type { CartLine } from '../domain/cart';
import {
  chosenOf,
  fromSelections,
  NO_CHOICES,
  removeOption,
  setSub,
  toSelections,
  unitPrice,
  unmetGroupIds,
  type Choices,
} from '../domain/selection';
import { ntd } from './format';
import { OptionGroups, type SubRequest } from './option-groups';
import { focusGroup, OptionSheet } from './option-sheet';
import { Sheet } from './sheet';

/**
 * 商品詳情：選項、品項備註、份數，底部是「加入購物車」與即時算出的金額（只用來顯示，伺服器會重算）。
 * 必選群組沒選完時按鈕看起來是停用的，按下去會捲到第一個沒選完的群組並標示出來。
 * 從購物車打開時（`editing`）帶入原本的選擇，按鈕改成「更新購物車」。
 */
@Component({
  selector: 'app-item-sheet',
  imports: [Sheet, OptionGroups, OptionSheet],
  template: `
    <app-sheet labelledBy="item-title" (dismissed)="dismissed.emit()">
      <div class="sheet__head">
        <h2 class="sheet__title" id="item-title" tabindex="-1">{{ item().name }}</h2>
        <button type="button" class="sheet__close" aria-label="關閉" (click)="dismissed.emit()">
          ×
        </button>
      </div>
      <div class="sheet__scroll">
        @if (item().imageUrl; as src) {
          <img
            class="hero"
            [src]="src"
            alt=""
            width="640"
            height="400"
            decoding="async"
            (error)="$any($event.target).hidden = true"
          />
        }
        <div class="intro">
          <p class="price base">{{ ntd(item().price) }}</p>
          @if (item().description) {
            <p class="desc">{{ item().description }}</p>
          }
        </div>
        <app-option-groups
          [groups]="item().optionGroups"
          [choices]="choices()"
          idPrefix="item"
          [highlight]="attempted() ? unmet() : []"
          (choicesChange)="change($event)"
          (openSub)="sub.set({ request: $event, initial: subInitial($event) })"
        />
        @if (showComment()) {
          <div class="field comment">
            <label class="field__label" for="item-comment"
              >備註給店家 <span class="field__optional">（選填）</span></label
            >
            <textarea
              id="item-comment"
              class="input"
              rows="2"
              [maxLength]="commentMax()"
              [value]="comment()"
              (input)="comment.set($any($event.target).value)"
              placeholder="例如：麵硬一點、不要蔥"
            ></textarea>
            <p class="field__hint num">最多 {{ commentMax() }} 字</p>
          </div>
        }
      </div>
      <div class="sheet__foot">
        <p class="visually-hidden" aria-live="polite">{{ live() }}</p>
        <span class="stepper" role="group" aria-label="份數">
          <button
            type="button"
            class="stepper__btn"
            aria-label="減少份數"
            [disabled]="quantity() <= 1"
            (click)="setQuantity(quantity() - 1)"
          >
            −
          </button>
          <span class="stepper__value">{{ quantity() }}</span>
          <button
            type="button"
            class="stepper__btn"
            aria-label="增加份數"
            [disabled]="quantity() >= maxQuantity()"
            (click)="setQuantity(quantity() + 1)"
          >
            +
          </button>
        </span>
        <button
          type="button"
          class="btn btn--primary btn--split add"
          data-testid="add-to-cart"
          [attr.aria-disabled]="unmet().length > 0"
          (click)="save()"
        >
          <span>{{ editing() ? '更新購物車' : '加入購物車' }}</span>
          <span class="num">{{ ntd(total()) }}</span>
        </button>
      </div>
    </app-sheet>
    @if (sub(); as open) {
      <app-option-sheet
        [option]="open.request.option"
        [parentName]="open.request.group.name"
        [initial]="open.initial"
        (confirmed)="subConfirmed(open.request, $event)"
        (dismissed)="subDismissed(open.request)"
      />
    }
  `,
  styles: `
    .hero {
      width: 100%;
      height: auto;
      aspect-ratio: 16 / 10;
      object-fit: cover;
      background: var(--c-paper);
    }
    .intro {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-3) var(--space-4) var(--space-4);
    }
    .base {
      font-size: var(--fs-lead);
    }
    .desc {
      color: var(--c-ink-2);
    }
    .comment {
      padding: var(--space-3) var(--space-4) var(--space-5);
      border-top: 0.5rem solid var(--c-paper);
    }
    .add {
      flex: 1 1 auto;
      min-width: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ItemSheet {
  readonly item = input.required<MenuItem>();
  /** 從購物車修改時的那一行。 */
  readonly editing = input<CartLine | null>(null);
  readonly showComment = input(false);
  readonly commentMax = input(100);
  readonly maxQuantity = input(99);

  readonly saved = output<Omit<CartLine, 'key'>>();
  readonly dismissed = output<void>();

  protected readonly ntd = ntd;
  protected readonly choices = linkedSignal<Choices>(() => {
    const editing = this.editing();
    return (editing && fromSelections(this.item().optionGroups, editing.selections)) || NO_CHOICES;
  });
  protected readonly quantity = linkedSignal(() => this.editing()?.quantity ?? 1);
  protected readonly comment = linkedSignal(() => this.editing()?.comment ?? '');
  protected readonly attempted = signal(false);
  protected readonly live = signal('');
  protected readonly sub = signal<{ request: SubRequest; initial: Choices } | null>(null);

  protected readonly unmet = computed(() =>
    unmetGroupIds(this.item().optionGroups, this.choices()),
  );
  protected readonly unit = computed(() => unitPrice(this.item(), this.choices()));
  protected readonly total = computed(() => this.unit() * this.quantity());

  protected change(choices: Choices): void {
    this.choices.set(choices);
    this.live.set(
      `金額 ${ntd(this.total())}${this.attempted() && this.unmet().length === 0 ? '，都選好了' : ''}`,
    );
  }

  protected setQuantity(quantity: number): void {
    this.quantity.set(Math.max(1, Math.min(this.maxQuantity(), quantity)));
    this.live.set(`${this.quantity()} 份，金額 ${ntd(this.total())}`);
  }

  protected subInitial(request: SubRequest): Choices {
    return chosenOf(this.choices(), request.group.id, request.option.id)?.sub ?? NO_CHOICES;
  }

  protected subConfirmed(request: SubRequest, sub: Choices): void {
    this.change(setSub(this.choices(), request.group.id, request.option.id, sub));
    this.sub.set(null);
  }

  protected subDismissed(request: SubRequest): void {
    if (request.isNew) {
      this.change(removeOption(this.choices(), request.group.id, request.option.id));
    }
    this.sub.set(null);
  }

  protected save(): void {
    const unmet = this.unmet();
    if (unmet.length > 0) {
      this.attempted.set(true);
      const names = this.item()
        .optionGroups.filter((group) => unmet.includes(group.id))
        .map((group) => group.name);
      this.live.set(`還沒選完：${names.join('、')}`);
      focusGroup(`item-${unmet[0]}`);
      return;
    }
    const comment = this.comment().trim();
    this.saved.emit({
      itemId: this.item().id,
      name: this.item().name,
      quantity: this.quantity(),
      selections: toSelections(this.item().optionGroups, this.choices()),
      comment: this.showComment() && comment ? comment : null,
    });
  }
}
