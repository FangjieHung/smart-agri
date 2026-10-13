import { DatePipe, DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import {
  handoffChannelInSentence,
  needsNotificationEmailReminder,
  VISITOR_HANDOFF_CHANNEL_LABELS,
  type VisitorHandoffChannel,
  type VisitorHandoffStatusFilter,
  type VisitorHandoffView,
} from '../../core/domain/handoff.model';
import { HandoffsRepository } from '../../core/repositories/handoffs.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DEMO_ACCOUNT_BY_ROLE } from '../../core/session/api-mode/demo-identity-bridge';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { NAV_COUNTS_CHANGED_EVENT } from '../../layout/workspace-layout/nav-counts';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';

/**
 * 轉真人通知（issue #355）：我的助理在官網或 LINE 回覆了「找不到資料時的回覆」的紀錄——哪個助理、哪個管道、
 * 什麼時間——以及標記已處理。系統不保存訪客的內容，所以沒有對話可開：LINE 到 Official Account Manager 的聊天回覆，
 * 官網訪客依回覆裡的聯絡方式找來；每一則都連到該助理的發布設定。
 *
 * 自己還沒填通知 Email 時，上方提醒到「我的通知」填寫（issue #362）；填了、關掉轉真人 Email，或部署沒有寄信伺服器
 * 時不提醒。設定讀不到時也不提醒（清單照常）。外部客戶不會是助理的擁有者，也沒有「我的通知」，不提醒。
 */
@Component({
  selector: 'app-handoffs-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent],
  templateUrl: './handoffs-page.component.html',
  styleUrl: './handoffs-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HandoffsPageComponent {
  private readonly handoffs = inject(HandoffsRepository);
  private readonly session = inject(DemoSessionService);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly status = signal<VisitorHandoffStatusFilter>('pending');
  protected readonly resource = repositoryResource({
    params: () => {
      const account = this.session.activeAccountId();
      return account === null ? undefined : { account, status: this.status() };
    },
    stream: ({ status }) => this.handoffs.list(status),
  });
  protected readonly view = this.resource.view;
  protected readonly items = computed(() => {
    const result = this.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  private readonly settingsResource = repositoryResource({
    params: () => {
      const account = this.session.activeAccountId();
      return account === null || account === DEMO_ACCOUNT_BY_ROLE['external-customer'] ? undefined : account;
    },
    stream: () => this.handoffs.notificationSettings(),
  });
  protected readonly remindEmail = computed(() => {
    const result = this.settingsResource.view();
    return result.status === 'ready' && needsNotificationEmailReminder(result.data);
  });

  /** 正在標記的那一則；標記中按鈕停用，不會送出兩次。 */
  protected readonly busyId = signal<string | null>(null);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected channelLabel(channel: VisitorHandoffChannel): string {
    return VISITOR_HANDOFF_CHANNEL_LABELS[channel];
  }

  /** 「查看 LINE 發布設定」「將「客服助理」LINE 的轉真人標記為已處理」裡的管道名稱。 */
  protected inSentence(channel: VisitorHandoffChannel): string {
    return handoffChannelInSentence(channel);
  }

  protected show(status: VisitorHandoffStatusFilter): void {
    this.feedback.set('');
    this.error.set('');
    this.status.set(status);
  }

  protected markHandled(item: VisitorHandoffView): void {
    if (this.busyId() !== null) return;
    this.busyId.set(item.id);
    this.feedback.set('');
    this.error.set('');
    this.handoffs
      .markHandled(item.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busyId.set(null);
          if (result.status === 'ready') {
            this.feedback.set(`已將「${item.assistantName}」${this.inSentence(item.channel).trimStart()}的轉真人標記為已處理。`);
            this.resource.reload();
            // 側欄的待處理數字只在換頁時更新；這裡請外框立刻重新讀取。
            this.document.dispatchEvent(new Event(NAV_COUNTS_CHANGED_EVENT));
          } else if (result.status === 'permission-denied') {
            this.error.set(result.message);
          }
        },
        error: () => {
          this.busyId.set(null);
          this.error.set('目前無法標記已處理，請稍後再試。');
        },
      });
  }
}
