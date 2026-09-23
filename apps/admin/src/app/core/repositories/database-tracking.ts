import {
  DATABASE_FIELD_TYPES,
  isChoiceFieldType,
  type ComparisonPointView,
  type DatabaseFieldError,
  type DatabaseFieldView,
  type DatabaseRecordEntryView,
  type DatabaseRecordValue,
  type DatabaseTrialAnswer,
  type DatabaseRecordView,
  type DatabaseTrialAnswers,
  type MetricComparisonView,
  type SubjectComparisonView,
  type WithdrawnRecordView,
} from '../domain/database.model';
import type { DatabaseRecordFixture } from './demo-seed-databases';

/*
 * Mock 的業務計算集中在這裡：欄位驗證、試填驗證、紀錄格式化與本次／上次／首次比較。
 * 畫面元件只顯示這些結果，不自行推導差異。
 */

const numberFormat = new Intl.NumberFormat('en-US');

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
    dateLabel: record.recordedAt.slice(0, 10),
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
    submittedDateLabel: record.recordedAt.slice(0, 10),
    withdrawnDateLabel: (record.withdrawnAt ?? '').slice(0, 10),
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
    const points = chronological.flatMap((record): ComparisonPointView[] => {
      const match = record.values.find((candidate) => candidate.fieldId === value.fieldId);
      if (match === undefined || (match.type !== 'number' && match.type !== 'scale')) return [];
      return [
        {
          recordId: record.id,
          dateLabel: record.recordedAt.slice(0, 10),
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

  return {
    status: 'available',
    recordCount,
    summary: `比較 ${recordCount} 筆已同意提交的紀錄（${chronological[0].recordedAt.slice(0, 10)} 至 ${latest.recordedAt.slice(0, 10)}）。`,
    metrics,
  };
}

/** 修正空白與不屬於該類型的設定，讓儲存的欄位結構一致。 */
export function normalizeField(field: DatabaseFieldView): DatabaseFieldView {
  return {
    id: field.id,
    label: field.label.trim(),
    type: field.type,
    required: field.required === true,
    options: isChoiceFieldType(field.type)
      ? field.options.map((option) => option.trim()).filter((option) => option.length > 0)
      : [],
    scale: field.type === 'scale' ? (field.scale ?? { min: 1, max: 5, minLabel: '', maxLabel: '' }) : null,
    unit: field.type === 'number' ? field.unit.trim() : '',
  };
}

export function validateFields(fields: readonly DatabaseFieldView[]): DatabaseFieldError[] {
  if (fields.length === 0) return [{ fieldId: null, message: '表單至少需要一個欄位。' }];

  const seen = new Set<string>();
  return fields.flatMap((field): DatabaseFieldError[] => {
    if (!DATABASE_FIELD_TYPES.includes(field.type)) {
      return [{ fieldId: field.id, message: '不支援的欄位類型。' }];
    }
    if (field.label.length === 0) return [{ fieldId: field.id, message: '請填寫欄位名稱。' }];
    if (seen.has(field.label)) return [{ fieldId: field.id, message: '欄位名稱不可重複。' }];
    seen.add(field.label);
    if (isChoiceFieldType(field.type) && field.options.length < 2) {
      return [{ fieldId: field.id, message: '單選或多選至少需要 2 個選項。' }];
    }
    const scale = field.scale;
    if (
      field.type === 'scale' &&
      (scale === null ||
        !Number.isInteger(scale.min) ||
        !Number.isInteger(scale.max) ||
        scale.min >= scale.max)
    ) {
      return [{ fieldId: field.id, message: '量尺的最小值必須小於最大值。' }];
    }
    if (scale !== null && field.type === 'scale' && scale.max - scale.min > 10) {
      return [{ fieldId: field.id, message: '量尺最多 11 個刻度。' }];
    }
    return [];
  });
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
        if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) return fail('請輸入日期。');
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
