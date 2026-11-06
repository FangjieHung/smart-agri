import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { API_PREFIX, OrderingApi, TABLE_HEADER, TOKEN_HEADER } from './ordering-api';

const TOKEN = 'kq3Vd8u6m0yJxT2bA9cR4eLw7sZpN1fH5gUiOoE_-Qa';

function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

describe('OrderingApi', () => {
  function setup(response: () => Promise<Response>) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const api = TestBed.inject(OrderingApi);
    const calls: { url: string; init: RequestInit }[] = [];
    api.fetcher = (url, init) => {
      calls.push({ url, init });
      return response();
    };
    return { api, calls };
  }

  it('sends the status token only in the X-Ordering-Token header, never in the URL', async () => {
    const { api, calls } = setup(async () => json(200, { number: 1 }));
    await api.order('wangji-noodles', TOKEN);
    await api.cancel('wangji-noodles', TOKEN);
    expect(calls.map((call) => call.url)).toEqual([
      `${API_PREFIX}wangji-noodles/orders/current`,
      `${API_PREFIX}wangji-noodles/orders/current/cancel`,
    ]);
    for (const call of calls) {
      expect(call.url).not.toContain(TOKEN);
      expect((call.init.headers as Record<string, string>)[TOKEN_HEADER]).toBe(TOKEN);
      expect(call.init.referrerPolicy).toBe('no-referrer');
      expect(call.init.body ?? '').not.toContain(TOKEN);
    }
    expect(calls[1].init.method).toBe('POST');
  });

  it('sends the table token (#402) only in the X-Ordering-Table header, never in the URL', async () => {
    const TABLE = 'Tb1e_Tok3n-0123456789abcdefghijklmnopqrstuv';
    const { api, calls } = setup(async () => json(200, { name: 'A1' }));
    await api.table('wangji-noodles', TABLE);
    await api.submit('wangji-noodles', { serviceType: 'dine-in' } as never, TABLE);
    await api.submit('wangji-noodles', { serviceType: 'takeout-asap' } as never);
    expect(calls.map((call) => call.url)).toEqual([
      `${API_PREFIX}wangji-noodles/table`,
      `${API_PREFIX}wangji-noodles/orders`,
      `${API_PREFIX}wangji-noodles/orders`,
    ]);
    const headers = calls.map((call) => call.init.headers as Record<string, string>);
    expect(headers[0][TABLE_HEADER]).toBe(TABLE);
    expect(headers[1][TABLE_HEADER]).toBe(TABLE);
    expect(headers[2][TABLE_HEADER]).toBeUndefined();
    for (const call of calls) {
      expect(call.url).not.toContain(TABLE);
      expect(call.init.body ?? '').not.toContain(TABLE);
    }
  });

  it('turns problem+json into a problem with its reason', async () => {
    const { api } = setup(async () =>
      json(
        409,
        { status: 409, reason: 'menu-changed', message: '菜單剛剛更新了', menuVersion: 2 },
        { 'Content-Type': 'application/problem+json' },
      ),
    );
    const result = await api.menu('wangji-noodles');
    expect(result.ok).toBe(false);
    expect('problem' in result && result.problem).toMatchObject({
      reason: 'menu-changed',
      menuVersion: 2,
    });
  });

  it('reads the five-field rate-limit answer and its Retry-After', async () => {
    const { api } = setup(async () =>
      json(
        429,
        {
          type: 't',
          title: 'Too Many Requests',
          status: 429,
          reason: 'rate-limited',
          message: 'x',
        },
        { 'Retry-After': '42' },
      ),
    );
    const result = await api.shop('wangji-noodles');
    expect('problem' in result && result.problem.reason).toBe('rate-limited');
    expect('retryAfter' in result && result.retryAfter).toBe(42);
  });

  it('an answer without a reason (the framework 400) is unexpected', async () => {
    const { api } = setup(async () => json(400, { title: 'Bad Request' }));
    const result = await api.slots('wangji-noodles');
    expect('problem' in result && result.problem).toEqual({
      status: 400,
      reason: 'unexpected',
      message: '',
    });
  });

  it('reports a network failure', async () => {
    const { api } = setup(() => Promise.reject(new TypeError('Failed to fetch')));
    expect(await api.submit('wangji-noodles', {} as never)).toEqual({
      ok: false,
      status: 0,
      network: true,
    });
  });

  it('encodes the slug in the path', async () => {
    const { api, calls } = setup(async () => json(200, {}));
    await api.shop('a/b');
    expect(calls[0].url).toBe(`${API_PREFIX}a%2Fb`);
  });
});
