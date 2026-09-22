import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';

export type PublishingChannelId =
  'channel-website' | 'channel-public-link' | 'channel-qr-code';

export type PublishingChannelType = 'website-embed' | 'public-link' | 'qr-code';

export type PublishingConnectionStatus =
  'connected' | 'disconnected' | 'not-configured';

export type PublishingVisibility = 'authorized-users-only';

export interface PublishingChannelView {
  readonly id: PublishingChannelId;
  readonly assistantId: AssistantId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly type: PublishingChannelType;
  readonly connectionStatus: PublishingConnectionStatus;
  readonly visibility: PublishingVisibility;
}
