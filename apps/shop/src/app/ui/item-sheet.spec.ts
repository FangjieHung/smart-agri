import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BEEF } from '../domain/menu.testing';
import { ItemSheet } from './item-sheet';

describe('ItemSheet', () => {
  async function render() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(ItemSheet);
    fixture.componentRef.setInput('item', BEEF);
    fixture.componentRef.setInput('showComment', true);
    const saved: unknown[] = [];
    fixture.componentInstance.saved.subscribe((line) => saved.push(line));
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const click = async (selector: string) => {
      root.querySelector<HTMLElement>(selector)?.click();
      await fixture.whenStable();
    };
    const button = () => root.querySelector('[data-testid="add-to-cart"]') as HTMLButtonElement;
    return { fixture, root, saved, click, button };
  }

  it('will not add until every required group is chosen, and highlights the unmet ones', async () => {
    const { root, saved, click, button } = await render();
    expect(button().getAttribute('aria-disabled')).toBe('true');
    expect(button().textContent).toContain('NT$150');

    await click('[data-testid="add-to-cart"]');
    expect(saved).toEqual([]);
    expect(Array.from(root.querySelectorAll('.group--unmet')).map((element) => element.id)).toEqual(
      ['item-size', 'item-noodle'],
    );
    expect(root.querySelector('#item-size')?.textContent).toContain('請選擇份量');
    expect(root.querySelector('[aria-live="polite"]')?.textContent).toContain(
      '還沒選完：份量、麵條',
    );

    await click('#item-size-large');
    await click('#item-noodle-thin');
    await click('[aria-label="增加滷蛋"]');
    expect(root.querySelectorAll('.group--unmet')).toHaveLength(0);
    expect(button().getAttribute('aria-disabled')).toBe('false');
    expect(button().textContent).toContain('NT$185');
    await click('[aria-label="增加份數"]');
    expect(button().textContent).toContain('NT$370');

    await click('[data-testid="add-to-cart"]');
    expect(saved).toEqual([
      {
        itemId: 'beef-noodle',
        name: '紅燒牛肉麵',
        quantity: 2,
        comment: null,
        selections: [
          { groupId: 'size', optionId: 'large', count: 1, selections: null },
          { groupId: 'noodle', optionId: 'thin', count: 1, selections: null },
          { groupId: 'toppings', optionId: 'egg', count: 1, selections: null },
        ],
      },
    ]);
  });

  it('opens the second level for an option with groups, and shows what was chosen there', async () => {
    const { fixture, root, click, button } = await render();
    await click('#item-size-regular');
    await click('#item-noodle-wide');
    await click('#item-combo-set');
    expect(root.querySelector('app-option-sheet')).not.toBeNull();
    // 第二層沒選完不能確定。
    const confirm = () =>
      Array.from(root.querySelectorAll<HTMLButtonElement>('app-option-sheet button')).find((b) =>
        b.textContent?.includes('確定'),
      );
    confirm()?.click();
    await fixture.whenStable();
    expect(root.querySelector('app-option-sheet')).not.toBeNull();
    await click('#opt-l2-set-side-cucumber');
    await click('#opt-l2-set-drink-winter-melon');
    confirm()?.click();
    await fixture.whenStable();
    expect(root.querySelector('app-option-sheet')).toBeNull();
    expect(root.querySelector('.opt__sub')?.textContent).toContain('涼拌小黃瓜、冬瓜檸檬');
    expect(button().textContent).toContain('NT$220');
  });

  it('abandoning a newly chosen option on the second level unchooses it', async () => {
    const { root, click } = await render();
    await click('#item-combo-set');
    await click('app-option-sheet [aria-label="返回，不儲存"]');
    expect(root.querySelector('app-option-sheet')).toBeNull();
    expect(root.querySelector<HTMLInputElement>('#item-combo-set')?.checked).toBe(false);
  });
});
