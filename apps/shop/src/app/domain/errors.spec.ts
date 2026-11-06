import type { ApiResult } from '../api/ordering-api';
import type { Problem } from '../api/types';
import { describeSubmitFailure, lineProblemText } from './errors';

type Failure = Exclude<ApiResult<unknown>, { ok: true }>;

function refused(problem: Problem, retryAfter: number | null = null): Failure {
  return { ok: false, status: problem.status, problem, retryAfter };
}

function full(status: number, reason: string, extra: Partial<Problem> = {}): Problem {
  return {
    type: 'x',
    title: 'x',
    status,
    reason,
    message: '伺服器的說明。',
    errors: null,
    lineErrors: null,
    menuVersion: null,
    orderNumber: null,
    orderStatus: null,
    ...extra,
  };
}

describe('submit errors → messages and next steps', () => {
  const phone = '06-123-4567';

  it.each([
    ['menu-changed', 'refresh-menu', '菜單剛剛更新了'],
    ['slot-full', 'pick-time', '額滿'],
    ['slot-unavailable', 'pick-time', '重新選擇'],
    ['service-unavailable', 'pick-time', '伺服器的說明'],
    ['shop-paused', 'stop', '暫停接受線上點餐'],
    ['too-many-pending', 'stop', '伺服器的說明'],
    ['shop-not-found', 'stop', '找不到這家店'],
    ['something-new', 'retry', '再試一次'],
  ])('%s → %s', (reason, recovery, text) => {
    const failure = describeSubmitFailure(refused(full(409, reason)), phone);
    expect(failure.recovery).toBe(recovery);
    expect(failure.message).toContain(text);
  });

  it('shop-paused names the phone to call', () => {
    expect(describeSubmitFailure(refused(full(409, 'shop-paused')), phone).message).toContain(
      phone,
    );
  });

  it('items-unavailable marks the lines', () => {
    const failure = describeSubmitFailure(
      refused(
        full(409, 'items-unavailable', {
          lineErrors: [{ line: 1, code: 'sold-out-today', path: '' }],
        }),
      ),
      phone,
    );
    expect(failure.recovery).toBe('mark-lines');
    expect(failure.lines).toEqual([{ line: 1, code: 'sold-out-today' }]);
    expect(lineProblemText('sold-out-today')).toContain('售完');
    expect(lineProblemText('category-unavailable')).toContain('不供應');
    expect(lineProblemText('too-few')).toContain('選項');
  });

  it('invalid-order with field errors puts them next to the fields', () => {
    const failure = describeSubmitFailure(
      refused(
        full(422, 'invalid-order', {
          errors: { 'customer.phone': ['請填寫台灣的手機或市話號碼。'] },
        }),
      ),
      phone,
    );
    expect(failure.recovery).toBe('fix-fields');
    expect(failure.fields).toEqual({ 'customer.phone': '請填寫台灣的手機或市話號碼。' });
  });

  it('invalid-order with line errors sends the customer to the cart', () => {
    const failure = describeSubmitFailure(
      refused(
        full(422, 'invalid-order', { lineErrors: [{ line: 0, code: 'too-few', path: 'noodle' }] }),
      ),
      phone,
    );
    expect(failure.recovery).toBe('mark-lines');
    expect(failure.lines).toEqual([{ line: 0, code: 'too-few' }]);
  });

  it('duplicate-submission shows the existing order number', () => {
    const failure = describeSubmitFailure(
      refused(full(409, 'duplicate-submission', { orderNumber: '#003' })),
      phone,
    );
    expect(failure.recovery).toBe('show-existing');
    expect(failure.orderNumber).toBe('#003');
    expect(failure.message).toContain('#003');
  });

  it('rate-limited has only the five basic fields and says when to retry', () => {
    const problem: Problem = { status: 429, reason: 'rate-limited', message: 'Too many requests.' };
    const failure = describeSubmitFailure(refused(problem, 30), phone);
    expect(failure.recovery).toBe('retry');
    expect(failure.message).toContain('30 秒');
    expect(failure.lines).toEqual([]);
  });

  it('a network failure can be retried with the same submission key', () => {
    const failure = describeSubmitFailure({ ok: false, status: 0, network: true }, phone);
    expect(failure.recovery).toBe('retry');
    expect(failure.message).toContain('不會重複下單');
  });
});
