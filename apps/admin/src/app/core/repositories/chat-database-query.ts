import type { ChatDatabaseQueryView, ChatReplyView } from '../domain/conversation.model';
import type { DatabaseId, DatabasePeriodSummaryView } from '../domain/database.model';
import { formatNumber } from './database-tracking';

/*
 * 對話中的數據庫查詢（issue #149）在 mock 的版本：文字與後端 `DatabaseQueryTools` 逐字相同，
 * 數字取自 `summarizePeriod`（與固定查詢同一套規則），所以 Mock 與 API 模式顯示一致。
 */

/** 與後端 `DatabaseQueryTools.NotAvailableText` 相同：未連接、無權限、已撤銷都是這一句。 */
export const CHAT_QUERY_NOT_AVAILABLE_TEXT =
  '目前無法查詢：這個助理沒有連接你可以查看的數據庫，或你的查詢權限已被收回。如需這些數字，請洽數據庫的資料管理者。';

const RECORD_UNIT = '筆';

export function notAvailableQueryReply(): ChatReplyView {
  return {
    kind: 'database-query',
    text: CHAT_QUERY_NOT_AVAILABLE_TEXT,
    query: {
      status: 'not-available',
      databaseId: null,
      databaseName: null,
      query: null,
      queryLabel: null,
      period: null,
      previousPeriod: null,
      subjectOnly: false,
      figures: [],
      message: null,
    },
  };
}

/** 固定查詢 `record-count` 的回答（後端 `DatabaseQueryTools.Compose` 的筆數版本）。 */
export function recordCountQueryReply(
  databaseId: DatabaseId,
  databaseName: string,
  summary: DatabasePeriodSummaryView,
): ChatReplyView {
  const display = formatNumber(summary.recordCount, RECORD_UNIT);
  const previousDisplay = formatNumber(summary.previousRecordCount, RECORD_UNIT);
  const source = `根據「${databaseName}」的紀錄筆數查詢`;
  const previous = `前一期（${summary.previousPeriod.from} 至 ${summary.previousPeriod.to}）`;
  const noData = summary.recordCount === 0;
  const text = noData
    ? `${source}：${summary.period.label}沒有任何有效紀錄，共 ${display}；${previous}為 ${previousDisplay}。`
    : `${source}：${summary.period.label}共有 ${display}有效紀錄；${previous}為 ${previousDisplay}，變化 ${summary.recordCountChangeLabel}。`;
  const query: ChatDatabaseQueryView = {
    status: noData ? 'no-data' : 'answered',
    databaseId,
    databaseName,
    query: 'record-count',
    queryLabel: '紀錄筆數',
    period: summary.period,
    previousPeriod: summary.previousPeriod,
    subjectOnly: false,
    figures: [
      {
        metric: '有效紀錄筆數',
        value: summary.recordCount,
        display,
        previousDisplay,
        changeLabel: summary.recordCountChangeLabel,
      },
    ],
    message: null,
  };
  return { kind: 'database-query', text, query };
}
