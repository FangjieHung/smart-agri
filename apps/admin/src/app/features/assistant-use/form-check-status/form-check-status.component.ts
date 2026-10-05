import { ChangeDetectionStrategy, Component } from '@angular/core';

/** 判斷中輪播的四句（issue #171 交接文件 ①），每句 1.5 秒、一輪 6 秒。 */
export const FORM_CHECK_PHRASES = [
  '正在整理問題重點…',
  '核對相關資訊…',
  '正在梳理可行做法…',
  '準備回覆內容…',
] as const;

/** 螢幕報讀器只聽到這一句固定文字，輪播的句子不朗讀。 */
export const FORM_CHECK_STATUS_TEXT = '助理正在整理你的問題';

/**
 * 伺服器正在判斷要不要跳出表單時的等待狀態（issue #171 ①）：只在收到 `form-check` 事件後顯示，
 * 位置是助理泡泡會出現的地方，但不用泡泡、不加圖示。純 CSS 動畫：掃光（`background-clip: text`）
 * 加上四句輪播；`prefers-reduced-motion: reduce` 時只顯示第一句、不掃光。
 */
@Component({
  // 直接當作訊息列表的 `<li>`：判斷完成時由外層的 `animate.leave` 加上 `.form-check-leave` 淡出。
  // eslint-disable-next-line @angular-eslint/component-selector
  selector: 'li[app-form-check-status]',
  templateUrl: './form-check-status.component.html',
  styleUrl: './form-check-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormCheckStatusComponent {
  protected readonly phrases = FORM_CHECK_PHRASES;
  protected readonly statusText = FORM_CHECK_STATUS_TEXT;
}
