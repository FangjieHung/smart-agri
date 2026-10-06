import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { AssistantIssueDetailView, AssistantIssueOpenedCaseView, AssistantIssueView, OpenCaseFromIssueRequest } from '../domain/assistant-issue.model';
import type { CaseDetailView } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_ISSUES_PATH, apiIssueOpenCasePath, apiIssuePath, AssistantIssuesRepository } from './assistant-issues.repository';
import { apiCasePath, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #252): `dotnet run` of this branch on port 5263 against a
// throw-away PostgreSQL database `m7_252` (migrated, development seed `anxin`, plus — inserted with psql —
// one assistant `設備助理` owned by `admin` that keeps no conversations, and `handle-assistant-issues` for
// `internal`). Three issues were opened through POST …/chat/handoffs as `admin`; the second was assigned
// to `internal`; 設備組 (member: `internal`) and the types 設備故障報修 / 已停用的類型 through the case
// settings API. Signed in as `admin`, `internal` and `customer` through /connect/authorize + /connect/token
// by `scratchpad/252/record.py`. The bodies are pasted unchanged; never edit them to match the types.
/** admin-open-case: HTTP 201. */
const OPEN_CASE_JSON = `{"caseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14","issue":{"issue":{"id":"01a112c7-cf5e-7423-90f0-08b67e82546e","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","assigneeAccountId":null,"assigneeDisplayName":null,"reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.07074+00:00","updatedAt":"2026-10-06T19:54:07.184644+00:00","resolvedAt":"2026-10-06T19:54:07.184644+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14"},"events":[{"id":"01a112c7-cf5f-7501-8010-c597260b49b9","action":"created","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.07074+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"open","dueAt":null},{"id":"01a112c7-cfd4-7f44-8903-0762ca540fd7","action":"case-opened","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.184644+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"resolved","dueAt":null}],"linkedCase":{"caseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14","canOpen":true}}}`;
/** admin-open-case-again-409: HTTP 409. */
const AGAIN_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"case-already-opened","message":"這個處理事項已經另開過案件。","caseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14"}`;
/** admin-open-case-resolved-409: HTTP 409. */
const RESOLVED_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"issue-changed","message":"這個處理事項剛被其他人更新，請重新整理後再試。"}`;
/** admin-open-case-inactive-type-422: HTTP 422. */
const INACTIVE_TYPE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-type-inactive","message":"這個案件類型已停用或不存在，請選擇其他類型。","errors":{"typeId":["這個案件類型已停用或不存在，請選擇其他類型。"]}}`;
/** internal-open-case-not-visible-403: HTTP 403. */
const NOT_VISIBLE_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-issue","message":"你沒有這個處理事項的存取權限，或它已不存在。"}`;
/** customer-open-case-403: HTTP 403. */
const CUSTOMER_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-issue","message":"你沒有這個處理事項的存取權限，或它已不存在。"}`;
/** admin-issue-detail: HTTP 200. */
const ISSUE_DETAIL_JSON = `{"issue":{"id":"01a112c7-cf5e-7423-90f0-08b67e82546e","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","assigneeAccountId":null,"assigneeDisplayName":null,"reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.07074+00:00","updatedAt":"2026-10-06T19:54:07.184644+00:00","resolvedAt":"2026-10-06T19:54:07.184644+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14"},"events":[{"id":"01a112c7-cf5f-7501-8010-c597260b49b9","action":"created","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.07074+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"open","dueAt":null},{"id":"01a112c7-cfd4-7f44-8903-0762ca540fd7","action":"case-opened","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.184644+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"resolved","dueAt":null}],"linkedCase":{"caseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14","canOpen":true}}`;
/** internal-open-case: HTTP 201. */
const INTERNAL_OPEN_CASE_JSON = `{"caseId":"01a112c7-d04b-7524-a07b-c0180dada278","issue":{"issue":{"id":"01a112c7-cf90-7606-9462-fccb34018c2f","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"二號溫室的灌溉馬達有異音，需要安排維修。","assigneeAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","assigneeDisplayName":"安心商行客服同仁","reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"二號溫室的灌溉馬達有異音，需要安排維修。","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.120788+00:00","updatedAt":"2026-10-06T19:54:07.306553+00:00","resolvedAt":"2026-10-06T19:54:07.306553+00:00","viewerIsAssistantOwner":false,"viewerIsAssignee":true,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-d04b-7524-a07b-c0180dada278"},"events":[{"id":"01a112c7-cf90-7cca-a3de-88ced76875ca","action":"created","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.120788+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"open","dueAt":null},{"id":"01a112c7-cfa2-758f-acd3-044f1fdaa6d2","action":"assigned","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.138782+00:00","note":null,"assigneeAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","assigneeDisplayName":"安心商行客服同仁","status":null,"dueAt":null},{"id":"01a112c7-d04b-7799-911c-1bceaf13bc6f","action":"case-opened","actorAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","actorDisplayName":"安心商行客服同仁","at":"2026-10-06T19:54:07.306553+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"resolved","dueAt":null}],"linkedCase":{"caseId":"01a112c7-d04b-7524-a07b-c0180dada278","canOpen":true}}}`;
/** admin-issue2-detail: HTTP 200. */
const ISSUE2_DETAIL_ADMIN_JSON = `{"issue":{"id":"01a112c7-cf90-7606-9462-fccb34018c2f","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"二號溫室的灌溉馬達有異音，需要安排維修。","assigneeAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","assigneeDisplayName":"安心商行客服同仁","reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"二號溫室的灌溉馬達有異音，需要安排維修。","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.120788+00:00","updatedAt":"2026-10-06T19:54:07.306553+00:00","resolvedAt":"2026-10-06T19:54:07.306553+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-d04b-7524-a07b-c0180dada278"},"events":[{"id":"01a112c7-cf90-7cca-a3de-88ced76875ca","action":"created","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.120788+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"open","dueAt":null},{"id":"01a112c7-cfa2-758f-acd3-044f1fdaa6d2","action":"assigned","actorAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","actorDisplayName":"安心商行管理者","at":"2026-10-06T19:54:07.138782+00:00","note":null,"assigneeAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","assigneeDisplayName":"安心商行客服同仁","status":null,"dueAt":null},{"id":"01a112c7-d04b-7799-911c-1bceaf13bc6f","action":"case-opened","actorAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","actorDisplayName":"安心商行客服同仁","at":"2026-10-06T19:54:07.306553+00:00","note":null,"assigneeAccountId":null,"assigneeDisplayName":null,"status":"resolved","dueAt":null}],"linkedCase":{"caseId":"01a112c7-d04b-7524-a07b-c0180dada278","canOpen":true}}`;
/** admin-issue-list: HTTP 200. */
const ISSUE_LIST_JSON = `[{"id":"01a112c7-cf96-71e8-830a-6237720009f1","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"退貨運費由誰負擔？","assigneeAccountId":null,"assigneeDisplayName":null,"reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"退貨運費由誰負擔？","answer":"請參考冷藏庫的保養說明。","resolutionNote":"已補上退貨說明","createdAt":"2026-10-06T19:54:07.126153+00:00","updatedAt":"2026-10-06T19:54:07.323101+00:00","resolvedAt":"2026-10-06T19:54:07.323101+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"fixed","linkedCaseId":null},{"id":"01a112c7-cf90-7606-9462-fccb34018c2f","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"二號溫室的灌溉馬達有異音，需要安排維修。","assigneeAccountId":"01a112c6-c5e0-783f-a6d5-5af1bc2a6be1","assigneeDisplayName":"安心商行客服同仁","reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"二號溫室的灌溉馬達有異音，需要安排維修。","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.120788+00:00","updatedAt":"2026-10-06T19:54:07.306553+00:00","resolvedAt":"2026-10-06T19:54:07.306553+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-d04b-7524-a07b-c0180dada278"},{"id":"01a112c7-cf5e-7423-90f0-08b67e82546e","assistantId":"0199f000-0000-7000-8000-0000000a0252","assistantName":"設備助理","source":"handoff","status":"resolved","title":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","assigneeAccountId":null,"assigneeDisplayName":null,"reporterAccountId":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","reporterDisplayName":"安心商行管理者","dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","answer":"請參考冷藏庫的保養說明。","resolutionNote":null,"createdAt":"2026-10-06T19:54:07.07074+00:00","updatedAt":"2026-10-06T19:54:07.184644+00:00","resolvedAt":"2026-10-06T19:54:07.184644+00:00","viewerIsAssistantOwner":true,"viewerIsAssignee":false,"handoffUnverified":true,"resolutionKind":"not-assistant-issue","linkedCaseId":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14"}]`;
/** admin-case-detail: HTTP 200. */
const CASE_DETAIL_ADMIN_JSON = `{"case":{"id":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14","title":"派人檢查冷藏庫","description":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","status":"pending","origin":"assistant-issue","type":{"id":"01a112c7-cf35-7147-8bb9-ef0134f2a7ec","name":"設備故障報修"},"group":{"id":"01a112c7-cecc-7bc4-b2b7-138eca69a25f","name":"設備組","archived":false},"createdBy":{"id":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-09T19:54:07+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T19:54:07.184644+00:00","updatedAt":"2026-10-06T19:54:07.184644+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a112c7-cfd4-71cd-abab-136df67fb68b","ordinal":1,"action":"created","actor":{"id":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","displayName":"安心商行管理者"},"at":"2026-10-06T19:54:07.184644+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a112c7-cecc-7bc4-b2b7-138eca69a25f","name":"設備組","archived":false},"dueAt":"2026-10-09T19:54:07+00:00"}],"links":{"record":null,"thread":null,"assistantIssue":{"issueId":"01a112c7-cf5e-7423-90f0-08b67e82546e","canOpen":true},"previousCase":null},"allowedActions":["cancel","transfer","comment"],"cancelReasonRequired":false}`;
/** internal-case-detail: HTTP 200. */
const CASE_DETAIL_INTERNAL_JSON = `{"case":{"id":"01a112c7-cfd3-7f3d-8a75-b3b8d812af14","title":"派人檢查冷藏庫","description":"冷藏庫的溫度一直降不下來，可以派人來看嗎？","status":"pending","origin":"assistant-issue","type":{"id":"01a112c7-cf35-7147-8bb9-ef0134f2a7ec","name":"設備故障報修"},"group":{"id":"01a112c7-cecc-7bc4-b2b7-138eca69a25f","name":"設備組","archived":false},"createdBy":{"id":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-09T19:54:07+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T19:54:07.184644+00:00","updatedAt":"2026-10-06T19:54:07.184644+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a112c7-cfd4-71cd-abab-136df67fb68b","ordinal":1,"action":"created","actor":{"id":"01a112c6-c5a6-7c59-bc96-e16b1d35d8e6","displayName":"安心商行管理者"},"at":"2026-10-06T19:54:07.184644+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a112c7-cecc-7bc4-b2b7-138eca69a25f","name":"設備組","archived":false},"dueAt":"2026-10-09T19:54:07+00:00"}],"links":{"record":null,"thread":null,"assistantIssue":{"issueId":"01a112c7-cf5e-7423-90f0-08b67e82546e","canOpen":false},"previousCase":null},"allowedActions":["accept"],"cancelReasonRequired":true}`;

function apiRepositories() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return {
    issues: TestBed.inject(AssistantIssuesRepository),
    cases: TestBed.inject(CasesRepository),
    http: TestBed.inject(HttpTestingController),
  };
}

function flushError(http: HttpTestingController, url: string, status: number, statusText: string, body: string): void {
  http.expectOne((request) => request.url === url && request.method === 'POST').flush(JSON.parse(body), { status, statusText });
}

const opened = JSON.parse(OPEN_CASE_JSON) as AssistantIssueOpenedCaseView;
const issueId = opened.issue.issue.id;
const openedCase = (JSON.parse(CASE_DETAIL_ADMIN_JSON) as CaseDetailView).case;
const request: OpenCaseFromIssueRequest = {
  typeId: openedCase.type.id,
  groupId: openedCase.group.id,
  dueAt: openedCase.dueAt,
  title: openedCase.title,
  description: openedCase.description,
};
const OPTIONS = { activeTypeIds: [], groupIds: [] };

describe('AssistantIssuesRepository.openCase (API mode, recorded responses)', () => {
  it('posts the case fields to :open-case and returns the case id with the issue resolved as 非助理問題', async () => {
    const { issues, http } = apiRepositories();
    const result = firstValueFrom(issues.openCase(issueId, request, OPTIONS));
    const call = http.expectOne(apiIssueOpenCasePath(issueId));
    expect(call.request.method).toBe('POST');
    expect(call.request.url).toBe(`/api/v1/issues/${issueId}:open-case`);
    expect(call.request.body).toEqual(request);
    call.flush(JSON.parse(OPEN_CASE_JSON), { status: 201, statusText: 'Created' });

    const view = await result;
    if (view.status !== 'ready') throw new Error(view.status);
    expect(view.data.caseId).toBe(opened.caseId);
    expect(view.data.issue.issue).toMatchObject({ status: 'resolved', resolutionKind: 'not-assistant-issue', linkedCaseId: opened.caseId });
    expect(view.data.issue.linkedCase).toEqual({ caseId: opened.caseId, canOpen: true });
    expect(view.data.issue.events.at(-1)).toMatchObject({ action: 'case-opened', status: 'resolved' });
    http.verify();
  });

  it('turns a second 另開案件 into a conflict carrying the existing case, and a resolved issue into issue-changed', async () => {
    const { issues, http } = apiRepositories();
    const again = firstValueFrom(issues.openCase(issueId, request, OPTIONS));
    flushError(http, apiIssueOpenCasePath(issueId), 409, 'Conflict', AGAIN_409_JSON);
    expect(await again).toEqual({
      status: 'conflict', reason: 'case-already-opened', message: '這個處理事項已經另開過案件。', caseId: opened.caseId,
    });

    const resolved = firstValueFrom(issues.openCase('issue-3', request, OPTIONS));
    flushError(http, apiIssueOpenCasePath('issue-3'), 409, 'Conflict', RESOLVED_409_JSON);
    expect(await resolved).toEqual({
      status: 'conflict', reason: 'issue-changed', message: '這個處理事項剛被其他人更新，請重新整理後再試。',
    });
    http.verify();
  });

  it('keeps the reason and field error of a 422, and maps a 403 to permission-denied', async () => {
    const { issues, http } = apiRepositories();
    const inactive = firstValueFrom(issues.openCase(issueId, request, OPTIONS));
    flushError(http, apiIssueOpenCasePath(issueId), 422, 'Unprocessable Content', INACTIVE_TYPE_422_JSON);
    expect(await inactive).toEqual({
      status: 'validation-failed', reason: 'case-type-inactive', message: '這個案件類型已停用或不存在，請選擇其他類型。',
      fieldErrors: { typeId: '這個案件類型已停用或不存在，請選擇其他類型。' },
    });

    for (const body of [NOT_VISIBLE_403_JSON, CUSTOMER_403_JSON]) {
      const denied = firstValueFrom(issues.openCase(issueId, request, OPTIONS));
      flushError(http, apiIssueOpenCasePath(issueId), 403, 'Forbidden', body);
      expect(await denied).toEqual({
        status: 'permission-denied', reason: 'assistant-issue', message: '你沒有這個處理事項的存取權限，或它已不存在。',
      });
    }
    http.verify();
  });

  it('reads the linked case of an issue detail and the resolution kind in the list', async () => {
    const { issues, http } = apiRepositories();
    const detail = firstValueFrom(issues.get(issueId));
    http.expectOne(apiIssuePath(issueId)).flush(JSON.parse(ISSUE_DETAIL_JSON));
    const view = await detail;
    if (view.status !== 'ready') throw new Error(view.status);
    expect(view.data.linkedCase).toEqual({ caseId: opened.caseId, canOpen: true });
    expect(view.data.issue.resolutionKind).toBe('not-assistant-issue');

    const handled = JSON.parse(ISSUE2_DETAIL_ADMIN_JSON) as AssistantIssueDetailView;
    expect(handled.linkedCase?.canOpen).toBe(true);
    expect((JSON.parse(INTERNAL_OPEN_CASE_JSON) as AssistantIssueOpenedCaseView).caseId).toBe(handled.linkedCase?.caseId);

    const list = firstValueFrom(issues.list());
    http.expectOne((call) => call.url === API_ISSUES_PATH).flush(JSON.parse(ISSUE_LIST_JSON));
    const rows = await list;
    if (rows.status !== 'ready') throw new Error(rows.status);
    const kinds = new Map(rows.data.map((row: AssistantIssueView) => [row.id, row.resolutionKind]));
    expect(kinds.get(issueId)).toBe('not-assistant-issue');
    expect(new Set(kinds.values())).toEqual(new Set(['not-assistant-issue', 'fixed']));
    http.verify();
  });

  it('shows the case\'s source issue as openable only to someone who may open the issue', async () => {
    const { cases, http } = apiRepositories();
    for (const [body, canOpen] of [[CASE_DETAIL_ADMIN_JSON, true], [CASE_DETAIL_INTERNAL_JSON, false]] as const) {
      const detail = firstValueFrom(cases.get(opened.caseId));
      http.expectOne(apiCasePath(opened.caseId)).flush(JSON.parse(body));
      const view = await detail;
      if (view.status !== 'ready') throw new Error(view.status);
      const data: CaseDetailView = view.data;
      expect(data.case.origin).toBe('assistant-issue');
      expect(data.links.assistantIssue).toEqual({ issueId, canOpen });
    }
    http.verify();
  });
});
