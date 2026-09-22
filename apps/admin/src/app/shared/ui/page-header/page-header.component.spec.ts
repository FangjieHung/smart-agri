import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PageHeaderComponent } from './page-header.component';

@Component({
  imports: [PageHeaderComponent],
  template:
    '<app-page-header title="知識庫" description="管理助理可以引用的公司資料"><button type="button" page-header-actions>新增知識庫</button></app-page-header>',
})
class HeaderHost {}

describe('PageHeaderComponent', () => {
  it('provides the page heading, description, and projected actions', async () => {
    const fixture = TestBed.createComponent(HeaderHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent).toBe('知識庫');
    expect(element.querySelector('p')?.textContent).toBe(
      '管理助理可以引用的公司資料',
    );
    expect(element.querySelector('header button')?.textContent).toBe(
      '新增知識庫',
    );
  });
});
