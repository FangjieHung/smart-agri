import { createHmac, randomBytes } from 'node:crypto';
import { defineConfig } from 'cypress';

/**
 * API 模式的 E2E（M2 Slice 17，issue #51）：對真實後端跑，只收 `src/e2e-api/` 底下的 spec。
 *
 * 與 cypress.config.ts（mock 模式）不同，這裡不透過 Nx preset 啟動 dev server——API、資料庫與
 * `nx serve admin --configuration=api` 都要先準備好（CI 的 `e2e-api` job；本機做法見
 * apps/admin-e2e/README.md）。Nx 的 @nx/cypress plugin 只認得 `cypress.config.*`，所以這個檔案
 * 不會被推斷成 target，改由 project.json 的 `e2e-api` target 以 `--config-file` 指定。
 *
 * 環境變數：
 * - `ADMIN_E2E_API_BASE_URL`：admin 的網址（預設 http://localhost:4200，即 `nx serve` 的預設埠）。
 * - `ADMIN_E2E_API_URL`：API 的網址（預設 http://localhost:5153），也是 `PublicChannels:PublicBaseUrl`；
 *   website-embed-api.cy.ts 直接開 API 提供的對話視窗 `/use/{id}`，並以 `cy.request` 檢查它的標頭。
 * - `SEED_DEMO_PASSWORD`：`migrate` 建立示範帳號時用的密碼，spec 用它登入；沒有設定時 spec 直接失敗。
 * - `ADMIN_E2E_FAKE_LINE_URL`：假的 LINE 伺服器（tools/fake-line-server/server.mjs，預設 http://127.0.0.1:5180），
 *   也就是 API 的 `Line:ApiBaseUrl`；line-api.cy.ts 透過下面的 `line*` task 設定它、讀它收到的 reply／push。
 */
const API_URL = (process.env['ADMIN_E2E_API_URL'] || 'http://localhost:5153').replace(/\/$/, '');
const FAKE_LINE_URL = (process.env['ADMIN_E2E_FAKE_LINE_URL'] || 'http://127.0.0.1:5180').replace(/\/$/, '');

/** 假 LINE 伺服器的控制 API（不是 LINE 的 API）；回應不是 2xx 時讓 task 失敗並說明。 */
async function fakeLine<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${FAKE_LINE_URL}${path}`, {
      method,
      headers: body === undefined ? undefined : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (error) {
    throw new Error(`The fake LINE server at ${FAKE_LINE_URL} is not reachable (start tools/fake-line-server/server.mjs): ${String(error)}`);
  }
  const text = await response.text();
  if (!response.ok) throw new Error(`fake LINE ${method} ${path} answered ${response.status}: ${text}`);
  return JSON.parse(text) as T;
}

/** LINE 的事件 id 是 26 字元的 ULID；後端依它去重，所以每個事件都要不同。 */
function newWebhookEventId(): string {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  return Array.from(randomBytes(26), (byte) => alphabet[byte % 32]).join('');
}

/** `lineSendText` 的參數：一位 LINE 使用者（一對一，或在群組裡 @ 官方帳號）傳來的文字訊息。 */
interface LineTextEvent {
  readonly assistantId: string;
  readonly channelSecret: string;
  /** 官方帳號（bot）的 userId：測試連線時記下的，webhook 的 `destination` 必須等於它。 */
  readonly destination: string;
  readonly userId: string;
  readonly text: string;
  /** 有值時是群組訊息；`mention` 是要 @ 官方帳號的文字（例如「@安心客服」），放在 `text` 前面。 */
  readonly groupId?: string;
  readonly mention?: string;
}
export default defineConfig({
  e2e: {
    baseUrl: process.env['ADMIN_E2E_API_BASE_URL'] || 'http://localhost:4200',
    specPattern: 'src/e2e-api/**/*.cy.ts',
    supportFile: 'src/support/e2e.ts',
    fixturesFolder: 'src/fixtures',
    fileServerFolder: '.',
    screenshotsFolder: '../../dist/cypress/apps/admin-e2e-api/screenshots',
    videosFolder: '../../dist/cypress/apps/admin-e2e-api/videos',
    video: false,
    chromeWebSecurity: false,
    // 背景工作處理文件、PKCE 導轉都比 mock 慢；個別等待處理完成的斷言另外放寬。
    defaultCommandTimeout: 10000,
    // 密碼只經 `cy.env()` 讀取；關掉舊的 `Cypress.env()`，受測頁面的程式碼就讀不到它。
    allowCypressEnv: false,
    env: {
      demoPassword: process.env['SEED_DEMO_PASSWORD'] ?? '',
      apiUrl: API_URL,
    },
    setupNodeEvents(on) {
      // 跨 test 保留的值（只在這次 `cypress run` 的記憶體裡）。spec 的 test 換到另一個來源（例如 API 提供的
      // 對話視窗 `/use/{id}`）時，Cypress 會在新來源重新載入 spec，模組裡的變數就沒了，所以存在 Node 這一側。
      const values = new Map<string, unknown>();
      on('task', {
        rememberValue({ key, value }: { key: string; value: unknown }) {
          values.set(key, value);
          return null;
        },
        recallValue(key: string) {
          return values.get(key) ?? null;
        },

        // 假的 LINE 伺服器（M5b #234）：新增或取代一個 bot，回傳它的 userId（webhook 的 destination）。
        lineAddBot(bot: { accessToken: string; channelSecret: string; basicId: string; displayName?: string }) {
          return fakeLine<{ userId: string; basicId: string; displayName: string }>('POST', '/__control/bots', bot);
        },
        /** 這個 bot 收到的 reply 與 push，依收到的順序。 */
        lineMessages(accessToken: string) {
          return fakeLine<unknown[]>('GET', `/__control/messages?accessToken=${encodeURIComponent(accessToken)}`);
        },
        /** 忘記這個 bot 與它收到的所有請求。 */
        lineReset(accessToken: string) {
          return fakeLine<object>('POST', `/__control/reset?accessToken=${encodeURIComponent(accessToken)}`).then(() => null);
        },
        /**
         * 模擬 LINE 平台把一則文字訊息送到助理的 webhook：在 Node 這一側組事件、以 Channel secret 對「實際送出的
         * 位元組」算 HMAC-SHA256（base64，放在 `x-line-signature`），送到 `{API}/api/v1/line/webhook/{assistantId}`。
         * 回傳 HTTP 狀態與這個事件的 replyToken（後端回覆時會帶上它）。每個事件的 webhookEventId 都不同（後端依它去重）。
         */
        async lineSendText(event: LineTextEvent) {
          const replyToken = randomBytes(16).toString('hex');
          const webhookEventId = newWebhookEventId();
          const text = event.mention ? `${event.mention} ${event.text}` : event.text;
          const message: Record<string, unknown> = {
            id: String(Date.now()),
            type: 'text',
            quoteToken: randomBytes(16).toString('hex'),
            text,
          };
          if (event.mention) {
            message['mention'] = {
              mentionees: [{ index: 0, length: event.mention.length, type: 'user', userId: event.destination, isSelf: true }],
            };
          }
          const payload = {
            destination: event.destination,
            events: [{
              type: 'message',
              mode: 'active',
              timestamp: Date.now(),
              webhookEventId,
              deliveryContext: { isRedelivery: false },
              replyToken,
              source: event.groupId
                ? { type: 'group', groupId: event.groupId, userId: event.userId }
                : { type: 'user', userId: event.userId },
              message,
            }],
          };
          const raw = Buffer.from(JSON.stringify(payload), 'utf8');
          const signature = createHmac('sha256', event.channelSecret).update(raw).digest('base64');
          const response = await fetch(`${API_URL}/api/v1/line/webhook/${encodeURIComponent(event.assistantId)}`, {
            method: 'POST',
            headers: { 'content-type': 'application/json; charset=utf-8', 'x-line-signature': signature },
            body: raw,
          });
          await response.arrayBuffer();
          return { status: response.status, replyToken, webhookEventId };
        },
      });
    },
  },
});
