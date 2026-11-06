import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { App, slugFromPath } from './app';

describe('shop placeholder', () => {
  it('reads the shop slug from /order/{slug}', () => {
    expect(slugFromPath('/order/wangji-noodles')).toBe('wangji-noodles');
    expect(slugFromPath('/order/WangJi/o')).toBe('wangji');
    expect(slugFromPath('/order/')).toBeNull();
    expect(slugFromPath('/reserve/wangji')).toBeNull();
  });

  it('renders its heading', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')?.textContent).toBe('線上點餐');
  });
});
