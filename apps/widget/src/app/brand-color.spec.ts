import { BRAND_COLORS, DEFAULT_BRAND_COLOR, applyBrandColor, resolveBrandColor } from './brand-color';
import { FakeServer, answer, companyReply, mountReady, session } from './widget.testing';

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((start) => {
    const value = parseInt(hex.slice(start, start + 2), 16) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(foreground: string, background: string): number {
  const [light, dark] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (light + 0.05) / (dark + 0.05);
}

describe('brand color', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => {
    document.documentElement.style.removeProperty('--color-accent');
    delete document.documentElement.dataset['brand'];
    document.body.replaceChildren();
  });

  it('maps the wire values to the same colors as the admin publishing settings', () => {
    expect(BRAND_COLORS).toEqual({ forest: '#1f6f5c', ocean: '#1d5fa8', amber: '#a3520c', plum: '#6b3fa0' });
  });

  it.each(['forest', 'ocean', 'amber', 'plum'] as const)('%s: sets --color-accent and data-brand on the root', (id) => {
    expect(applyBrandColor(document.documentElement, id)).toBe(id);
    expect(document.documentElement.style.getPropertyValue('--color-accent')).toBe(BRAND_COLORS[id]);
    expect(document.documentElement.dataset['brand']).toBe(id);
  });

  it.each([undefined, null, '', 'neon', '#ff0000', '__proto__', 'toString'])('falls back to the default for %s', (value) => {
    expect(resolveBrandColor(value)).toBe(DEFAULT_BRAND_COLOR);
  });

  it.each(Object.entries(BRAND_COLORS))('%s: white text on the brand color and the brand color on white both reach 4.5:1', (_id, hex) => {
    expect(contrast('#ffffff', hex)).toBeGreaterThanOrEqual(4.5);
  });

  it.each(Object.entries(BRAND_COLORS))('%s: the brand color on the soft background (10%% mix with white) reaches 4.5:1', (_id, hex) => {
    const mix = (channel: number) => Math.round(channel * 0.1 + 255 * 0.9);
    const soft = `#${[1, 3, 5].map((start) => mix(parseInt(hex.slice(start, start + 2), 16)).toString(16).padStart(2, '0')).join('')}`;
    expect(contrast(hex, soft)).toBeGreaterThanOrEqual(4.5);
  });

  it('the window applies the brand color from the session response', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session({}, { brandColor: 'plum' }));
    fake.runs.push(answer(companyReply));
    await mountReady(fake);

    expect(document.documentElement.style.getPropertyValue('--color-accent')).toBe('#6b3fa0');
    expect(document.documentElement.dataset['brand']).toBe('plum');
  });

  it('the window falls back to forest for a color it does not know', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session({}, { brandColor: 'chartreuse' }));
    await mountReady(fake);

    expect(document.documentElement.style.getPropertyValue('--color-accent')).toBe('#1f6f5c');
  });
});
