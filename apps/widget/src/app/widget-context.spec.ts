import { parseRetryAfter } from './visitor-api';
import { TestBed } from '@angular/core/testing';
import { WIDGET_PARENT, parentMessenger, parseHostOrigin, readWidgetContext } from './widget-context';

/** A cross-origin parent window: only postMessage is reachable, any other property throws. */
function crossOriginParent(postMessage: (message: unknown, targetOrigin: string) => void): Window {
  return new Proxy({} as Window, {
    get: (_target, property) => {
      if (property === 'postMessage') return postMessage;
      throw new DOMException(`Blocked a frame from accessing a cross-origin frame (${String(property)}).`, 'SecurityError');
    },
  });
}

describe('parentMessenger', () => {
  it('is null when the widget is not in an iframe', () => {
    const self = {} as Window & { parent: Window };
    self.parent = self;
    expect(parentMessenger(self)).toBeNull();
  });

  it('never exposes the cross-origin parent window, so Angular can inject it (#205)', () => {
    const sent: unknown[] = [];
    const parent = crossOriginParent((message, targetOrigin) => sent.push([message, targetOrigin]));
    const messenger = parentMessenger({ parent });

    // Compared with === on purpose: a matcher would format (and so read) the cross-origin window.
    expect(messenger === parent).toBe(false);
    expect(() => (messenger as unknown as Record<string, unknown>)['ngOnDestroy']).not.toThrow();
    messenger?.postMessage({ type: 'smartagri:close' }, 'https://shop.example.com');
    expect(sent).toEqual([[{ type: 'smartagri:close' }, 'https://shop.example.com']]);
  });

  it('injects through Angular DI when the parent is cross-origin', () => {
    const parent = crossOriginParent(() => undefined);
    TestBed.configureTestingModule({
      providers: [{ provide: WIDGET_PARENT, useFactory: () => parentMessenger({ parent }) }],
    });
    expect(() => TestBed.inject(WIDGET_PARENT)).not.toThrow();
  });

  it('a raw cross-origin window would break injection (the bug this guards against)', () => {
    const parent = crossOriginParent(() => undefined);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: WIDGET_PARENT, useFactory: () => parent }] });
    expect(() => TestBed.inject(WIDGET_PARENT)).toThrow();
  });
});

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
