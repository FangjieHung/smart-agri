import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LandingPageComponent } from './landing-page.component';

describe('LandingPageComponent', () => {
  it('explains the assistant value and links visitors to the demo login without offering a public trial', async () => {
    await TestBed.configureTestingModule({
      imports: [LandingPageComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingPageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('h1')?.textContent).toContain('讓每一次服務回覆，都更有依據');
    expect(page.textContent).toContain('知識庫');
    expect(page.textContent).toContain('資料使用說明');
    expect(page.querySelector('a[href="/login"]')?.textContent).toContain('進入 Demo');
    expect(page.textContent).not.toContain('免費試用');
  });
});
