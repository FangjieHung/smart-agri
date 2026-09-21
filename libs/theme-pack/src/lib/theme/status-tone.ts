export const STATUS_TONE = {
  success: 'positive',
  approved: 'positive',
  completed: 'positive',
  active: 'positive',
  online: 'positive',
  info: 'info',
  processing: 'info',
  loading: 'info',
  queued: 'info',
  pending: 'info',
  draft: 'neutral',
  inactive: 'neutral',
  archived: 'neutral',
  offline: 'neutral',
  empty: 'neutral',
  noResult: 'neutral',
  warning: 'warning',
  error: 'danger',
  critical: 'danger',
  rejected: 'danger',
  cancelled: 'danger',
} as const;

export type StatusKey = keyof typeof STATUS_TONE;
export type Tone = (typeof STATUS_TONE)[StatusKey];

export function toneOf(key: StatusKey): Tone {
  return STATUS_TONE[key];
}
