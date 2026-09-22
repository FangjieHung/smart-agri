import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  PUBLISHING_STATUS_META,
  type PublishingChannelType,
  type PublishingChannelView,
} from '../../../core/domain/publishing.model';

const CHANNEL_ICONS: Readonly<Record<PublishingChannelType, string>> = {
  platform: '◎',
  website: '▭',
  line: '✉',
};

/** 單一管道的狀態卡：狀態同時以符號與文字呈現，不只靠顏色；動作連結由使用端投影。 */
@Component({
  selector: 'app-channel-card',
  templateUrl: './channel-card.component.html',
  styleUrl: './channel-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelCardComponent {
  readonly channel = input.required<PublishingChannelView>();
  readonly selected = input(false);

  protected readonly meta = computed(() => PUBLISHING_STATUS_META[this.channel().status]);
  protected readonly icon = computed(() => CHANNEL_ICONS[this.channel().type]);
  protected readonly headingId = computed(() => `${this.channel().type}-${this.channel().assistantId}-title`);
}
