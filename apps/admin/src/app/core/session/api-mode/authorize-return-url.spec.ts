import { authorizeReturnUrl } from './authorize-return-url';

const ORIGIN = 'http://localhost:4200';

describe('authorizeReturnUrl', () => {
  it('accepts this site’s authorize request and keeps its query', () => {
    expect(authorizeReturnUrl('/connect/authorize?client_id=admin-spa&state=a%2Fb', ORIGIN)).toBe(
      '/connect/authorize?client_id=admin-spa&state=a%2Fb',
    );
  });

  it.each([
    ['missing', null],
    ['empty', ''],
    ['absolute URL to another site', 'https://evil.example/connect/authorize'],
    ['absolute URL to this site', `${ORIGIN}/connect/authorize`],
    ['protocol-relative URL', '//evil.example/connect/authorize'],
    ['backslash trick', '/\\evil.example/connect/authorize'],
    ['another local path', '/app/home'],
    ['a path that only starts like authorize', '/connect/authorize-evil'],
    ['a nested path', '/connect/authorize/../token'],
    ['javascript URL', 'javascript:alert(1)'],
  ])('rejects %s', (_label, returnUrl) => {
    expect(authorizeReturnUrl(returnUrl, ORIGIN)).toBeNull();
  });
});
