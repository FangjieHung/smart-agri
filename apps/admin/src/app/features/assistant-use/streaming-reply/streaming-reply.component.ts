import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** `[n]`，也接受全形的 `【n】`、`［n］`（與後端引用驗證的解析規則相同）。 */
const CITATION_MARKER = /(\[\d+\]|【\d+】|［\d+］)/;

interface Segment {
  readonly text: string;
  readonly citation: boolean;
}

/**
 * 串流中的助理回答（issue #80）。引用標記在收到最終的 `smartagri.reply` 之前只是
 * 「確認中」的樣式、不能點開；收到之後整則換成 `app-chat-message`。
 * 外層的對話紀錄在串流期間是 `aria-busy`，這裡的文字不會被逐字朗讀。
 */
@Component({
  selector: 'app-streaming-reply',
  templateUrl: './streaming-reply.component.html',
  styleUrl: './streaming-reply.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StreamingReplyComponent {
  readonly text = input.required<string>();

  protected readonly segments = computed<readonly Segment[]>(() =>
    this.text()
      .split(CITATION_MARKER)
      .filter((part) => part.length > 0)
      .map((part) => ({ text: part, citation: CITATION_MARKER.test(part) })),
  );
  protected readonly hasCitations = computed(() => this.segments().some((segment) => segment.citation));
}
