import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  apiAssistantCaseTypeSourcePath,
  apiAssistantChatPath,
  apiChatCaseProposalPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 助理提議開案在 API 模式（issue #254）。下列回應是 2026-10-07 以 Development 環境（Fake 模型、關鍵字模式）、
 * 臨時資料庫 `m7_254` 實際呼叫 API 錄下的原始 JSON（`scratchpad/254/record.py`：管理者建立承辦組與類型，
 * 把「設備故障報修」加進自己助理的可提議類型，提問「二號冷藏庫溫度降不下來，需要報修」後確認、再問兩題，
 * 一則選「不用了」、一則在移除類型後確認），原樣 `JSON.parse`，不依產生的型別手寫。
 */
const ASSISTANT_ID = '01a11242-0000-7000-8000-000000000254';
const THREAD_ID = '01a11244-363e-7e20-a4e1-0f7b70cec133';
const MESSAGE_ID = '01a11244-367c-7913-b6b8-4f168236d058';
const DISMISSED_ID = '01a11244-36e7-7920-b2a8-d40a9e091355';
const REMOVED_ID = '01a11244-36f4-7306-98bc-4620fb87fdbf';
const TYPE_ID = '01a11243-3917-7d25-822d-7640177f1370';

/** `settings-add`（HTTP 200） */
const REAL_CASE_PROPOSAL_SETTINGS_ADDED_JSON =
  "{\"configuration\":{\"id\":\"01a11242-0000-7000-8000-000000000254\",\"ownerAccountId\":\"01a11242-c2e8-712b-a321-d789426fb262\",\"name\":\"設備小幫手\",\"purpose\":\"協助處理設備問題\",\"status\":\"ready\",\"viewerCanManage\":true,\"createdAt\":\"2026-10-06T17:30:24.047125+00:00\",\"updatedAt\":\"2026-10-06T17:30:24.047125+00:00\",\"acceptanceStatus\":\"not-accepted\"},\"knowledgeBaseIds\":[],\"databaseIds\":[],\"caseTypeIds\":[\"01a11243-3917-7d25-822d-7640177f1370\"],\"tone\":\"friendly\",\"roleInstructions\":\"\",\"rules\":{\"knowledgeScope\":\"company-data-only\",\"refusalMessage\":\"目前的資料中找不到這個問題的答案。\",\"showCitations\":true,\"keepConversations\":true,\"dataWriteDatabaseId\":null,\"dataWritePurpose\":\"\",\"periodicReport\":\"off\"},\"periodicReportAutoDisabled\":null}";

/** `settings-add-inactive-422`（HTTP 422） */
const REAL_CASE_PROPOSAL_SETTINGS_ADD_INACTIVE_422_JSON =
  "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.21\",\"title\":\"Unprocessable Content\",\"status\":422,\"reason\":\"case-type-inactive\",\"message\":\"這個案件類型已停用或不存在，請選擇其他類型。\",\"errors\":{\"caseTypeIds\":[\"這個案件類型已停用或不存在，請選擇其他類型。\"]}}";

/** `settings-remove`（HTTP 200） */
const REAL_CASE_PROPOSAL_SETTINGS_REMOVED_JSON =
  "{\"configuration\":{\"id\":\"01a11242-0000-7000-8000-000000000254\",\"ownerAccountId\":\"01a11242-c2e8-712b-a321-d789426fb262\",\"name\":\"設備小幫手\",\"purpose\":\"協助處理設備問題\",\"status\":\"ready\",\"viewerCanManage\":true,\"createdAt\":\"2026-10-06T17:30:24.047125+00:00\",\"updatedAt\":\"2026-10-06T17:30:24.047125+00:00\",\"acceptanceStatus\":\"not-accepted\"},\"knowledgeBaseIds\":[],\"databaseIds\":[],\"caseTypeIds\":[],\"tone\":\"friendly\",\"roleInstructions\":\"\",\"rules\":{\"knowledgeScope\":\"company-data-only\",\"refusalMessage\":\"目前的資料中找不到這個問題的答案。\",\"showCitations\":true,\"keepConversations\":true,\"dataWriteDatabaseId\":null,\"dataWritePurpose\":\"\",\"periodicReport\":\"off\"},\"periodicReportAutoDisabled\":null}";

/** `run-reply`（HTTP 200） */
const REAL_CASE_PROPOSAL_RUN_REPLY_JSON =
  "{\"id\":\"01a11244-367c-7913-b6b8-4f168236d058\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"二號冷藏庫溫度降不下來，需要報修\",\"description\":\"\",\"status\":\"proposed\",\"available\":true,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":null}},\"createdAt\":\"2026-10-06T17:30:22.716121+00:00\"}";

/** `chat`（HTTP 200） */
const REAL_CASE_PROPOSAL_CHAT_JSON =
  "{\"assistantId\":\"01a11242-0000-7000-8000-000000000254\",\"assistantName\":\"設備小幫手\",\"purpose\":\"協助處理設備問題\",\"threadId\":\"01a11244-363e-7e20-a4e1-0f7b70cec133\",\"title\":\"二號冷藏庫溫度降不下來，需要報修\",\"historyMode\":\"saved\",\"welcome\":\"你好，我是「設備小幫手」，協助處理設備問題有什麼我可以幫忙的嗎？\",\"privacyNotice\":\"只有你自己看得到這裡的對話紀錄，助理擁有者無法讀取對話內容。\",\"suggestedPrompts\":[],\"messages\":[{\"id\":\"01a11244-3646-70a7-a00d-7f62dd6ea2a8\",\"author\":\"account\",\"text\":\"二號冷藏庫溫度降不下來，需要報修\",\"reply\":null,\"createdAt\":\"2026-10-06T17:30:22.653627+00:00\"},{\"id\":\"01a11244-367c-7913-b6b8-4f168236d058\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"二號冷藏庫溫度降不下來，需要報修\",\"description\":\"\",\"status\":\"proposed\",\"available\":true,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":null}},\"createdAt\":\"2026-10-06T17:30:22.716121+00:00\"}]}";

/** `confirm-blank-422`（HTTP 422） */
const REAL_CASE_PROPOSAL_CONFIRM_BLANK_422_JSON =
  "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.21\",\"title\":\"Unprocessable Content\",\"status\":422,\"message\":\"請輸入案件標題。\",\"errors\":{\"title\":[\"請輸入案件標題。\"]}}";

/** `confirm`（HTTP 200） */
const REAL_CASE_PROPOSAL_CONFIRMED_JSON =
  "{\"id\":\"01a11244-367c-7913-b6b8-4f168236d058\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"二號冷藏庫溫度異常\",\"description\":\"請今天派人檢查壓縮機。\",\"status\":\"confirmed\",\"available\":false,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":\"01a11244-36b3-734d-bd3d-38f03344bef0\"}},\"createdAt\":\"2026-10-06T17:30:22.716121+00:00\"}";

/** `confirm-again-409`（HTTP 409） */
const REAL_CASE_PROPOSAL_CONFIRM_AGAIN_409_JSON =
  "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.10\",\"title\":\"Conflict\",\"status\":409,\"reason\":\"case-proposal-closed\",\"message\":\"這個提議已經處理過了：已建立案件，或已選擇不用了。\"}";

/** `dismiss`（HTTP 200） */
const REAL_CASE_PROPOSAL_DISMISSED_JSON =
  "{\"id\":\"01a11244-36e7-7920-b2a8-d40a9e091355\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"灌溉馬達有異音，請安排維修\",\"description\":\"\",\"status\":\"dismissed\",\"available\":false,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":null}},\"createdAt\":\"2026-10-06T17:30:22.823408+00:00\"}";

/** `confirm-removed-type-422`（HTTP 422） */
const REAL_CASE_PROPOSAL_CONFIRM_REMOVED_TYPE_422_JSON =
  "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.21\",\"title\":\"Unprocessable Content\",\"status\":422,\"reason\":\"case-type-not-proposable\",\"message\":\"這個案件類型已停用，或助理已不再提議這個類型，無法建立案件。\",\"errors\":{\"typeId\":[\"這個案件類型已停用，或助理已不再提議這個類型，無法建立案件。\"]}}";

/** `chat-after`（HTTP 200） */
const REAL_CASE_PROPOSAL_CHAT_AFTER_JSON =
  "{\"assistantId\":\"01a11242-0000-7000-8000-000000000254\",\"assistantName\":\"設備小幫手\",\"purpose\":\"協助處理設備問題\",\"threadId\":\"01a11244-363e-7e20-a4e1-0f7b70cec133\",\"title\":\"二號冷藏庫溫度降不下來，需要報修\",\"historyMode\":\"saved\",\"welcome\":\"你好，我是「設備小幫手」，協助處理設備問題有什麼我可以幫忙的嗎？\",\"privacyNotice\":\"只有你自己看得到這裡的對話紀錄，助理擁有者無法讀取對話內容。\",\"suggestedPrompts\":[],\"messages\":[{\"id\":\"01a11244-3646-70a7-a00d-7f62dd6ea2a8\",\"author\":\"account\",\"text\":\"二號冷藏庫溫度降不下來，需要報修\",\"reply\":null,\"createdAt\":\"2026-10-06T17:30:22.653627+00:00\"},{\"id\":\"01a11244-367c-7913-b6b8-4f168236d058\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"二號冷藏庫溫度異常\",\"description\":\"請今天派人檢查壓縮機。\",\"status\":\"confirmed\",\"available\":false,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":\"01a11244-36b3-734d-bd3d-38f03344bef0\"}},\"createdAt\":\"2026-10-06T17:30:22.716121+00:00\"},{\"id\":\"01a11244-36e4-763b-ab79-cf06137d0256\",\"author\":\"account\",\"text\":\"灌溉馬達有異音，請安排維修\",\"reply\":null,\"createdAt\":\"2026-10-06T17:30:22.820128+00:00\"},{\"id\":\"01a11244-36e7-7920-b2a8-d40a9e091355\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"灌溉馬達有異音，請安排維修\",\"description\":\"\",\"status\":\"dismissed\",\"available\":false,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":null}},\"createdAt\":\"2026-10-06T17:30:22.823408+00:00\"},{\"id\":\"01a11244-36f2-74e1-97ff-3f7295f9c42e\",\"author\":\"account\",\"text\":\"溫室風扇停了，需要報修\",\"reply\":null,\"createdAt\":\"2026-10-06T17:30:22.834154+00:00\"},{\"id\":\"01a11244-36f4-7306-98bc-4620fb87fdbf\",\"author\":\"assistant\",\"text\":null,\"reply\":{\"kind\":\"case-proposal\",\"text\":\"這件事可以開一件「設備故障報修」案件，請確認內容。\",\"citations\":[],\"notice\":null,\"nextSteps\":[],\"form\":null,\"receipt\":null,\"databaseQuery\":null,\"caseProposal\":{\"typeId\":\"01a11243-3917-7d25-822d-7640177f1370\",\"typeName\":\"設備故障報修\",\"title\":\"溫室風扇停了，需要報修\",\"description\":\"\",\"status\":\"proposed\",\"available\":false,\"group\":{\"id\":\"01a11243-38d6-7a4c-a0a6-411c8460ba3c\",\"name\":\"設備組\",\"archived\":false},\"dueHours\":72,\"caseId\":null}},\"createdAt\":\"2026-10-06T17:30:22.836618+00:00\"}]}";

function setUp() {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin', chatViewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '01a11242-c2e8-712b-a321-d789426fb262',
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-assistants', 'use-shared-assistants'],
        organizationId: '01a11242-c2ae-7d7b-947f-4174b2b91993',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

function respond(controller: HttpTestingController, method: string, url: string, json: string, status = 200) {
  const request = controller.expectOne({ method, url });
  request.flush(JSON.parse(json), { status, statusText: String(status) });
  return request.request;
}

describe('HybridDemoRepository case proposals (issue #254, recorded API responses)', () => {
  it('reads a case proposal from GET chat with the type, group and handling time as the server checked them', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getAssistantChat(ASSISTANT_ID, THREAD_ID));
    respond(controller, 'GET', apiAssistantChatPath(ASSISTANT_ID, THREAD_ID), REAL_CASE_PROPOSAL_CHAT_JSON);

    const view = await result;
    if (view.status !== 'ready') throw new Error(view.status);
    const reply = view.data.messages[1];
    expect(reply).toMatchObject({
      id: MESSAGE_ID,
      author: 'assistant',
      reply: {
        kind: 'case-proposal',
        text: '這件事可以開一件「設備故障報修」案件，請確認內容。',
        proposal: {
          typeId: TYPE_ID, typeName: '設備故障報修', title: '二號冷藏庫溫度降不下來，需要報修', description: '',
          status: 'proposed', available: true, group: { name: '設備組', archived: false }, dueHours: 72, caseId: null,
        },
      },
    });
    controller.verify();
  });

  it('re-reads every proposal on the next GET chat: confirmed, dismissed, and unavailable after the type was removed', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getAssistantChat(ASSISTANT_ID, THREAD_ID));
    respond(controller, 'GET', apiAssistantChatPath(ASSISTANT_ID, THREAD_ID), REAL_CASE_PROPOSAL_CHAT_AFTER_JSON);

    const view = await result;
    if (view.status !== 'ready') throw new Error(view.status);
    const proposals = view.data.messages.flatMap((message) =>
      message.author === 'assistant' && message.reply.kind === 'case-proposal' ? [[message.id, message.reply.proposal]] as const : []);
    expect(proposals.map(([id, proposal]) => [id, proposal?.status, proposal?.available])).toEqual([
      [MESSAGE_ID, 'confirmed', false],
      [DISMISSED_ID, 'dismissed', false],
      [REMOVED_ID, 'proposed', false],
    ]);
    expect(proposals[0]?.[1]?.caseId).toEqual(expect.any(String));
    controller.verify();
  });

  it('confirms with the title and description the asker edited and returns the proposal with its case', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT_ID, MESSAGE_ID, {
      title: '二號冷藏庫溫度異常', description: '請今天派人檢查壓縮機。',
    }));
    const request = respond(controller, 'POST', apiChatCaseProposalPath(ASSISTANT_ID, MESSAGE_ID, 'confirm'), REAL_CASE_PROPOSAL_CONFIRMED_JSON);

    expect(request.url).toBe(`/api/v1/assistants/${ASSISTANT_ID}/chat/case-proposals/${MESSAGE_ID}:confirm`);
    expect(request.body).toEqual({ title: '二號冷藏庫溫度異常', description: '請今天派人檢查壓縮機。' });
    const confirmed = await result;
    if (confirmed.status !== 'ready') throw new Error(confirmed.status);
    expect(confirmed.data).toMatchObject({
      id: MESSAGE_ID,
      reply: { kind: 'case-proposal', proposal: { status: 'confirmed', available: false, title: '二號冷藏庫溫度異常', description: '請今天派人檢查壓縮機。' } },
    });
    expect(confirmed.data.author === 'assistant' && confirmed.data.reply.kind === 'case-proposal' && confirmed.data.reply.proposal?.caseId)
      .toMatch(/^[0-9a-f-]{36}$/);
    controller.verify();
  });

  it('maps a blank title, a second confirmation and a removed type to their results', async () => {
    let setup = setUp();
    let result = firstValueFrom(setup.repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT_ID, MESSAGE_ID, { title: '  ', description: '' }));
    respond(setup.controller, 'POST', apiChatCaseProposalPath(ASSISTANT_ID, MESSAGE_ID, 'confirm'), REAL_CASE_PROPOSAL_CONFIRM_BLANK_422_JSON, 422);
    expect(await result).toEqual({ status: 'validation-failed', reason: null, message: '請輸入案件標題。', fieldErrors: { title: '請輸入案件標題。' } });

    setup = setUp();
    result = firstValueFrom(setup.repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT_ID, MESSAGE_ID, { title: '再一次', description: '' }));
    respond(setup.controller, 'POST', apiChatCaseProposalPath(ASSISTANT_ID, MESSAGE_ID, 'confirm'), REAL_CASE_PROPOSAL_CONFIRM_AGAIN_409_JSON, 409);
    expect(await result).toEqual({ status: 'conflict', message: '這個提議已經處理過了：已建立案件，或已選擇不用了。' });

    setup = setUp();
    result = firstValueFrom(setup.repository.confirmChatCaseProposal('account-smb-admin', ASSISTANT_ID, REMOVED_ID, { title: '溫室風扇停了', description: '' }));
    respond(setup.controller, 'POST', apiChatCaseProposalPath(ASSISTANT_ID, REMOVED_ID, 'confirm'), REAL_CASE_PROPOSAL_CONFIRM_REMOVED_TYPE_422_JSON, 422);
    expect(await result).toEqual({
      status: 'validation-failed',
      reason: 'case-type-not-proposable',
      message: '這個案件類型已停用，或助理已不再提議這個類型，無法建立案件。',
      fieldErrors: {},
    });
    setup.controller.verify();
  });

  it('dismisses without creating anything', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.dismissChatCaseProposal('account-smb-admin', ASSISTANT_ID, DISMISSED_ID));
    const request = respond(controller, 'POST', apiChatCaseProposalPath(ASSISTANT_ID, DISMISSED_ID, 'dismiss'), REAL_CASE_PROPOSAL_DISMISSED_JSON);

    expect(request.url).toBe(`/api/v1/assistants/${ASSISTANT_ID}/chat/case-proposals/${DISMISSED_ID}:dismiss`);
    const dismissed = await result;
    if (dismissed.status !== 'ready') throw new Error(dismissed.status);
    expect(dismissed.data).toMatchObject({ id: DISMISSED_ID, reply: { kind: 'case-proposal', proposal: { status: 'dismissed', available: false, caseId: null } } });
    controller.verify();
  });

  it('adds and removes a proposable case type, and refuses an inactive one on its own field', async () => {
    let setup = setUp();
    let result = firstValueFrom(setup.repository.setAssistantCaseType(ASSISTANT_ID, TYPE_ID, true));
    respond(setup.controller, 'PUT', apiAssistantCaseTypeSourcePath(ASSISTANT_ID, TYPE_ID), REAL_CASE_PROPOSAL_SETTINGS_ADDED_JSON);
    let settings = await result;
    if (settings.status !== 'ready') throw new Error(settings.status);
    expect(settings.data.caseTypeIds).toEqual([TYPE_ID]);

    setup = setUp();
    result = firstValueFrom(setup.repository.setAssistantCaseType(ASSISTANT_ID, TYPE_ID, false));
    respond(setup.controller, 'DELETE', apiAssistantCaseTypeSourcePath(ASSISTANT_ID, TYPE_ID), REAL_CASE_PROPOSAL_SETTINGS_REMOVED_JSON);
    settings = await result;
    if (settings.status !== 'ready') throw new Error(settings.status);
    expect(settings.data.caseTypeIds).toEqual([]);

    setup = setUp();
    const inactive = '01a11243-0000-7000-8000-00000000dead';
    result = firstValueFrom(setup.repository.setAssistantCaseType(ASSISTANT_ID, inactive, true));
    respond(setup.controller, 'PUT', apiAssistantCaseTypeSourcePath(ASSISTANT_ID, inactive), REAL_CASE_PROPOSAL_SETTINGS_ADD_INACTIVE_422_JSON, 422);
    expect(await result).toEqual({
      status: 'validation-failed',
      errors: [{ field: 'caseTypeIds', message: '這個案件類型已停用或不存在，請選擇其他類型。' }],
      message: '這個案件類型已停用或不存在，請選擇其他類型。',
    });
    setup.controller.verify();
  });

  it('maps the streamed reply exactly like the saved one', async () => {
    const { toAdminChatReply } = await import('./hybrid-demo-repository');
    const streamed = JSON.parse(REAL_CASE_PROPOSAL_RUN_REPLY_JSON);
    expect(toAdminChatReply(streamed.reply)).toMatchObject({ kind: 'case-proposal', proposal: { status: 'proposed', available: true } });
  });
});
