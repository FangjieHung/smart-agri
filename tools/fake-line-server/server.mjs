#!/usr/bin/env node
// A fake LINE Messaging API for the API-mode E2E (M5b Slice 6, issue #234).
//
// The API's `Line:ApiBaseUrl` points here (CI's `e2e-api` job, apps/admin-e2e/README.md), so the
// connection test, replies and pushes never reach LINE. It answers the endpoints
// `LineMessagingClient` calls the way the .NET test double
// (apps/api/tests/SmartAgri.Api.Tests/Infrastructure/FakeLineServer.cs) does:
//
// - bots are keyed by access token; an unknown token gets LINE's 401;
// - `GET /v2/bot/info` answers the bot's userId, basicId and displayName;
// - `PUT|GET /v2/bot/channel/webhook/endpoint` store and read the webhook URL;
// - `POST /v2/bot/channel/webhook/test` really delivers a signed test event — `destination` the bot's
//   userId, no events, `x-line-signature` = base64(HMAC-SHA256(channel secret, raw body)) — to the
//   given (or stored) endpoint, and reports what happened as LINE does
//   (`{ success, timestamp, statusCode, reason, detail }`);
// - reply and push answer 200 `{ "sentMessages": [] }`, the loading animation 202 `{}`;
// - every response carries an `x-line-request-id`.
//
// A spec drives it through the control API (never part of LINE's):
// - `POST /__control/bots` `{ accessToken, channelSecret, basicId, displayName?, userId? }` adds or
//   replaces a bot and answers it (a userId is made up when none is given);
// - `GET /__control/messages?accessToken=…` the recorded replies and pushes of that bot, in order:
//   `{ endpoint: 'reply' | 'push', replyToken?, to?, messages, receivedAt }`;
// - `GET /__control/requests?accessToken=…` every recorded request of that bot (method, path, body);
// - `POST /__control/reset` forgets every bot and request (`?accessToken=…`: only that bot's);
// - `GET /__control/health` answers 200 once listening.
//
// Only `node:http` and `node:crypto`; no npm dependencies. Port: `FAKE_LINE_PORT` (default 5180).
// It keeps everything in memory and logs one line per request without bodies or tokens.

import { createHmac, randomUUID } from 'node:crypto';
import { createServer } from 'node:http';

const port = Number(process.env['FAKE_LINE_PORT'] || 5180);
const host = process.env['FAKE_LINE_HOST'] || '127.0.0.1';
/** How long the webhook test waits for our endpoint (LINE gives up after a few seconds too). */
const WEBHOOK_TEST_TIMEOUT_MS = 5000;
const MAX_BODY_BYTES = 1024 * 1024;

/** @type {Map<string, { accessToken: string, channelSecret: string, userId: string, basicId: string, displayName: string, premiumId: string | null, webhookEndpoint: string | null }>} */
const bots = new Map();
/** @type {Array<{ accessToken: string | null, method: string, path: string, body: unknown, receivedAt: string }>} */
const requests = [];

function send(response, status, body) {
  const text = JSON.stringify(body ?? {});
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'x-line-request-id': randomUUID(),
  });
  response.end(text);
}

function readBody(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    request.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        reject(new Error('body too large'));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => resolve(Buffer.concat(chunks)));
    request.on('error', reject);
  });
}

function parseJson(raw) {
  if (raw.length === 0) return null;
  try {
    return JSON.parse(raw.toString('utf8'));
  } catch {
    return undefined;
  }
}

/** base64(HMAC-SHA256(channel secret, raw body)): LINE's `x-line-signature`. */
export function lineSignature(channelSecret, rawBody) {
  return createHmac('sha256', channelSecret).update(rawBody).digest('base64');
}

function tokenOf(request) {
  const authorization = request.headers['authorization'];
  return typeof authorization === 'string' && authorization.startsWith('Bearer ') ? authorization.slice('Bearer '.length) : null;
}

/** The path from `/v2/` on, whatever base path `Line:ApiBaseUrl` has. */
function apiPath(pathname) {
  const start = pathname.indexOf('/v2/');
  return start >= 0 ? pathname.slice(start) : pathname;
}

function isMessageList(messages) {
  return Array.isArray(messages) && messages.length >= 1 && messages.length <= 5
    && messages.every((message) => message !== null && typeof message === 'object' && typeof message.type === 'string');
}

/** Delivers a signed test event to `endpoint` and describes the outcome as LINE's webhook test does. */
async function testWebhook(bot, endpoint) {
  const timestamp = new Date().toISOString();
  const raw = Buffer.from(JSON.stringify({ destination: bot.userId, events: [] }), 'utf8');
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), WEBHOOK_TEST_TIMEOUT_MS);
  try {
    const answer = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'content-type': 'application/json; charset=utf-8',
        'user-agent': 'LineBotWebhook/2.0',
        'x-line-signature': lineSignature(bot.channelSecret, raw),
      },
      body: raw,
      signal: controller.signal,
    });
    await answer.arrayBuffer().catch(() => undefined);
    return answer.ok
      ? { success: true, timestamp, statusCode: answer.status, reason: 'OK', detail: String(answer.status) }
      : { success: false, timestamp, statusCode: answer.status, reason: 'ERROR_STATUS_CODE', detail: String(answer.status) };
  } catch (error) {
    return controller.signal.aborted
      ? { success: false, timestamp, statusCode: 0, reason: 'REQUEST_TIMEOUT', detail: 'Request timeout' }
      : { success: false, timestamp, statusCode: 0, reason: 'COULD_NOT_CONNECT', detail: String(error?.cause?.code ?? error?.message ?? 'error') };
  } finally {
    clearTimeout(timer);
  }
}

async function handleLine(request, response, path, raw) {
  const token = tokenOf(request);
  const body = parseJson(raw);
  requests.push({ accessToken: token, method: request.method ?? '', path, body: body ?? null, receivedAt: new Date().toISOString() });
  const bot = token === null ? undefined : bots.get(token);
  if (bot === undefined) {
    send(response, 401, { message: 'Authentication failed. Confirm that the access token in the authorization header is valid.' });
    return;
  }

  if (body === undefined) {
    send(response, 400, { message: 'The request body has 1 error(s)' });
    return;
  }

  const endpoint = `${request.method} ${path}`;
  switch (endpoint) {
    case 'GET /v2/bot/info':
      send(response, 200, {
        userId: bot.userId,
        basicId: bot.basicId,
        premiumId: bot.premiumId,
        displayName: bot.displayName,
        chatMode: 'bot',
        markAsReadMode: 'auto',
      });
      return;
    case 'PUT /v2/bot/channel/webhook/endpoint':
      if (typeof body?.endpoint !== 'string' || body.endpoint.trim() === '') {
        send(response, 400, { message: 'The request body has 1 error(s)' });
        return;
      }
      bot.webhookEndpoint = body.endpoint;
      send(response, 200, {});
      return;
    case 'GET /v2/bot/channel/webhook/endpoint':
      if (bot.webhookEndpoint === null) {
        send(response, 404, { message: 'Not found' });
        return;
      }
      send(response, 200, { endpoint: bot.webhookEndpoint, active: true });
      return;
    case 'POST /v2/bot/channel/webhook/test': {
      const target = typeof body?.endpoint === 'string' && body.endpoint.trim() !== '' ? body.endpoint : bot.webhookEndpoint;
      if (target === null) {
        send(response, 400, { message: 'The webhook URL is not set' });
        return;
      }
      send(response, 200, await testWebhook(bot, target));
      return;
    }
    case 'POST /v2/bot/message/reply':
      if (typeof body?.replyToken !== 'string' || body.replyToken === '' || !isMessageList(body.messages)) {
        send(response, 400, { message: 'The request body has 1 error(s)' });
        return;
      }
      send(response, 200, { sentMessages: [] });
      return;
    case 'POST /v2/bot/message/push':
      if (typeof body?.to !== 'string' || body.to === '' || !isMessageList(body.messages)) {
        send(response, 400, { message: 'The request body has 1 error(s)' });
        return;
      }
      send(response, 200, { sentMessages: [] });
      return;
    case 'POST /v2/bot/chat/loading/start':
      send(response, 202, {});
      return;
    default:
      send(response, 404, { message: 'Not found' });
  }
}

function recordedMessages(accessToken) {
  return requests
    .filter((entry) => entry.accessToken === accessToken && entry.method === 'POST'
      && (entry.path === '/v2/bot/message/reply' || entry.path === '/v2/bot/message/push'))
    .map((entry) => {
      const body = /** @type {{ replyToken?: string, to?: string, messages?: unknown[] }} */ (entry.body ?? {});
      return entry.path === '/v2/bot/message/reply'
        ? { endpoint: 'reply', replyToken: body.replyToken ?? null, messages: body.messages ?? [], receivedAt: entry.receivedAt }
        : { endpoint: 'push', to: body.to ?? null, messages: body.messages ?? [], receivedAt: entry.receivedAt };
    });
}

function handleControl(request, response, url, raw) {
  const route = `${request.method} ${url.pathname}`;
  const accessToken = url.searchParams.get('accessToken');
  switch (route) {
    case 'GET /__control/health':
      send(response, 200, { status: 'ok', bots: bots.size, requests: requests.length });
      return;
    case 'POST /__control/bots': {
      const body = parseJson(raw);
      const valid = body !== null && typeof body === 'object'
        && typeof body.accessToken === 'string' && body.accessToken !== ''
        && typeof body.channelSecret === 'string' && body.channelSecret !== ''
        && typeof body.basicId === 'string' && body.basicId !== '';
      if (!valid) {
        send(response, 400, { message: 'accessToken, channelSecret and basicId are required' });
        return;
      }
      const bot = {
        accessToken: body.accessToken,
        channelSecret: body.channelSecret,
        userId: typeof body.userId === 'string' && body.userId !== '' ? body.userId : 'U' + randomUUID().replace(/-/g, ''),
        basicId: body.basicId,
        displayName: typeof body.displayName === 'string' ? body.displayName : '安心客服',
        premiumId: typeof body.premiumId === 'string' ? body.premiumId : null,
        webhookEndpoint: null,
      };
      bots.set(bot.accessToken, bot);
      send(response, 200, { userId: bot.userId, basicId: bot.basicId, displayName: bot.displayName });
      return;
    }
    case 'GET /__control/messages':
      send(response, 200, accessToken === null ? [] : recordedMessages(accessToken));
      return;
    case 'GET /__control/requests':
      send(response, 200, requests
        .filter((entry) => accessToken === null || entry.accessToken === accessToken)
        .map(({ method, path, body, receivedAt }) => ({ method, path, body, receivedAt })));
      return;
    case 'POST /__control/reset':
      if (accessToken === null) {
        bots.clear();
        requests.length = 0;
      } else {
        bots.delete(accessToken);
        for (let index = requests.length - 1; index >= 0; index -= 1) {
          if (requests[index].accessToken === accessToken) requests.splice(index, 1);
        }
      }
      send(response, 200, {});
      return;
    default:
      send(response, 404, { message: 'Not found' });
  }
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', `http://${request.headers.host ?? 'localhost'}`);
  let raw;
  try {
    raw = await readBody(request);
  } catch {
    send(response, 413, { message: 'Request entity too large' });
    return;
  }

  try {
    if (url.pathname.startsWith('/__control/')) {
      handleControl(request, response, url, raw);
    } else {
      await handleLine(request, response, apiPath(url.pathname), raw);
    }
  } catch (error) {
    console.error(`fake LINE: ${request.method} ${url.pathname} failed: ${error?.message ?? error}`);
    if (!response.headersSent) send(response, 500, { message: 'Internal server error' });
  } finally {
    // One line per request: no bodies, tokens or ids.
    console.log(`${new Date().toISOString()} ${request.method} ${url.pathname} ${response.statusCode}`);
  }
});

server.listen(port, host, () => {
  console.log(`fake LINE Messaging API listening on http://${host}:${port}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
