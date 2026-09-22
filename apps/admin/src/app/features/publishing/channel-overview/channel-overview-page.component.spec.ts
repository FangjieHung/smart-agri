import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { providePublishingTesting } from '../publishing.testing';
import { ChannelOverviewPageComponent } from './channel-overview-page.component';

function render(accountId: Parameters<typeof providePublishingTesting>[0] = 'account-smb-admin') {
  const { providers } = providePublishingTesting(accountId);
  TestBed.configureTestingModule({
    imports: [ChannelOverviewPageComponent],
    providers: [provideRouter([]), ...providers],
  });
  const fixture = TestBed.createComponent(ChannelOverviewPageComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ChannelOverviewPageComponent', () => {
  it('shows three channel cards per assistant with a text and symbol status', () => {
    const page = render();

    expect(page.querySelector('h1')?.textContent).toContain('發布管道');
    expect(page.textContent).toContain('Demo，不會連接外部服務');
    const groups = page.querySelectorAll('section.assistant-channels');
    expect(groups).toHaveLength(2);
    const cards = Array.from(groups[0].querySelectorAll('app-channel-card'));
    expect(cards.map((card) => card.querySelector('h3')?.textContent?.trim())).toEqual(['平台內分享', '官網嵌入', 'LINE']);
    for (const card of cards) {
      const status = card.querySelector('.channel-status');
      expect(status?.querySelector('[aria-hidden="true"]')?.textContent?.trim()).not.toBe('');
      expect(status?.textContent).toMatch(/尚未設定|測試中|已發布|需要處理|已暫停/);
    }
  });

  it('shows all five unified statuses and marks only the failing channel as needing attention', () => {
    const page = render();
    const statuses = Array.from(page.querySelectorAll('.channel-status__label'), (node) => node.textContent?.trim());

    expect(new Set(statuses)).toEqual(new Set(['尚未設定', '測試中', '已發布', '需要處理', '已暫停']));
    const attention = page.querySelectorAll('[data-status="needs-attention"]');
    expect(attention).toHaveLength(1);
    expect(attention[0].textContent).toContain('LINE');
    const link = attention[0].querySelector('a');
    expect(link?.textContent).toContain('前往處理');
    expect(link?.getAttribute('href')).toBe('/app/assistants/assistant-customer-service/publishing?channel=line');
  });

  it('shows an empty state for accounts without their own assistants', () => {
    const page = render('account-internal-employee');

    expect(page.querySelectorAll('app-channel-card')).toHaveLength(0);
    expect(page.textContent).toContain('目前沒有可設定發布管道的助理');
    expect(page.textContent).not.toContain('客服助理');
  });
});
