export function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}

export function addDays(d: Date, n: number): Date {
  const r = new Date(d);
  r.setDate(r.getDate() + n);
  return r;
}

export function diffDays(a: Date, b: Date): number {
  return Math.floor((startOfDay(a).getTime() - startOfDay(b).getTime()) / 86_400_000);
}

export function isSameDay(a: Date, b: Date): boolean {
  return startOfDay(a).getTime() === startOfDay(b).getTime();
}

export function isoAt(daysFromToday: number, hour: number): string {
  const d = addDays(startOfDay(new Date()), daysFromToday);
  d.setHours(hour);
  return d.toISOString();
}

const pad = (n: number) => String(n).padStart(2, '0');

export function fmtDateTime(iso: string): string {
  const d = new Date(iso);
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function fmtDate(d: Date): string {
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())}`;
}
