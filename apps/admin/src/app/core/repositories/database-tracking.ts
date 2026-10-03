import {
  PERIODIC_REPORT_LABELS,
  type PeriodicReportSchedule,
} from '../domain/assistant-draft.model';
import {
  DATABASE_FIELD_TYPES,
  isChoiceFieldType,
  DATABASE_PERIOD_OPTIONS,
  type ComparisonPointView,
  type DatabaseFieldError,
  type DatabaseFieldId,
  type DatabaseFieldSumView,
  type DatabasePeriodName,
  type DatabasePeriodRangeView,
  type DatabasePeriodSummaryView,
  type DatabaseFieldView,
  type DatabaseRecordEntryView,
  type DatabaseRecordValue,
  type DatabaseTrialAnswer,
  type DatabaseRecordView,
  type DatabaseTrialAnswers,
  type MetricComparisonView,
  type PeriodicReportView,
  type SubjectComparisonView,
  type TrackedSubjectId,
  type TrackedSubjectView,
  type WithdrawnRecordView,
} from '../domain/database.model';
import type { DatabaseRecordFixture } from './demo-seed-databases';

/*
 * Mock 的業務計算集中在這裡：欄位驗證、試填驗證、紀錄格式化與本次／上次／首次比較。
 * 畫面元件只顯示這些結果，不自行推導差異。
 */

const numberFormat = new Intl.NumberFormat('en-US');

/**
 * 統計與日期標籤使用的時區（IANA）。與後端 `Statistics:TimeZone` 的預設相同（`Asia/Taipei`）：紀錄屬於
 * 它在這個時區落在的曆日，所以台灣 08:00 前送出的資料算當天，不是 UTC 的前一天。後端的設定若改了，
 * 這個常數要一起改（兩邊的規則見 M4 設計文件 §10.3）。
 */
export const STATISTICS_TIME_ZONE = 'Asia/Taipei';

const dayFormat = new Intl.DateTimeFormat('en-CA', {
  timeZone: STATISTICS_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** 一個時間點在統計時區的曆日（`YYYY-MM-DD`）；與回執、時間軸顯示的日期同一天。 */
export function statisticsDay(iso: string): string {
  return dayFormat.format(new Date(iso));
}

function formatNumber(value: number, unit: string): string {
  const text = numberFormat.format(value);
  return unit ? `${text} ${unit}` : text;
}

function formatSigned(value: number, unit: string): string {
  if (value === 0) return '持平';
  const sign = value > 0 ? '+' : '-';
  return `${sign}${formatNumber(Math.abs(value), unit)}`;
}

export function displayRecordValue(value: DatabaseRecordValue): string {
  switch (value.type) {
    case 'number':
      return formatNumber(value.value, value.unit);
    case 'scale':
      return `${value.value} / ${value.max}`;
    case 'multiple-choice':
      return value.value.length > 0 ? value.value.join('、') : '未填寫';
    default:
      return value.value.trim() ? value.value : '未填寫';
  }
}

export function toRecordView(record: DatabaseRecordFixture): DatabaseRecordView {
  return {
    id: record.id,
    recordedAt: record.recordedAt,
    dateLabel: statisticsDay(record.recordedAt),
    source: record.source,
    entries: record.values.map((value) => ({
      fieldId: value.fieldId,
      label: value.label,
      display: displayRecordValue(value),
    })),
  };
}

/**
 * 已撤回同意的紀錄轉成軌跡：只保留提交與撤回的時間與來源。
 * 撤回時 `values` 已在 mock 中清空，這裡也不讀取任何欄位值。
 */
export function toWithdrawnRecordView(record: DatabaseRecordFixture): WithdrawnRecordView {
  return {
    id: record.id,
    submittedAt: record.recordedAt,
    submittedDateLabel: statisticsDay(record.recordedAt),
    withdrawnDateLabel: record.withdrawnAt === undefined ? '' : statisticsDay(record.withdrawnAt),
    source: record.source,
  };
}

/** 依時間先後排列的已同意紀錄，建立本次／上次／首次比較。 */
export function compareRecords(chronological: readonly DatabaseRecordFixture[]): SubjectComparisonView {
  const recordCount = chronological.length;
  if (recordCount < 2) {
    return {
      status: 'insufficient-records',
      recordCount,
      message: `目前只有 ${recordCount} 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。`,
    };
  }

  const latest = chronological[recordCount - 1];
  const metrics = latest.values.flatMap((value): MetricComparisonView[] => {
    if (value.type !== 'number' && value.type !== 'scale') return [];
    // 同一個欄位編號但型別或單位改過的紀錄不混進同一條趨勢（與後端 `DatabaseQueryResults.Compare` 相同）。
    const sameMeasure = (candidate: DatabaseRecordValue): boolean =>
      candidate.fieldId === value.fieldId &&
      candidate.type === value.type &&
      (candidate.type !== 'number' || (value.type === 'number' && candidate.unit === value.unit));
    const points = chronological.flatMap((record): ComparisonPointView[] => {
      const match = record.values.find(sameMeasure);
      if (match === undefined || (match.type !== 'number' && match.type !== 'scale')) return [];
      return [
        {
          recordId: record.id,
          dateLabel: statisticsDay(record.recordedAt),
          value: match.value,
          display: displayRecordValue(match),
        },
      ];
    });
    if (points.length < 2) return [];

    const first = points[0];
    const previous = points[points.length - 2];
    const current = points[points.length - 1];
    const unit = value.type === 'number' ? value.unit : '分';
    const changeFromPrevious = current.value - previous.value;
    const changeFromFirst = current.value - first.value;
    const values = points.map((point) => point.value);
    const changeFromPreviousLabel = formatSigned(changeFromPrevious, unit);
    const changeFromFirstLabel = formatSigned(changeFromFirst, unit);

    return [
      {
        fieldId: value.fieldId,
        label: value.label,
        first,
        previous,
        current,
        changeFromPrevious,
        changeFromFirst,
        changeFromPreviousLabel,
        changeFromFirstLabel,
        direction: changeFromPrevious > 0 ? 'up' : changeFromPrevious < 0 ? 'down' : 'flat',
        points,
        axis:
          value.type === 'scale'
            ? { min: value.min, max: value.max }
            : { min: Math.min(...values), max: Math.max(...values) },
        summary: `${value.label}：本次 ${current.display}，較上次 ${changeFromPreviousLabel}，較首次 ${changeFromFirstLabel}。`,
      },
    ];
  });

  if (metrics.length === 0) {
    return {
      status: 'insufficient-records',
      recordCount,
      message: `目前有 ${recordCount} 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。`,
    };
  }

  return {
    status: 'available',
    recordCount,
    summary: `比較 ${recordCount} 筆已同意提交的紀錄（${statisticsDay(chronological[0].recordedAt)} 至 ${statisticsDay(latest.recordedAt)}）。`,
    metrics,
  };
}

const DAY_MS = 86_400_000;

function dayNumber(label: string): number {
  const [year, month, day] = label.split('-').map(Number);
  return Date.UTC(year, month - 1, day) / DAY_MS;
}

function dayLabel(number: number): string {
  return new Date(number * DAY_MS).toISOString().slice(0, 10);
}

function monthStart(label: string, offsetMonths: number): string {
  const [year, month] = label.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1 + offsetMonths, 1)).toISOString().slice(0, 10);
}

function periodNameLabel(name: DatabasePeriodName): string {
  return DATABASE_PERIOD_OPTIONS.find((option) => option.id === name)?.label ?? name;
}

function rangeOf(name: DatabasePeriodName | null, from: number, to: number): DatabasePeriodRangeView {
  const range = `${dayLabel(from)} 至 ${dayLabel(to)}`;
  return {
    name,
    from: dayLabel(from),
    to: dayLabel(to),
    label: name === null ? range : `${periodNameLabel(name)}（${range}）`,
  };
}

/**
 * 具名期間涵蓋的 統計時區（`STATISTICS_TIME_ZONE`）的曆日（起訖都含）：週從週一到週日、月從 1 日到月底，`last-7-days`／`last-30-days`
 * 到今天為止。與後端 `DatabaseFixedQueries.Resolve` 相同；`today` 是 `YYYY-MM-DD`（統計時區）。
 */
export function resolvePeriod(name: DatabasePeriodName, today: string): DatabasePeriodRangeView {
  const todayNumber = dayNumber(today);
  const monday = todayNumber - ((new Date(todayNumber * DAY_MS).getUTCDay() + 6) % 7);
  switch (name) {
    case 'this-week':
      return rangeOf(name, monday, monday + 6);
    case 'last-week':
      return rangeOf(name, monday - 7, monday - 1);
    case 'this-month':
      return rangeOf(name, dayNumber(monthStart(today, 0)), dayNumber(monthStart(today, 1)) - 1);
    case 'last-month':
      return rangeOf(name, dayNumber(monthStart(today, -1)), dayNumber(monthStart(today, 0)) - 1);
    case 'last-7-days':
      return rangeOf(name, todayNumber - 6, todayNumber);
    case 'last-30-days':
      return rangeOf(name, todayNumber - 29, todayNumber);
  }
}

/** 前一個完整期間：月的前一個曆月，其餘為同樣天數接在前面（與後端 `Previous` 相同）。 */
export function previousPeriod(period: DatabasePeriodRangeView): DatabasePeriodRangeView {
  const from = dayNumber(period.from);
  if (period.name === 'this-month' || period.name === 'last-month') {
    return rangeOf(null, dayNumber(monthStart(period.from, -1)), from - 1);
  }
  const days = dayNumber(period.to) - from + 1;
  return rangeOf(null, from - days, from - 1);
}

interface PeriodRows {
  readonly count: number;
  readonly sums: ReadonlyMap<DatabaseFieldId, { readonly sum: number; readonly count: number }>;
}

function rowsOf(
  records: readonly DatabaseRecordFixture[],
  period: DatabasePeriodRangeView,
  units: ReadonlyMap<DatabaseFieldId, string>,
): PeriodRows {
  const sums = new Map<DatabaseFieldId, { sum: number; count: number }>();
  let count = 0;
  for (const record of records) {
    const day = statisticsDay(record.recordedAt);
    if (day < period.from || day > period.to) continue;
    count += 1;
    for (const value of record.values) {
      // 只加總現行單位的數字（舊單位的值不併入新單位的總和）。
      if (value.type !== 'number' || units.get(value.fieldId) !== value.unit) continue;
      const entry = sums.get(value.fieldId) ?? { sum: 0, count: 0 };
      sums.set(value.fieldId, { sum: entry.sum + value.value, count: entry.count + 1 });
    }
  }
  return { count, sums };
}

/**
 * 期間統計（固定查詢 `period-summary` 的 mock 版）：這一期與前一期的有效紀錄筆數，以及每個數字欄位
 * 的加總。`records` 只放有效（已同意、未撤回）的紀錄；`fields` 是目前表單，現行欄位即使沒有值也列出，
 * 已從表單移除、但有值的欄位才跟著列出（與後端 `DatabaseQueryResults.Sums` 相同）。
 */
export function summarizePeriod(input: {
  readonly records: readonly DatabaseRecordFixture[];
  readonly fields: readonly DatabaseFieldView[];
  readonly subjectId: TrackedSubjectId | null;
  readonly period: DatabasePeriodName;
  readonly today: string;
}): DatabasePeriodSummaryView {
  const period = resolvePeriod(input.period, input.today);
  const previous = previousPeriod(period);
  const records = input.records.filter((record) => input.subjectId === null || record.subjectId === input.subjectId);

  const references = new Map<DatabaseFieldId, { label: string; unit: string; current: boolean }>();
  for (const field of input.fields) {
    if (field.type === 'number') references.set(field.id, { label: field.label, unit: field.unit, current: true });
  }
  for (const record of [...records].reverse()) {
    for (const value of record.values) {
      if (value.type === 'number' && !references.has(value.fieldId)) {
        references.set(value.fieldId, { label: value.label, unit: value.unit, current: false });
      }
    }
  }

  const units = new Map([...references].map(([id, reference]) => [id, reference.unit]));
  const now = rowsOf(records, period, units);
  const before = rowsOf(records, previous, units);
  const sums = [...references].flatMap(([id, reference]): DatabaseFieldSumView[] => {
    const current = now.sums.get(id) ?? { sum: 0, count: 0 };
    const earlier = before.sums.get(id) ?? { sum: 0, count: 0 };
    if (!reference.current && current.count === 0 && earlier.count === 0) return [];
    const change = current.sum - earlier.sum;
    return [
      {
        fieldId: id,
        label: reference.label,
        unit: reference.unit,
        sum: current.sum,
        display: formatNumber(current.sum, reference.unit),
        recordCount: current.count,
        previousSum: earlier.sum,
        previousDisplay: formatNumber(earlier.sum, reference.unit),
        previousRecordCount: earlier.count,
        change,
        changeLabel: formatSigned(change, reference.unit),
      },
    ];
  });

  const countChange = now.count - before.count;
  return {
    period,
    previousPeriod: previous,
    subjectId: input.subjectId,
    recordCount: now.count,
    previousRecordCount: before.count,
    recordCountChange: countChange,
    recordCountChangeLabel: formatSigned(countChange, '筆'),
    sums,
  };
}

/** 定期回報的下一次日期：每週加 7 天，每月加 1 個月（下個月沒有這一天時取月底）。 */
export function nextReportDate(
  anchorDateLabel: string,
  schedule: Exclude<PeriodicReportSchedule, 'off'>,
): string {
  const [year, month, day] = anchorDateLabel.split('-').map(Number);
  if (schedule === 'weekly') {
    return new Date(Date.UTC(year, month - 1, day + 7)).toISOString().slice(0, 10);
  }

  const lastDayOfNextMonth = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();
  return new Date(Date.UTC(year, month, Math.min(day, lastDayOfNextMonth)))
    .toISOString()
    .slice(0, 10);
}

/**
 * 定期回報面板：排程由最近一次紀錄推算，摘要則整段沿用 `compareRecords` 已經算好的
 * `MetricComparisonView.summary`——這裡不重新計算任何數字，也不產生新的結論。
 */
export function buildPeriodicReport(input: {
  readonly assistantName: string;
  readonly schedule: Exclude<PeriodicReportSchedule, 'off'>;
  readonly purpose: string;
  readonly anchorLabel: string;
  readonly subjects: readonly TrackedSubjectView[];
}): PeriodicReportView {
  return {
    assistantName: input.assistantName,
    scheduleLabel: PERIODIC_REPORT_LABELS[input.schedule],
    anchorLabel: input.anchorLabel,
    nextReportLabel: nextReportDate(input.anchorLabel, input.schedule),
    purpose: input.purpose,
    lines: input.subjects.flatMap((subject) =>
      subject.comparison.status === 'available'
        ? subject.comparison.metrics.map((metric) => `${subject.displayName} · ${metric.summary}`)
        : [],
    ),
    note: '摘要直接引用「趨勢比較」已算好的差異值，助理不會重新計算數字。',
  };
}

/** 與後端 `DatabaseFormField`／`DatabaseFormVersion` 的上限相同（mock 與 API 的驗證一致）。 */
export const DATABASE_FORM_LIMITS = {
  maxFields: 50,
  labelMaxLength: 100,
  maxOptions: 30,
  optionMaxLength: 100,
  unitMaxLength: 20,
  scaleLabelMaxLength: 20,
} as const;

/** 修正空白與不屬於該類型的設定，讓儲存的欄位結構一致（與後端 `DatabaseFormRules` 相同）。 */
export function normalizeField(field: DatabaseFieldView): DatabaseFieldView {
  const scale = field.scale ?? { min: 1, max: 5, minLabel: '', maxLabel: '' };
  return {
    id: field.id,
    label: field.label.trim(),
    type: field.type,
    required: field.required === true,
    options: isChoiceFieldType(field.type)
      ? field.options.map((option) => option.trim()).filter((option) => option.length > 0)
      : [],
    scale:
      field.type === 'scale'
        ? { ...scale, minLabel: scale.minLabel.trim(), maxLabel: scale.maxLabel.trim() }
        : null,
    unit: field.type === 'number' ? field.unit.trim() : '',
  };
}

/** 每個欄位只回報第一個錯誤，訊息與後端 `DatabaseFormRules` 逐字相同。 */
export function validateFields(fields: readonly DatabaseFieldView[]): DatabaseFieldError[] {
  if (fields.length === 0) return [{ fieldId: null, message: '表單至少需要一個欄位。' }];
  if (fields.length > DATABASE_FORM_LIMITS.maxFields) {
    return [{ fieldId: null, message: `表單最多 ${DATABASE_FORM_LIMITS.maxFields} 個欄位。` }];
  }

  const seen = new Set<string>();
  return fields.flatMap((field): DatabaseFieldError[] => {
    const fail = (message: string): DatabaseFieldError[] => [{ fieldId: field.id, message }];
    if (!DATABASE_FIELD_TYPES.includes(field.type)) return fail('不支援的欄位類型。');
    if (field.label.length === 0) return fail('請填寫欄位名稱。');
    if (field.label.length > DATABASE_FORM_LIMITS.labelMaxLength) {
      return fail(`欄位名稱請在 ${DATABASE_FORM_LIMITS.labelMaxLength} 個字以內。`);
    }
    if (seen.has(field.label)) return fail('欄位名稱不可重複。');
    seen.add(field.label);
    if (isChoiceFieldType(field.type)) {
      if (field.options.length < 2) return fail('單選或多選至少需要 2 個選項。');
      if (field.options.length > DATABASE_FORM_LIMITS.maxOptions) {
        return fail(`單選或多選最多 ${DATABASE_FORM_LIMITS.maxOptions} 個選項。`);
      }
      if (field.options.some((option) => option.length > DATABASE_FORM_LIMITS.optionMaxLength)) {
        return fail(`每個選項請在 ${DATABASE_FORM_LIMITS.optionMaxLength} 個字以內。`);
      }
      if (new Set(field.options).size !== field.options.length) return fail('選項不可重複。');
    }
    const scale = field.scale;
    if (field.type === 'scale') {
      if (
        scale === null ||
        !Number.isInteger(scale.min) ||
        !Number.isInteger(scale.max) ||
        scale.min >= scale.max
      ) {
        return fail('量尺的最小值必須小於最大值。');
      }
      if (scale.max - scale.min > 10) return fail('量尺最多 11 個刻度。');
      if (
        scale.minLabel.length > DATABASE_FORM_LIMITS.scaleLabelMaxLength ||
        scale.maxLabel.length > DATABASE_FORM_LIMITS.scaleLabelMaxLength
      ) {
        return fail(`量尺的說明文字請在 ${DATABASE_FORM_LIMITS.scaleLabelMaxLength} 個字以內。`);
      }
    }
    if (field.unit.length > DATABASE_FORM_LIMITS.unitMaxLength) {
      return fail(`單位請在 ${DATABASE_FORM_LIMITS.unitMaxLength} 個字以內。`);
    }
    return [];
  });
}

/** `YYYY-MM-DD` 且是真的存在的日期（與後端 `DateOnly.TryParseExact` 相同）。 */
function isCalendarDate(text: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (match === null) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

type TrialOutcome =
  | { readonly errors: readonly DatabaseFieldError[] }
  | { readonly entries: readonly DatabaseRecordEntryView[] };

/** 試填驗證：只回傳預覽，不會建立紀錄。 */
export function evaluateTrial(fields: readonly DatabaseFieldView[], answers: DatabaseTrialAnswers): TrialOutcome {
  const errors: DatabaseFieldError[] = [];
  const entries: DatabaseRecordEntryView[] = [];

  fields.forEach((field): void => {
    const raw = answers[field.id];
    const list = Array.isArray(raw) ? raw.filter((item) => typeof item === 'string') : [];
    const text = typeof raw === 'string' ? raw.trim() : '';
    const empty = field.type === 'multiple-choice' ? list.length === 0 : text.length === 0;
    const fail = (message: string): void => {
      errors.push({ fieldId: field.id, message: `「${field.label}」${message}` });
    };

    if (empty) {
      if (field.required) fail('為必填。');
      else entries.push({ fieldId: field.id, label: field.label, display: '未填寫' });
      return;
    }

    let display = text;
    switch (field.type) {
      case 'number': {
        const value = Number(text);
        if (!Number.isFinite(value)) return fail('請輸入數字。');
        display = formatNumber(value, field.unit);
        break;
      }
      case 'date':
        if (!isCalendarDate(text)) return fail('請輸入日期。');
        break;
      case 'single-choice':
        if (!field.options.includes(text)) return fail('請從選項中選擇。');
        break;
      case 'multiple-choice':
        if (!list.every((item) => field.options.includes(item))) return fail('請從選項中選擇。');
        display = field.options.filter((option) => list.includes(option)).join('、');
        break;
      case 'scale': {
        const value = Number(text);
        const scale = field.scale;
        if (scale === null || !Number.isInteger(value) || value < scale.min || value > scale.max) {
          return fail(`請選擇 ${scale?.min ?? 1} 到 ${scale?.max ?? 5} 之間的分數。`);
        }
        display = `${value} / ${scale.max}`;
        break;
      }
      default:
        break;
    }
    entries.push({ fieldId: field.id, label: field.label, display });
  });

  return errors.length > 0 ? { errors } : { entries };
}

/** 已通過 evaluateTrial 驗證的答案轉成紀錄原始值；label 保存提交當下的欄位名稱。 */
export function toRecordValues(
  fields: readonly DatabaseFieldView[],
  answers: DatabaseTrialAnswers,
): DatabaseRecordValue[] {
  return fields.map((field): DatabaseRecordValue => {
    const raw: DatabaseTrialAnswer | undefined = answers[field.id];
    const text = typeof raw === 'string' ? raw.trim() : '';
    const base = { fieldId: field.id, label: field.label };
    switch (field.type) {
      case 'number':
        return { ...base, type: 'number', value: Number(text), unit: field.unit };
      case 'scale':
        return {
          ...base,
          type: 'scale',
          value: Number(text),
          min: field.scale?.min ?? 1,
          max: field.scale?.max ?? 5,
        };
      case 'multiple-choice': {
        const list = Array.isArray(raw) ? raw : [];
        return { ...base, type: 'multiple-choice', value: field.options.filter((option) => list.includes(option)) };
      }
      default:
        return { ...base, type: field.type, value: text };
    }
  });
}
