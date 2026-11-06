import type { components } from './api-schema';

// 顧客點餐網頁用到的公開 API 型別（tag「Ordering (public)」）。api-schema.ts 由 `nx run shop:api-types`
// 從 apps/api/openapi/v1.json 產生，只有型別、不進 bundle；這裡取別名讓程式不必寫長路徑。
type Schemas = components['schemas'];

export type ShopInfo = Schemas['OrderingShopInfoView'];
export type Menu = Schemas['OrderingMenuView'];
export type MenuCategory = Schemas['OrderingCategoryView'];
export type MenuItem = Schemas['OrderingItemView'];
export type OptionGroup = Schemas['OrderingGroupView'];
export type MenuOption = Schemas['OrderingOptionView'];
export type Availability = Schemas['OrderingAvailabilityView'];
export type Slots = Schemas['OrderingSlotsView'];
export type Zone = Schemas['OrderingZoneView'];
export type DeliveryInfo = Schemas['OrderingDeliveryInfoView'];
export type OrderDelivery = Schemas['OrderingDeliveryView'];
export type SlotDate = Schemas['OrderingDateView'];
export type Slot = Schemas['OrderingSlotView'];
export type OrderRequest = Schemas['OrderingOrderRequest'];
export type LineRequest = Schemas['OrderingLineRequest'];
export type SelectionRequest = Schemas['OrderingSelectionRequest'];
export type Submitted = Schemas['OrderingSubmittedView'];
export type Order = Schemas['OrderingOrderView'];
export type OrderLine = Schemas['OrderingOrderLine'];
export type ChosenOption = Schemas['ChosenOption'];
export type ServiceType = Schemas['OrderingServiceType'];
export type OrderStatus = NonNullable<Schemas['OrderingOrderStatus']>;
export type LineError = Schemas['OrderingLineError'];
export type DeclineReason = NonNullable<Schemas['OrderingDeclineReason']>;
export type CancelReason = NonNullable<Schemas['OrderingCancelReason']>;
export type Canceller = NonNullable<Schemas['OrderingCanceller']>;

/**
 * 公開點餐 API 的拒絕回應。`OrderingProblem` 的每個欄位都會送出，但 `429 rate-limited` 是共用的頻率限制回應，
 * 只有 type、title、status、reason、message 五個欄位（設計文件 §4.2），所以其餘欄位一律當成可能缺少。
 */
export type Problem = Pick<Schemas['OrderingProblem'], 'status' | 'reason' | 'message'> &
  Partial<Omit<Schemas['OrderingProblem'], 'status' | 'reason' | 'message'>>;
