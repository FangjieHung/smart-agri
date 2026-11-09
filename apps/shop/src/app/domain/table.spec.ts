import { parseRoute } from '../state/nav';
import { saveTableToken, storedTableToken, tableTokenFromHash } from './table';

const TABLE = 'Tb1e_Tok3n-0123456789abcdefghijklmnopqrstuv';

describe('dine-in table token (#402)', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
  });

  it('the QR code URL /order/{slug}/table is its own route', () => {
    expect(TABLE).toHaveLength(43);
    expect(parseRoute('/order/wangji-noodles/table')).toEqual({
      slug: 'wangji-noodles',
      page: 'table',
    });
    expect(parseRoute('/order/WangJi-Noodles/TABLE/')).toEqual({
      slug: 'wangji-noodles',
      page: 'table',
    });
    expect(parseRoute('/order/wangji-noodles/tables').page).toBe('not-found');
  });

  it('reads only a 43-character base64url token from the fragment', () => {
    expect(tableTokenFromHash(`#${TABLE}`)).toBe(TABLE);
    expect(tableTokenFromHash(TABLE)).toBe(TABLE);
    expect(tableTokenFromHash(`#${encodeURIComponent(TABLE)} `)).toBe(TABLE);
    expect(tableTokenFromHash('')).toBeNull();
    expect(tableTokenFromHash('#')).toBeNull();
    expect(tableTokenFromHash(`#${TABLE.slice(1)}`)).toBeNull();
    expect(tableTokenFromHash(`#${TABLE}x`)).toBeNull();
    expect(tableTokenFromHash(`#${TABLE.slice(1)}=`)).toBeNull();
    expect(tableTokenFromHash('#%E0%A4%A')).toBeNull();
  });

  it('keeps the token in sessionStorage per shop (never localStorage) and forgets it', () => {
    saveTableToken('wangji-noodles', TABLE);
    expect(storedTableToken('wangji-noodles')).toBe(TABLE);
    expect(storedTableToken('other-shop')).toBeNull();
    expect(JSON.stringify({ ...localStorage })).not.toContain(TABLE);
    saveTableToken('wangji-noodles', null);
    expect(storedTableToken('wangji-noodles')).toBeNull();
    sessionStorage.setItem('smart-agri-shop:table:wangji-noodles', 'not-a-token');
    expect(storedTableToken('wangji-noodles')).toBeNull();
  });
});
