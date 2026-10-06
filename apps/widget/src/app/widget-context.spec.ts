import { parseRetryAfter } from './visitor-api';
import { parseHostOrigin, readWidgetContext } from './widget-context';

describe('readWidgetContext', () => {
  it('reads the assistant id from /use/{id} and the host origin from ?host', () => {
    expect(readWidgetContext({ pathname: '/use/abc-123', search: '?host=https%3A%2F%2Fshop.example.com' })).toEqual({
      assistantId: 'abc-123',
      basePath: '',
      hostOrigin: 'https://shop.example.com',
    });
  });

  it('accepts a trailing slash and an API path prefix', () => {
    expect(readWidgetContext({ pathname: '/ai/use/abc/', search: '' })).toEqual({ assistantId: 'abc', basePath: '/ai', hostOrigin: null });
  });

  it('decodes the id', () => {
    expect(readWidgetContext({ pathname: '/use/a%20b', search: '' }).assistantId).toBe('a b');
    expect(readWidgetContext({ pathname: '/use/%E0%A4%A', search: '' }).assistantId).toBeNull();
  });

  it.each(['/', '/widget/index.html', '/use', '/use/a/b'])('has no assistant id for %s', (pathname) => {
    expect(readWidgetContext({ pathname, search: '' }).assistantId).toBeNull();
  });
});

describe('parseHostOrigin', () => {
  it.each([
    ['https://shop.example.com', 'https://shop.example.com'],
    ['http://localhost:4200', 'http://localhost:4200'],
  ])('accepts the origin %s', (value, expected) => expect(parseHostOrigin(value)).toBe(expected));

  it.each([null, '', 'null', 'shop.example.com', 'https://shop.example.com/', 'https://shop.example.com/path', 'https://shop.example.com?x=1', 'javascript:alert(1)', 'file:///etc/passwd', 'ftp://x.example', '*'])(
    'rejects %s',
    (value) => expect(parseHostOrigin(value)).toBeNull(),
  );
});

describe('parseRetryAfter', () => {
  const now = Date.parse('2026-10-06T02:00:00Z');

  it('reads seconds', () => expect(parseRetryAfter('7', now)).toBe(7));
  it('reads an HTTP date', () => expect(parseRetryAfter('Tue, 06 Oct 2026 02:01:00 GMT', now)).toBe(60));
  it('rounds up and keeps it at least one second', () => {
    expect(parseRetryAfter('0.2', now)).toBe(1);
    expect(parseRetryAfter('0', now)).toBe(1);
    expect(parseRetryAfter('-5', now)).toBe(1);
  });
  it('caps it at one hour', () => expect(parseRetryAfter('999999', now)).toBe(3600));
  it.each([null, '', 'soon'])('falls back to 30 seconds for %s', (value) => expect(parseRetryAfter(value, now)).toBe(30));
});
