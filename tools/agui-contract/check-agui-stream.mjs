#!/usr/bin/env node
// Parses POST /api/v1/assistants/{id}/chat/runs output with @ag-ui/client — the same
// HttpAgent the admin app uses (M3 plan Slice 7/10; ticket #77) — to prove the backend's
// AG-UI stream is one the JavaScript client accepts: event order, ids, CUSTOM events, RUN_ERROR.
//
// Usage:
//   node tools/agui-contract/check-agui-stream.mjs
//       Checks every recorded stream in tools/agui-contract/fixtures/ (CI runs this). The
//       fixtures are recorded from the real endpoint by the Api integration test
//       ChatRunEndpointsTests.The_recorded_streams_match_the_fixtures_the_ag_ui_client_check_parses,
//       which fails whenever the endpoint's output drifts from them (re-record with
//       UPDATE_AGUI_FIXTURES=1, then rerun this script).
//   node tools/agui-contract/check-agui-stream.mjs --url <runs-endpoint> --token <bearer> --question <text>
//       Runs one question against a running Api instead (manual check).
import { readdir, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { HttpAgent } from '@ag-ui/client';

const REPLY_EVENT = 'smartagri.reply';
const THREAD_EVENT = 'smartagri.thread';

/** What each recorded fixture must parse into. */
const EXPECTATIONS = {
  'company-data-saved.sse': { outcome: 'finished', replyKind: 'company-data', thread: true },
  'no-result-unsaved.sse': { outcome: 'finished', replyKind: 'no-result', thread: false },
  'fail-midway.sse': { outcome: 'error', errorCode: 'chat-unavailable' },
};

/** Runs @ag-ui/client's HttpAgent over one response and records what it saw. */
async function parseWithClient(fetchImpl, { threadId = '', question = '問題' } = {}) {
  const agent = new HttpAgent({
    url: 'http://smart-agri.test/api/v1/assistants/assistant/chat/runs',
    threadId,
    fetch: fetchImpl,
    initialMessages: [{ id: 'question', role: 'user', content: question }],
  });
  const seen = { types: [], custom: [], errors: [], failed: null, messages: [] };
  const subscriber = {
    onEvent: ({ event }) => void seen.types.push(event.type),
    onCustomEvent: ({ event }) => void seen.custom.push({ name: event.name, value: event.value }),
    onRunErrorEvent: ({ event }) => void seen.errors.push({ code: event.code, message: event.message }),
    onRunFailed: ({ error }) => void (seen.failed = error),
  };
  try {
    await agent.runAgent({ runId: 'run-check' }, subscriber);
  } catch (error) {
    seen.failed ??= error;
  }
  seen.messages = agent.messages;
  return seen;
}

function check(name, seen, expected) {
  const problems = [];
  const expect = (condition, message) => void (condition || problems.push(message));

  expect(seen.types[0] === 'RUN_STARTED', `first event is ${seen.types[0]}, not RUN_STARTED`);
  expect(seen.types.includes('TEXT_MESSAGE_CONTENT'), 'no TEXT_MESSAGE_CONTENT');
  if (expected.outcome === 'finished') {
    expect(seen.failed === null, `the client rejected the stream: ${seen.failed?.message ?? seen.failed}`);
    expect(seen.types.at(-1) === 'RUN_FINISHED', `last event is ${seen.types.at(-1)}, not RUN_FINISHED`);
    const names = seen.custom.map((event) => event.name);
    expect(names[0] === REPLY_EVENT, `first CUSTOM event is ${names[0]}, not ${REPLY_EVENT}`);
    expect(names.includes(THREAD_EVENT) === expected.thread, `${THREAD_EVENT} ${expected.thread ? 'missing' : 'unexpected'}`);
    const reply = seen.custom.find((event) => event.name === REPLY_EVENT)?.value;
    expect(typeof reply?.id === 'string' && reply.author === 'assistant', 'smartagri.reply is not a ChatMessageView');
    expect(reply?.reply?.kind === expected.replyKind, `reply kind ${reply?.reply?.kind}, not ${expected.replyKind}`);
    expect(Array.isArray(reply?.reply?.citations) && Array.isArray(reply?.reply?.nextSteps), 'reply lacks citations/nextSteps arrays');
    if (expected.thread) {
      const thread = seen.custom.find((event) => event.name === THREAD_EVENT)?.value;
      expect(typeof thread?.threadId === 'string' && typeof thread?.title === 'string', 'smartagri.thread lacks threadId/title');
    }
    const streamed = seen.messages.find((message) => message.role === 'assistant');
    expect(typeof streamed?.content === 'string' && streamed.content.length > 0, 'the client built no assistant message from the stream');
  } else {
    expect(seen.types.at(-1) === 'RUN_ERROR', `last event is ${seen.types.at(-1)}, not RUN_ERROR`);
    expect(seen.errors.length === 1 && seen.errors[0].code === expected.errorCode, `RUN_ERROR code ${seen.errors[0]?.code}, not ${expected.errorCode}`);
    expect(!seen.types.includes('RUN_FINISHED'), 'RUN_FINISHED after an error');
    expect(seen.custom.length === 0, 'CUSTOM events after an error');
  }

  if (problems.length > 0) {
    console.error(`✗ ${name}\n  - ${problems.join('\n  - ')}\n  events: ${seen.types.join(' → ')}`);
    return false;
  }

  console.log(`✓ ${name}: ${seen.types.join(' → ')}`);
  return true;
}

const sseResponse = (body) => async () =>
  new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream' } });

async function checkFixtures() {
  const directory = join(dirname(fileURLToPath(import.meta.url)), 'fixtures');
  const files = (await readdir(directory)).filter((file) => file.endsWith('.sse')).sort();
  const missing = Object.keys(EXPECTATIONS).filter((file) => !files.includes(file));
  if (missing.length > 0) {
    console.error(`✗ missing fixtures: ${missing.join(', ')}`);
    return false;
  }

  let ok = true;
  for (const file of files) {
    const expected = EXPECTATIONS[file];
    if (expected === undefined) {
      console.error(`✗ ${file}: no expectation in check-agui-stream.mjs`);
      ok = false;
      continue;
    }
    const body = await readFile(join(directory, file), 'utf8');
    ok = check(file, await parseWithClient(sseResponse(body)), expected) && ok;
  }
  return ok;
}

async function checkLive({ url, token, question }) {
  const seen = await parseWithClient(
    (target, init) => fetch(url, { ...init, headers: { ...init.headers, authorization: `Bearer ${token}` } }),
    { question },
  );
  const finished = seen.types.at(-1) === 'RUN_FINISHED';
  const kind = seen.custom.find((event) => event.name === REPLY_EVENT)?.value?.reply?.kind;
  return check(
    url,
    seen,
    finished ? { outcome: 'finished', replyKind: kind, thread: seen.custom.some((event) => event.name === THREAD_EVENT) } : { outcome: 'error', errorCode: seen.errors[0]?.code },
  );
}

const { values } = parseArgs({ options: { url: { type: 'string' }, token: { type: 'string' }, question: { type: 'string' } } });
const ok = values.url
  ? await checkLive({ url: values.url, token: values.token ?? '', question: values.question ?? '你好' })
  : await checkFixtures();
process.exit(ok ? 0 : 1);
