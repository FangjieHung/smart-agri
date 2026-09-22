import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { HomePageComponent } from './home-page.component';

describe('HomePageComponent', () => {
  it('shows the create, continue, and pending-work areas for a signed-in account', async () => {
    await TestBed.configureTestingModule({
      imports: [HomePageComponent],
      providers: [
        provideRouter([]),
        {
          provide: DemoSessionService,
          useValue: { activeAccountId: () => 'account-smb-admin' },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomePageComponent);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('建立新助理');
    expect(page.textContent).toContain('繼續設定');
    expect(page.textContent).toContain('待處理事項');
  });
});
