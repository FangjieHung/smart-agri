import { BEEF, COMBO, NOODLE, SIZE, TOPPINGS } from './menu.testing';
import {
  controlOf,
  countIn,
  describe as describeChoices,
  fromSelections,
  issues,
  maxCountFor,
  NO_CHOICES,
  removeOption,
  requirementText,
  setCount,
  setSub,
  toggle,
  toSelections,
  unitPrice,
  unmetGroupIds,
  type Choices,
} from './selection';

/** 選好份量與麵條的牛肉麵。 */
function basic(size = 'regular', noodle = 'thin'): Choices {
  return toggle(NOODLE, toggle(SIZE, NO_CHOICES, size), noodle);
}

describe('option selection', () => {
  it('draws required single choices as radios, optional single choices as checkboxes, repeatable groups as steppers', () => {
    expect(controlOf(SIZE)).toBe('radio');
    expect(controlOf(COMBO)).toBe('checkbox');
    expect(controlOf(TOPPINGS)).toBe('stepper');
    expect(requirementText(SIZE)).toBe('必選 1 個');
    expect(requirementText(COMBO)).toBe('選填');
    expect(requirementText(TOPPINGS)).toBe('選填，最多 5 個');
    expect(requirementText({ ...SIZE, min: 1, max: 2 })).toBe('必選 1–2 個');
  });

  it('a radio replaces the choice; an optional single checkbox can be cleared', () => {
    let choices = toggle(SIZE, NO_CHOICES, 'regular');
    choices = toggle(SIZE, choices, 'large');
    expect(choices['size']).toEqual([{ optionId: 'large', count: 1, sub: {} }]);
    expect(toggle(SIZE, choices, 'large')).toBe(choices);

    choices = toggle(COMBO, choices, 'set');
    expect(countIn(choices, 'combo')).toBe(1);
    choices = toggle(COMBO, choices, 'set');
    expect(choices['combo']).toBeUndefined();
  });

  it('a multiple-choice group stops at its maximum', () => {
    const group = { ...NOODLE, min: 0, max: 2 };
    let choices = toggle(group, NO_CHOICES, 'thin');
    choices = toggle(group, choices, 'wide');
    expect(toggle(group, choices, 'rice-noodle')).toBe(choices);
    expect(countIn(choices, 'noodle')).toBe(2);
  });

  it('repeatable counts are clamped to what is left of the group maximum', () => {
    let choices = setCount(TOPPINGS, NO_CHOICES, 'egg', 3);
    expect(maxCountFor(TOPPINGS, choices, 'tofu')).toBe(2);
    choices = setCount(TOPPINGS, choices, 'tofu', 9);
    expect(choices['toppings']).toEqual([
      { optionId: 'egg', count: 3, sub: {} },
      { optionId: 'tofu', count: 2, sub: {} },
    ]);
    choices = setCount(TOPPINGS, choices, 'egg', 0);
    expect(choices['toppings']).toEqual([{ optionId: 'tofu', count: 2, sub: {} }]);
  });

  it('reports every unmet group at every level, with the server paths', () => {
    expect(issues(BEEF.optionGroups, NO_CHOICES)).toEqual([
      { code: 'too-few', path: 'size', groupId: 'size' },
      { code: 'too-few', path: 'noodle', groupId: 'noodle' },
    ]);
    const withSet = toggle(COMBO, basic(), 'set');
    expect(issues(BEEF.optionGroups, withSet)).toEqual([
      { code: 'too-few', path: 'combo.set.side', groupId: 'side' },
      { code: 'too-few', path: 'combo.set.drink', groupId: 'drink' },
    ]);
    // 第二層沒選完，算在第一層的「升級套餐」。
    expect(unmetGroupIds(BEEF.optionGroups, withSet)).toEqual(['combo']);
    expect(issues(BEEF.optionGroups, basic())).toEqual([]);
  });

  it('flags a count above 1 in a group that is not repeatable, like the server', () => {
    const bad: Choices = {
      size: [{ optionId: 'large', count: 2, sub: {} }],
      noodle: [{ optionId: 'thin', count: 1, sub: {} }],
    };
    expect(issues(BEEF.optionGroups, bad).map((issue) => issue.code)).toEqual([
      'not-repeatable',
      'too-many',
    ]);
  });

  it('prices like MenuPricing: item + Σ count × (option + what is chosen under it)', () => {
    let choices = basic('large');
    choices = setCount(TOPPINGS, choices, 'egg', 2);
    choices = setSub(toggle(COMBO, choices, 'set'), 'combo', 'set', {
      side: [{ optionId: 'cucumber', count: 1, sub: {} }],
      drink: [{ optionId: 'winter-melon', count: 1, sub: {} }],
    });
    // 150 + 20 + 2 × 15 + 1 × (60 + 10) = 270（伺服器錄到的總額也是 270）。
    expect(unitPrice(BEEF, choices)).toBe(270);

    // 可重複的選項帶下一層時，下一層的加價每一份都算。
    const repeatableCombo = { ...COMBO, max: 3, repeatable: true };
    const item = { ...BEEF, optionGroups: [repeatableCombo] };
    const twoSets = setSub(setCount(repeatableCombo, NO_CHOICES, 'set', 2), 'combo', 'set', {
      side: [{ optionId: 'cucumber', count: 1, sub: {} }],
      drink: [{ optionId: 'winter-melon', count: 1, sub: {} }],
    });
    expect(unitPrice(item, twoSets)).toBe(150 + 2 * (60 + 10));
  });

  it('sends selections in the menu order with nested selections, and restores them', () => {
    let choices = setCount(TOPPINGS, NO_CHOICES, 'beef', 1);
    choices = setCount(TOPPINGS, choices, 'egg', 2);
    choices = toggle(NOODLE, toggle(SIZE, choices, 'large'), 'wide');
    choices = setSub(toggle(COMBO, choices, 'set'), 'combo', 'set', {
      drink: [{ optionId: 'black-tea', count: 1, sub: {} }],
      side: [{ optionId: 'seaweed', count: 1, sub: {} }],
    });
    const selections = toSelections(BEEF.optionGroups, choices);
    expect(selections).toEqual([
      { groupId: 'size', optionId: 'large', count: 1, selections: null },
      { groupId: 'noodle', optionId: 'wide', count: 1, selections: null },
      { groupId: 'toppings', optionId: 'egg', count: 2, selections: null },
      { groupId: 'toppings', optionId: 'beef', count: 1, selections: null },
      {
        groupId: 'combo',
        optionId: 'set',
        count: 1,
        selections: [
          { groupId: 'side', optionId: 'seaweed', count: 1, selections: null },
          { groupId: 'drink', optionId: 'black-tea', count: 1, selections: null },
        ],
      },
    ]);
    const restored = fromSelections(BEEF.optionGroups, selections);
    expect(restored).not.toBeNull();
    const back = restored ?? NO_CHOICES;
    expect(unitPrice(BEEF, back)).toBe(unitPrice(BEEF, choices));
    expect(describeChoices(BEEF.optionGroups, back)).toBe(
      '大碗、寬麵、滷蛋 ×2、加肉、小菜飲料套餐（海帶絲、紅茶）',
    );
    expect(toSelections(BEEF.optionGroups, NO_CHOICES)).toBeNull();
  });

  it('refuses stored selections the menu no longer has', () => {
    expect(
      fromSelections(BEEF.optionGroups, [
        { groupId: 'size', optionId: 'huge', count: 1, selections: null },
      ]),
    ).toBeNull();
    expect(
      fromSelections(BEEF.optionGroups, [
        { groupId: 'gone', optionId: 'large', count: 1, selections: null },
      ]),
    ).toBeNull();
    expect(
      fromSelections(BEEF.optionGroups, [
        {
          groupId: 'combo',
          optionId: 'set',
          count: 1,
          selections: [{ groupId: 'side', optionId: 'fries', count: 1, selections: null }],
        },
      ]),
    ).toBeNull();
  });

  it('removes a newly chosen option when its second-level sheet is abandoned', () => {
    const choices = toggle(COMBO, basic(), 'set');
    expect(removeOption(choices, 'combo', 'set')['combo']).toBeUndefined();
    expect(removeOption(choices, 'combo', 'nothing')).toBe(choices);
  });
});
