import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  type AfterViewInit,
} from '@angular/core';
import { LocationStrategy } from '@angular/common';
import { Router } from '@angular/router';
import type { ChatFormView } from '../../../core/domain/conversation.model';
import type {
  DatabaseFieldError,
  DatabaseFieldId,
  DatabaseFieldView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';

/** 「我送出的資料」（issue #146）。 */
const ACTIVITY_PATH = '/app/activity';

/**
 * 對話中的表單：只收集填寫值並交給確認步驟，不會直接建立紀錄。
 *
 * 退路（issue #171 ②）：右上 × 與「不用了」都送出 `cancelled`（不送出任何資料）；下方「前往我送出的資料」
 * 連到 `/app/activity`，已有輸入內容時改送 `leaveRequested`，由外層先確認「離開會清除已填的內容」。
 */
@Component({
  selector: 'app-inline-form',
  templateUrl: './inline-form.component.html',
  styleUrl: './inline-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InlineFormComponent implements AfterViewInit {
  readonly form = input.required<ChatFormView>();
  readonly answers = input<DatabaseTrialAnswers>({});
  readonly errors = input<readonly DatabaseFieldError[]>([]);
  /** 未登入訪客沒有「我送出的資料」，不顯示任何 `/app` 連結。 */
  readonly showActivityLink = input(true);

  readonly review = output<DatabaseTrialAnswers>();
  readonly cancelled = output<void>();
  /** 已有輸入內容時按下「前往我送出的資料」：外層確認後再離開。 */
  readonly leaveRequested = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly router = inject(Router);
  /** 一般連結的 href（含部署的 base href），讓新分頁開啟等瀏覽器行為照常；同分頁點擊由 `leave` 處理。 */
  protected readonly activityHref = inject(LocationStrategy).prepareExternalUrl(ACTIVITY_PATH);
  protected readonly values = linkedSignal<DatabaseTrialAnswers>(() => this.answers());
  protected readonly invalidIds = computed(
    () => new Set(this.errors().flatMap((error) => (error.fieldId === null ? [] : [error.fieldId]))),
  );

  ngAfterViewInit(): void {
    this.host.nativeElement.querySelector<HTMLElement>('input')?.focus();
  }

  protected inputId(field: DatabaseFieldView): string {
    return `chat-field-${field.id}`;
  }

  protected text(fieldId: DatabaseFieldId): string {
    const value = this.values()[fieldId];
    return typeof value === 'string' ? value : '';
  }

  protected checked(fieldId: DatabaseFieldId, option: string): boolean {
    const value = this.values()[fieldId];
    return Array.isArray(value) ? value.includes(option) : value === option;
  }

  protected scaleOptions(field: DatabaseFieldView): string[] {
    const scale = field.scale;
    if (scale === null) return [];
    return Array.from({ length: scale.max - scale.min + 1 }, (_, index) => String(scale.min + index));
  }

  protected setText(fieldId: DatabaseFieldId, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.values.update((current) => ({ ...current, [fieldId]: value }));
  }

  protected toggle(fieldId: DatabaseFieldId, option: string, event: Event): void {
    const isChecked = (event.target as HTMLInputElement).checked;
    this.values.update((current) => {
      const previous = current[fieldId];
      const list = Array.isArray(previous) ? previous : [];
      const next = isChecked ? [...list, option] : list.filter((item) => item !== option);
      return { ...current, [fieldId]: next };
    });
  }

  /** 卡片上是否已有任何輸入內容（空白不算）。 */
  hasInput(): boolean {
    return Object.values(this.values()).some((value) =>
      Array.isArray(value) ? value.length > 0 : typeof value === 'string' ? value.trim().length > 0 : value !== null && value !== undefined,
    );
  }

  /** 在同一分頁前往：沒有輸入就直接前往；有輸入時先讓外層確認，不在這裡導頁。 */
  protected leave(event: MouseEvent): void {
    // 修飾鍵或中鍵：交給瀏覽器（例如在新分頁開啟），這張卡片不受影響。
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    if (this.hasInput()) {
      this.leaveRequested.emit();
      return;
    }
    void this.router.navigateByUrl(ACTIVITY_PATH);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.review.emit(this.values());
  }
}
