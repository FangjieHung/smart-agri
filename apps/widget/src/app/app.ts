import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  afterRenderEffect,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import {
  ChatMessageComponent,
  CitationDrawerComponent,
  StreamingReplyComponent,
  type CitationRequest,
} from '@smart-agri/chat';
import { applyBrandColor } from './brand-color';
import { ComposerComponent } from './composer.component';
import { WidgetStore } from './widget-store';
import { WIDGET_CONTEXT, WIDGET_PARENT } from './widget-context';
import { SERVICE_PAUSED_TEXT, SERVICE_UNAVAILABLE_TEXT, type RunFailure } from './widget.model';

/** 載入器接受的唯一訊息（契約見 `apps/embed-loader/README.md`）。 */
export const CLOSE_MESSAGE = { type: 'smartagri:close' } as const;

@Component({
  selector: 'app-root',
  imports: [ChatMessageComponent, StreamingReplyComponent, CitationDrawerComponent, ComposerComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // 鍵盤事件不會穿過 iframe 到載入器，所以視窗自己處理 Esc。
  host: { '(document:keydown.escape)': 'onEscape($event)' },
})
export class App {
  protected readonly store = inject(WidgetStore);
  private readonly context = inject(WIDGET_CONTEXT);
  private readonly parent = inject(WIDGET_PARENT);
  private readonly document = inject(DOCUMENT);

  protected readonly unavailableText = SERVICE_UNAVAILABLE_TEXT;
  protected readonly pausedText = SERVICE_PAUSED_TEXT;
  protected readonly drawer = signal<CitationRequest | null>(null);

  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    void this.store.init();
    effect(() => {
      applyBrandColor(this.document.documentElement, this.store.assistant()?.brandColor);
      this.document.title = this.store.displayName();
    });
    afterRenderEffect(() => {
      // 新訊息、串流文字一出現就捲到底。
      this.store.messages();
      this.store.run();
      const element = this.log()?.nativeElement;
      if (element) element.scrollTop = element.scrollHeight;
    });
  }

  protected close(): void {
    // 目標來源一定是 `host` 參數：沒有或不合法就不送（絕不用 '*'）。
    const target = this.context.hostOrigin;
    if (target !== null) this.parent?.postMessage(CLOSE_MESSAGE, target);
  }

  protected onEscape(event: Event): void {
    const keyboard = event as KeyboardEvent;
    // 輸入法組字中的 Esc 是取消組字；引用抽屜開著時 Esc 只關抽屜。
    if (keyboard.isComposing || keyboard.defaultPrevented) return;
    if (this.drawer() !== null || (keyboard.target as Element | null)?.closest?.('app-citation-drawer')) return;
    this.close();
  }

  protected openCitations(request: CitationRequest): void {
    this.drawer.set(request);
  }

  protected closeDrawer(): void {
    const trigger = this.drawer()?.trigger;
    this.drawer.set(null);
    trigger?.focus();
  }

  protected failureText(failure: RunFailure): string {
    switch (failure.kind) {
      case 'rate-limited':
        return '問題太頻繁了，請稍後再試';
      case 'busy':
      case 'invalid':
        return failure.message;
      case 'unavailable':
        return '對話服務暫時無法使用，請稍後再試。';
      case 'session-expired':
        return '連線已逾時，請再試一次。';
      case 'network':
        return '連線中斷，這則問題沒有取得回答，請再試一次。';
    }
  }

  protected canRetry(failure: RunFailure): boolean {
    return failure.kind !== 'invalid';
  }

  protected formatCountdown(seconds: number): string {
    return seconds < 60 ? `${seconds} 秒` : `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`;
  }
}
