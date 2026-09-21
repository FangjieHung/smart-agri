import { describe, it, expect } from 'vitest';
import { STATUS_TONE, toneOf, type Tone } from './status-tone';

const VALID_TONES: Tone[] = ['positive', 'info', 'neutral', 'warning', 'danger'];

describe('status-tone', () => {
  it('每個狀態都對到合法 tone', () => {
    for (const tone of Object.values(STATUS_TONE)) {
      expect(VALID_TONES).toContain(tone);
    }
  });

  it('toneOf 依 spec §5 對應', () => {
    expect(toneOf('approved')).toBe('positive');
    expect(toneOf('processing')).toBe('info');
    expect(toneOf('archived')).toBe('neutral');
    expect(toneOf('warning')).toBe('warning');
    expect(toneOf('cancelled')).toBe('danger');
  });

  it('涵蓋 spec §5 全部狀態 key', () => {
    expect(Object.keys(STATUS_TONE)).toHaveLength(21);
  });
});
