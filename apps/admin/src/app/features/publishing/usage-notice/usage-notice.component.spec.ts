import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { DemoScenario } from '../../../core/repositories/demo-repository';
import { providePublishingTesting } from '../publishing.testing';
import { UsageNoticeComponent } from './usage-notice.component';

async function render(
  variant: 'summary' | 'banner',
  options: { accountId?: Parameters<typeof providePublishingTesting>[0]; scenario?: DemoScenario } = {},
): Promise<HTMLElement> {
  const { providers, repository } = providePublishingTesting(options.accountId);
  if (options.scenario !== undefined) repository.setScenario(options.scenario);
  TestBed.configureTestingModule({ imports: [UsageNoticeComponent], providers: [provideRouter([]), ...providers] });
  const fixture = TestBed.createComponent(UsageNoticeComponent);
  fixture.componentRef.setInput('variant', variant);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const text = (element: HTMLElement) => (element.textContent ?? '').replace(/\s+/g, ' ');

describe('UsageNoticeComponent (issue #203)', () => {
  describe('banner (home page)', () => {
    it('near: shows used / limit with thousands separators and says external replies pause at 100%', async () => {
      const page = await render('banner');
      const banner = page.querySelector('[role="status"]');

      expect(banner?.getAttribute('data-state')).toBe('near');
      expect(text(page)).toContain('接近上限');
      expect(text(page)).toContain('864,300 / 1,000,000 tokens（86%）');
      expect(text(page)).toContain('達到 100% 時');
      expect(text(page)).toContain('對外回覆會暫停');
      expect(text(page)).toContain('組織內部使用不受影響');
      expect(page.querySelector('a')?.getAttribute('href')).toBe('/app/channels');
    });

    it('exceeded: says external replies are paused and internal use is unaffected', async () => {
      const page = await render('banner', { scenario: 'usage-exceeded' });

      expect(page.querySelector('[role="status"]')?.getAttribute('data-state')).toBe('exceeded');
      expect(text(page)).toContain('已超過上限');
      expect(text(page)).toContain('1,036,400 / 1,000,000 tokens（103%）');
      expect(text(page)).toContain('對外回覆已暫停');
      expect(text(page)).toContain('組織內部使用不受影響');
    });

    it('normal: shows nothing', async () => {
      const page = await render('banner', { scenario: 'usage-normal' });

      expect(page.querySelector('.usage')).toBeNull();
      expect(text(page).trim()).toBe('');
    });

    it('an account without manage-publishing sees no banner and the page is not even asked for usage', async () => {
      for (const accountId of ['account-internal-employee', 'account-external-customer'] as const) {
        TestBed.resetTestingModule();
        const page = await render('banner', { accountId });
        expect(page.querySelector('.usage')).toBeNull();
        expect(text(page).trim()).toBe('');
      }
    });

    it('does not use colour alone: the state has a text label and a symbol', async () => {
      const page = await render('banner');

      expect(page.querySelector('.usage__state')?.textContent).toMatch(/!\s+接近上限/);
      expect(page.querySelector('.usage__state [aria-hidden="true"]')?.textContent).toBe('!');
    });
  });

  describe('summary (publishing pages)', () => {
    it.each([
      ['usage-normal', '用量正常', '420,000 / 1,000,000 tokens（42%）', '達到上限時，對外回覆會暫停'],
      [undefined, '接近上限', '864,300 / 1,000,000 tokens（86%）', '達到 100% 時'],
      ['usage-exceeded', '已超過上限', '1,036,400 / 1,000,000 tokens（103%）', '對外回覆已暫停'],
    ] as const)('%s: shows month, used / limit, state and the matching note', async (scenario, label, numbers, note) => {
      TestBed.resetTestingModule();
      const page = await render('summary', { scenario });
      const summary = page.querySelector('section.usage');

      expect(summary?.getAttribute('aria-label')).toBe('本月用量');
      expect(text(page)).toContain('2026-09 本月用量');
      expect(text(page)).toContain(label);
      expect(text(page)).toContain(numbers);
      expect(text(page)).toContain(note);
      expect(text(page)).toContain('組織內部使用不受影響');
      expect(page.querySelector('.usage__bar')?.getAttribute('aria-hidden')).toBe('true');
    });

    it('an account without manage-publishing sees nothing (not an error)', async () => {
      const page = await render('summary', { accountId: 'account-internal-employee' });

      expect(page.querySelector('.usage')).toBeNull();
      expect(page.querySelector('.usage__error')).toBeNull();
    });
  });
});
