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
});
