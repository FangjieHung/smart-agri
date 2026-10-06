import type { Signal } from '@angular/core';

export interface NavLeaf {
  route: string;
  label: string;
  icon: string;
  /** 選填的數字：「案件」旁的逾期件數（issue #250）；0 時不顯示。 */
  count?: Signal<number>;
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
