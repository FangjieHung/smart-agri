import type { Signal } from '@angular/core';
import type { AccountPermission } from '../../core/domain/account.model';

export interface NavLeaf {
  route: string;
  label: string;
  icon: string;
  /** 選填的數字：「案件」旁的逾期件數（issue #250）；0 時不顯示。 */
  count?: Signal<number>;
  /** 報讀數字時接在後面的字（預設「件逾期」；「轉真人」是「則待處理」，issue #355）。 */
  countLabel?: string;
  /** 只給有這個權限的帳號顯示（「點餐」要 `manage-orders`，issue #399）。 */
  permission?: AccountPermission;
}

export interface NavGroup {
  label: string;
  icon: string;
  children: NavLeaf[];
}

export type NavEntry = NavLeaf | NavGroup;

export function isNavGroup(entry: NavEntry): entry is NavGroup {
  return 'children' in entry;
}
