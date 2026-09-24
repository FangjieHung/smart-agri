import { TestBed } from '@angular/core/testing';
import { HeaderComponent } from './header.component';

describe('HeaderComponent', () => {
  it('gives the compact navigation control an accessible name', async () => {
    await TestBed.configureTestingModule({ imports: [HeaderComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HeaderComponent);
    fixture.componentRef.setInput('currentTitle', '首頁');
    fixture.componentRef.setInput('currentGroupLabel', null);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.menu-toggle')?.getAttribute('aria-label')).toBe('開啟導覽選單');
  });

  it('shows the route only as a breadcrumb so the page header owns the heading', async () => {
    await TestBed.configureTestingModule({ imports: [HeaderComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HeaderComponent);
    fixture.componentRef.setInput('currentTitle', '知識庫');
    fixture.componentRef.setInput('currentGroupLabel', '建立');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.breadcrumb [aria-current="page"]')?.textContent?.trim()).toBe('知識庫');
    expect(host.textContent?.match(/知識庫/g)?.length).toBe(1);
  });
});
