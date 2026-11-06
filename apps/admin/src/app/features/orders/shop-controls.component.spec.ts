import { TestBed } from '@angular/core/testing';
import { of, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { OrderingItemView, OrderingMenuView, OrderingSummaryView } from '../../core/domain/ordering.model';
import { OrderingRepository, type OrderingWriteResult } from '../../core/repositories/ordering.repository';
import { ShopControlsComponent } from './shop-controls.component';

/** 一定要找到的元素（找不到就讓測試失敗）。 */
function el<T extends Element = HTMLElement>(root: ParentNode, selector: string): T {
  const found = root.querySelector<T>(selector as never);
  if (!found) throw new Error(`no ${selector}`);
  return found;
}

const NOW = Date.parse('2026-11-07T03:40:00Z');

const summary: OrderingSummaryView = {
  now: new Date(NOW).toISOString(), businessDate: '2026-11-07', timeZone: 'Asia/Taipei', shopName: '王記麵店', slug: 'wangji-noodles',
  pendingCount: 0, activeCount: 0, latestOrderAt: null, changedAt: null, paused: false, asapBand: '15-30',
  asap: { minMinutes: 15, maxMinutes: 30 }, services: { takeoutScheduled: true, takeoutAsap: true, delivery: false, deliveryAsap: false }, menuVersion: 1, shopVersion: 1,
};

const menu: OrderingMenuView = {
  now: summary.now,
  menuVersion: 1,
  categories: [
    { id: 'noodles', name: '麵食', items: [
      { id: 'beef-noodle', name: '紅燒牛肉麵', price: 150, availability: 'available', resetsAt: null },
      { id: 'zhajiang', name: '炸醬麵', price: 80, availability: 'sold-out-today', resetsAt: '2026-11-08T03:00:00Z' },
    ] },
    { id: 'sides', name: '小菜', items: [{ id: 'cucumber', name: '涼拌小黃瓜', price: 40, availability: 'unavailable', resetsAt: null }] },
  ],
};

function setup(setItemState?: (id: string, state: string) => Observable<OrderingWriteResult<OrderingItemView>>) {
  TestBed.resetTestingModule();
  const repository = {
    setPaused: vi.fn((paused: boolean) => of({ status: 'ready', data: { ...summary, paused, shopVersion: 2 } })),
    setAsapBand: vi.fn((band: string) => of({ status: 'ready', data: { ...summary, asapBand: band, shopVersion: 2 } })),
    items: vi.fn(() => of({ status: 'ready', data: menu })),
    setItemState: vi.fn(setItemState ?? ((id: string, state: string) => of({ status: 'ready', data: { ...menu.categories[0].items[0], id, availability: state, resetsAt: state === 'sold-out-today' ? '2026-11-08T03:00:00Z' : null } }))),
  };
  TestBed.configureTestingModule({ imports: [ShopControlsComponent], providers: [{ provide: OrderingRepository, useValue: repository }] });
  const fixture = TestBed.createComponent(ShopControlsComponent);
  fixture.componentRef.setInput('summary', summary);
  fixture.componentRef.setInput('now', NOW);
  const changes: OrderingSummaryView[] = [];
  fixture.componentInstance.summaryChange.subscribe((value) => changes.push(value));
  fixture.detectChanges();
  const page = fixture.nativeElement as HTMLElement;
  const openItems = () => {
    const details = el<HTMLDetailsElement>(page, '[data-ordering-items]');
    details.open = true;
    details.dispatchEvent(new Event('toggle'));
    fixture.detectChanges();
  };
  return { fixture, page, repository, changes, openItems };
}

describe('ShopControlsComponent (issue #399)', () => {
  it('asks before pausing, and resumes without asking', () => {
    const { fixture, page, repository, changes } = setup();
    const toggle = el<HTMLButtonElement>(page, '[data-ordering-pause]');
    expect([toggle.getAttribute('role'), toggle.getAttribute('aria-checked')]).toEqual(['switch', 'false']);
    toggle.click();
    fixture.detectChanges();
    expect(page.querySelector('[role="dialog"]')?.textContent).toContain('暫停接單？');
    expect(repository.setPaused).not.toHaveBeenCalled();
    el<HTMLButtonElement>(page, '[role="dialog"] button[data-tone="danger"]').click();
    fixture.detectChanges();
    expect(repository.setPaused).toHaveBeenCalledWith(true);
    expect(changes.at(-1)?.paused).toBe(true);
    expect(page.querySelector('[role="dialog"]')).toBeNull();

    fixture.componentRef.setInput('summary', { ...summary, paused: true, shopVersion: 2 });
    fixture.detectChanges();
    expect(page.querySelector('#shop-pause-state')?.textContent).toContain('已暫停');
    el<HTMLButtonElement>(page, '[data-ordering-pause]').click();
    fixture.detectChanges();
    expect(repository.setPaused).toHaveBeenLastCalledWith(false);
    expect(page.textContent).toContain('已恢復接單。');
  });

  it('changes the ASAP band from the four choices', () => {
    const { fixture, page, repository, changes } = setup();
    const labels = Array.from(page.querySelectorAll('input[name="shop-asap-band"]')).map((input) => input.parentElement?.textContent?.trim());
    expect(labels).toEqual(['15 分鐘內', '15–30 分鐘', '30–45 分鐘', '45–60 分鐘']);
    el<HTMLInputElement>(page, 'input[name="shop-asap-band"][value="30-45"]').click();
    fixture.detectChanges();
    expect(repository.setAsapBand).toHaveBeenCalledWith('30-45');
    expect(changes.at(-1)?.asapBand).toBe('30-45');
  });

  it('shows the ASAP band when only 外送即時 is offered, and hides it without any ASAP service (#401)', () => {
    const { fixture, page } = setup();
    fixture.componentRef.setInput('summary', { ...summary, services: { takeoutScheduled: true, takeoutAsap: false, delivery: true, deliveryAsap: true } });
    fixture.detectChanges();
    expect(page.querySelectorAll('input[name="shop-asap-band"]')).toHaveLength(4);

    fixture.componentRef.setInput('summary', { ...summary, services: { takeoutScheduled: true, takeoutAsap: false, delivery: true, deliveryAsap: false } });
    fixture.detectChanges();
    expect(page.querySelectorAll('input[name="shop-asap-band"]')).toHaveLength(0);
  });

  it('lists the items when opened, finds one by name, and marks it sold out until the next opening', () => {
    const { fixture, page, repository, openItems } = setup();
    expect(repository.items).not.toHaveBeenCalled();
    openItems();
    expect(page.querySelector('summary')?.textContent).toContain('1 項今日售完、1 項停售');
    expect(page.querySelector('[data-item="zhajiang"]')?.textContent).toContain('明天 11:00 自動恢復供應');

    const search = el<HTMLInputElement>(page, '[data-ordering-item-search]');
    search.value = '牛肉';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(Array.from(page.querySelectorAll('[data-item]')).map((item) => item.getAttribute('data-item'))).toEqual(['beef-noodle']);

    el<HTMLInputElement>(page, '[data-item="beef-noodle"] input[value="sold-out-today"]').click();
    fixture.detectChanges();
    expect(repository.setItemState).toHaveBeenCalledWith('beef-noodle', 'sold-out-today');
    expect(page.querySelector('[data-item="beef-noodle"]')?.getAttribute('data-availability')).toBe('sold-out-today');
    expect(page.textContent).toContain('「紅燒牛肉麵」改為今日售完，明天 11:00 自動恢復供應。');
  });

  it('puts the choice back when saving fails', () => {
    const { fixture, page, openItems } = setup(() => of({ status: 'item-not-found', message: '目前的菜單沒有這個品項，請重新整理。' }));
    openItems();
    const radio = el<HTMLInputElement>(page, '[data-item="beef-noodle"] input[value="unavailable"]');
    radio.click();
    fixture.detectChanges();
    expect(radio.checked).toBe(false);
    expect(el<HTMLInputElement>(page, '[data-item="beef-noodle"] input[value="available"]').checked).toBe(true);
    expect(page.querySelector('[role="alert"]')?.textContent).toContain('目前的菜單沒有這個品項');
  });
});
