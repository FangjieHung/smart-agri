import type { DatabaseFieldType, DatabaseRecordSource } from '../../core/domain/database.model';

export const FIELD_TYPE_LABELS: Readonly<Record<DatabaseFieldType, string>> = {
  text: '文字',
  number: '數字',
  date: '日期',
  'single-choice': '單選',
  'multiple-choice': '多選',
  scale: '量尺',
};

export const RECORD_SOURCE_LABELS: Readonly<Record<DatabaseRecordSource, string>> = {
  'assistant-conversation': '助理對話',
  'form-link': '表單連結',
};
