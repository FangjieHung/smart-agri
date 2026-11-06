import type { Menu, MenuItem, OptionGroup } from '../api/types';

// 單元測試用的菜單：照 deploy/ordering-shop.example.json 的牛肉麵（份量、麵條、可重複的加料、帶第二層的套餐）。

const group = (
  partial: Partial<OptionGroup> & Pick<OptionGroup, 'id' | 'name' | 'min' | 'max'>,
): OptionGroup => ({
  repeatable: false,
  options: [],
  ...partial,
});

const option = (id: string, name: string, price = 0, optionGroups: OptionGroup[] = []) => ({
  id,
  name,
  price,
  optionGroups,
});

export const SIZE = group({
  id: 'size',
  name: '份量',
  min: 1,
  max: 1,
  options: [option('regular', '一般'), option('large', '大碗', 20)],
});
export const NOODLE = group({
  id: 'noodle',
  name: '麵條',
  min: 1,
  max: 1,
  options: [option('thin', '細麵'), option('wide', '寬麵'), option('rice-noodle', '米粉')],
});
export const TOPPINGS = group({
  id: 'toppings',
  name: '加料',
  min: 0,
  max: 5,
  repeatable: true,
  options: [option('egg', '滷蛋', 15), option('tofu', '豆干', 10), option('beef', '加肉', 60)],
});
export const SIDE = group({
  id: 'side',
  name: '套餐小菜',
  min: 1,
  max: 1,
  options: [option('cucumber', '涼拌小黃瓜'), option('seaweed', '海帶絲')],
});
export const DRINK = group({
  id: 'drink',
  name: '套餐飲料',
  min: 1,
  max: 1,
  options: [
    option('black-tea', '紅茶'),
    option('soy-milk', '豆漿'),
    option('winter-melon', '冬瓜檸檬', 10),
  ],
});
export const COMBO = group({
  id: 'combo',
  name: '升級套餐',
  min: 0,
  max: 1,
  options: [option('set', '小菜飲料套餐', 60, [SIDE, DRINK])],
});

export const BEEF: MenuItem = {
  id: 'beef-noodle',
  name: '紅燒牛肉麵',
  price: 150,
  description: '慢燉六小時的半筋半肉。',
  imageUrl: null,
  optionGroups: [SIZE, NOODLE, TOPPINGS, COMBO],
  state: null,
};

export const WONTON: MenuItem = {
  id: 'wonton-soup',
  name: '鮮肉餛飩湯',
  price: 70,
  description: null,
  imageUrl: null,
  optionGroups: [],
  state: null,
};

export function menu(version: number, items: MenuItem[] = [BEEF, WONTON]): Menu {
  return {
    version,
    categories: [{ id: 'noodles', name: '麵食', description: null, availability: null, items }],
  };
}
