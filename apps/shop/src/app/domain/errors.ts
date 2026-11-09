import type { ApiResult } from '../api/ordering-api';
import type { Problem } from '../api/types';

/** 送出失敗後畫面要做的事。 */
export type Recovery =
  /** 重新取菜單、重新檢查購物車，請顧客確認後再送。 */
  | 'refresh-menu'
  /** 回到取餐時間選擇。 */
  | 'pick-time'
  /** 在購物車標出無法供應的行。 */
  | 'mark-lines'
  /** 在欄位旁顯示錯誤。 */
  | 'fix-fields'
  /** 已經送出過：有 token 就帶去狀態頁。 */
  | 'show-existing'
  /** 店家不接單：停在畫面上，只能聯絡店家。 */
  | 'stop'
  /** 未達外送門檻（#401）：回購物車再加點。 */
  | 'add-more'
  /** 桌號 QR code 已失效（#402）：忘掉這桌，請顧客洽店員。 */
  | 'table-gone'
  /** 可以用同一個送出鍵再試一次。 */
  | 'retry';

export interface SubmitFailure {
  readonly recovery: Recovery;
  readonly message: string;
  /** `invalid-order` 的欄位錯誤（`customer.phone` → 訊息）。 */
  readonly fields: Readonly<Record<string, string>>;
  /** 有問題的購物車行（從 0 起）與原因代碼。 */
  readonly lines: readonly { line: number; code: string }[];
  readonly orderNumber: string | null;
}

/** 購物車行的錯誤代碼 → 顯示在那一行旁邊的說明。內用（#402）沒有時間可改，只請顧客移除。 */
export function lineProblemText(code: string, dineIn = false): string {
  switch (code) {
    case 'sold-out-today':
      return dineIn ? '今天已售完，請移除' : '今天已售完，請移除或改選其他時間';
    case 'unavailable':
      return '目前停售，請移除';
    case 'category-unavailable':
      return dineIn ? '現在不供應，請移除' : '這個取餐時間不供應，請移除或改選時間';
    case 'unknown-item':
      return '已經下架，請移除';
    case 'invalid-quantity':
      return '份數不正確，請重新調整';
    default:
      return '選項和目前的菜單不符，請重新選擇';
  }
}

/**
 * 送出訂單失敗時要顯示的訊息與接下來的處理（設計文件 §4.2 的每個 `reason`）。伺服器的 `message` 已經是
 * 可以直接顯示的中文，這裡只在需要更具體的下一步時換掉它；不認得的回應給通用說明。內用（#402，`dineIn`）
 * 沒有時間可改，請顧客洽店員。
 */
export function describeSubmitFailure(
  result: Exclude<ApiResult<unknown>, { ok: true }>,
  shopPhone: string,
  dineIn = false,
): SubmitFailure {
  const base = { fields: {}, lines: [], orderNumber: null };
  if (!('problem' in result)) {
    return {
      ...base,
      recovery: 'retry',
      message: '網路連線中斷，訂單可能沒有送出。請確認連線後按「再試一次」，不會重複下單。',
    };
  }

  const problem: Problem = result.problem;
  const serverMessage = problem.message || '';
  switch (problem.reason) {
    case 'menu-changed':
      return {
        ...base,
        recovery: 'refresh-menu',
        message: '菜單剛剛更新了。我們已重新整理購物車，請確認品項與金額後再送出。',
      };
    case 'slot-full':
      return {
        ...base,
        recovery: 'pick-time',
        message: '這個取餐時間剛好額滿了，請改選其他時間。',
      };
    case 'slot-unavailable':
      return { ...base, recovery: 'pick-time', message: '這個取餐時間已經不能選了，請重新選擇。' };
    case 'service-unavailable':
      if (dineIn)
        return {
          ...base,
          recovery: 'stop',
          message: serverMessage || '現在不是內用點餐的時間，請洽店員。',
        };
      return {
        ...base,
        recovery: 'pick-time',
        message: serverMessage || '這個取餐方式目前無法使用，請改選取餐時間。',
      };
    case 'unknown-zone':
      return {
        ...base,
        recovery: 'pick-time',
        message: '這個外送區域已經不能選了，請重新選擇外送區域。',
      };
    case 'below-minimum':
      return {
        ...base,
        recovery: 'add-more',
        message:
          problem.shortfall != null
            ? `還差 NT$${problem.shortfall.toLocaleString('en-US')} 達外送門檻，請回購物車再加點。`
            : serverMessage || '還沒達到外送門檻，請回購物車再加點。',
      };
    case 'items-unavailable':
      return {
        ...base,
        recovery: 'mark-lines',
        message: dineIn
          ? '購物車裡有品項現在無法供應，請移除標示的品項。'
          : '購物車裡有品項在這個取餐時間無法供應，請移除標示的品項或改選時間。',
        lines: (problem.lineErrors ?? []).map((error) => ({ line: error.line, code: error.code })),
      };
    case 'invalid-order': {
      const fields: Record<string, string> = {};
      for (const [field, messages] of Object.entries(problem.errors ?? {})) {
        if (messages.length > 0) fields[field] = messages[0];
      }
      const lineErrors = (problem.lineErrors ?? []).map((error) => ({
        line: error.line,
        code: error.code,
      }));
      return {
        ...base,
        recovery: lineErrors.length > 0 ? 'mark-lines' : 'fix-fields',
        message:
          lineErrors.length > 0
            ? '購物車裡有品項的選項和目前的菜單不符，請重新選擇。'
            : '有些資料需要修正，請看欄位旁的說明。',
        fields,
        lines: lineErrors,
      };
    }
    case 'duplicate-submission':
      return {
        ...base,
        recovery: 'show-existing',
        orderNumber: problem.orderNumber ?? null,
        message: `這筆訂單已經送出過了${problem.orderNumber ? `（訂單編號 ${problem.orderNumber}）` : ''}，不會重複下單。`,
      };
    case 'shop-paused':
      return {
        ...base,
        recovery: 'stop',
        message: dineIn
          ? '店家暫停接受線上點餐，請洽店員。'
          : `店家暫停接受線上點餐，請稍後再試或來電 ${shopPhone}。`,
      };
    case 'too-many-pending':
      return {
        ...base,
        recovery: 'stop',
        message:
          serverMessage ||
          (dineIn
            ? '這桌已經有多筆等待店家確認的訂單，請等店家確認後再加點，或洽店員。'
            : `這支電話已經有多筆等待店家確認的訂單，請等店家處理後再訂，或來電 ${shopPhone}。`),
      };
    case 'table-not-found':
      return {
        ...base,
        recovery: 'table-gone',
        message: serverMessage || '這個桌號的 QR code 已失效，請洽店員。',
      };
    case 'rate-limited':
      return {
        ...base,
        recovery: 'retry',
        message: `送出太頻繁了，請${result.retryAfter ? `在 ${result.retryAfter} 秒後` : '稍等一下再'}按「再試一次」。`,
      };
    case 'shop-not-found':
      return { ...base, recovery: 'stop', message: '找不到這家店的線上點餐頁面，請確認網址。' };
    default:
      return {
        ...base,
        recovery: 'retry',
        message: '送出時發生問題，請按「再試一次」；若仍然失敗，請來電店家。',
      };
  }
}
