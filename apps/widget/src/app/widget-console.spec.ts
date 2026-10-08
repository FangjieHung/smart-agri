import { FakeServer, QUESTION, RECORDED, mount, mountReady, session, type Responder } from './widget.testing';

/**
 * #226：暫停服務（`403 public-assistant`）與頻率限制（`429`）是預期中的回應，畫面處理正確，
 * 但 `@ag-ui/client` 1.0.0 會自己在主控台印一行 `Agent execution failed: …`，而且沒有正式的方式關掉
 * （`AbstractAgent.onError` 直接呼叫 `console.error`；`onRunFailed` 的型別刻意排除 `stopPropagation`，
 * 它還會連錯誤一起吞掉）。所以不關掉它，改在 `apps/widget/README.md` 與 `deploy/README.md` 說明。
 *
 * 這裡釘住客戶工程師在主控台看到的確切內容：升級 `@ag-ui/client` 後若改變，這裡會失敗，
 * 請一併更新兩份文件。回應本文是真實 API 錄下的（`RECORDED`，#329），內容類型也一樣是
 * `application/problem+json`（`@ag-ui/client` 不會解析它，原樣放進錯誤訊息）。
 */
const FORBIDDEN_BODY = RECORDED.runPublicAssistant403.body;
const RATE_LIMITED_BODY = RECORDED.runRateLimited429.body;

const problem =
  (status: number, body: string): Responder =>
  () =>
    new Response(body, { status, headers: { 'content-type': 'application/problem+json', 'Retry-After': '3' } });

describe('console output for expected refusals (#226)', () => {
  let lines: string[];

  beforeEach(() => {
    sessionStorage.clear();
    lines = [];
    vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
      lines.push(args.map((arg) => (arg instanceof Error ? `${arg.name}: ${arg.message}` : String(arg))).join(' '));
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    document.body.replaceChildren();
  });

  it.each([
    [403, FORBIDDEN_BODY, '目前暫停服務'],
    [429, RATE_LIMITED_BODY, '問題太頻繁了，請稍後再試'],
  ])('a %s when asking shows the usual screen and logs only the documented @ag-ui/client line', async (status, body, screen) => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(problem(status, body));
    const view = await mountReady(fake);

    view.type(QUESTION);
    view.send();
    await view.until(() => view.text().includes(screen));

    expect(lines).toEqual([`Agent execution failed: Error: HTTP ${status}: ${body}`]);
  });

  it.each([
    [403, FORBIDDEN_BODY, '這個對話視窗目前無法使用'],
    [429, RATE_LIMITED_BODY, '請求太頻繁了'],
  ])('a %s when opening the window logs nothing (the session request does not go through @ag-ui/client)', async (status, body, screen) => {
    const fake = new FakeServer();
    fake.sessions.push(problem(status, body));
    const view = await mount(fake);

    await view.until(() => view.text().includes(screen));

    expect(lines).toEqual([]);
  });
});
