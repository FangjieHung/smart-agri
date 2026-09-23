import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { DashboardPageComponent } from './dashboard-page.component';

describe('DashboardPageComponent', () => {
  async function render(): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [DashboardPageComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(DashboardPageComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders the activity page title', async () => {
    const host = await render();

    expect(host.querySelector('h1')?.textContent).toContain('對話與回報紀錄');
  });

  it('explains that conversations stay with the account that started them', async () => {
    const host = await render();

    expect(host.textContent).toContain('助理建立者看不到其他帳號的提問與回答');
  });

  it('links to where each kind of record is actually kept', async () => {
    const host = await render();
    const hrefs = Array.from(host.querySelectorAll('a')).map((a) => a.getAttribute('href'));

    expect(hrefs).toContain('/app/assistants');
    expect(hrefs).toContain('/app/databases');
    expect(hrefs).toContain('/app/chat');
  });
});
