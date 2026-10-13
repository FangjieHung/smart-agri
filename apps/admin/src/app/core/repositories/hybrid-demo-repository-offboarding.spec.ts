import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  apiMemberDeactivatePath,
  apiMemberOffboardingPath,
  apiMemberReactivatePath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const ADMIN_ID = '01a134c4-a7d7-70a8-a582-d9fd4fd594ff';
const MEMBER_ID = '01a134c4-a815-7d05-917b-bdcbc73368b8';

/*
 * 以下回應都是 2026-10-13 從本機 API（Development、暫用的資料庫，`migrate` 的開發 seed）實際取得（issue #356）：
 * 管理者先讓客服同仁具備「管理資料來源」、客服同仁建立一個知識庫，管理者試著停用自己（422）、
 * 停用客服同仁並指定自己接手、再重新啟用（那次的停用預覽在 #361 加了 `successorRequired`，改用下面重錄的）。原樣保留成字串再 `JSON.parse`，不是依產生的型別手寫。
 */
const REAL_DEACTIVATE_SELF_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"cannot-deactivate-self","message":"不能停用自己的帳號。","errors":{"memberId":["不能停用自己的帳號。"]}}`;
const REAL_DEACTIVATE_JSON = `{"team":{"members":[{"id":"01a134c4-a7d7-70a8-a582-d9fd4fd594ff","displayName":"安心商行管理者","role":"smb-admin","permissions":["manage-assistants","manage-data-sources","manage-publishing","read-consented-submissions"],"lockedPermissions":["manage-assistants"],"deactivatedAt":null},{"id":"01a134c4-a815-7d05-917b-bdcbc73368b8","displayName":"安心商行客服同仁","role":"internal-employee","permissions":["manage-data-sources","read-consented-submissions","use-shared-assistants"],"lockedPermissions":[],"deactivatedAt":"2026-10-13T10:18:25.993171+00:00"},{"id":"01a134c4-a837-7127-b974-5e8aa17a6484","displayName":"外部客戶","role":"external-customer","permissions":["submit-authorized-forms","use-shared-assistants","read-own-tracking"],"lockedPermissions":[],"deactivatedAt":null}],"savedAt":"2026-10-13T10:18:25.784736+00:00"},"transferred":{"assistants":0,"drafts":0,"knowledgeBases":1,"databases":0,"issuesReassigned":0,"issuesUnassigned":0,"cases":0}}`;
/*
 * 2026-10-14 從本機 API（Development、暫用的資料庫，`migrate` 的開發 seed）實際取得（issue #361，帳號 id 與上面那次不同）：
 * 管理者讓客服同仁具備「管理助理與團隊」「管理資料來源」、客服同仁建立一個知識庫；管理者讀客服同仁（名下有東西）與
 * 外部客戶（名下沒有東西）的停用預覽、不選接手人停用客服同仁（422）；客服同仁讀管理者的預覽、試著停用管理者（422）；
 * 管理者不選接手人停用外部客戶。原樣保留成字串再 `JSON.parse`，不是依產生的型別手寫。
 */
const R_ADMIN_ID = '01a13623-04bc-7a70-b386-0c0c67627152';
const R_MEMBER_ID = '01a13623-0507-722c-9fb8-42713b6c1d9e';
const R_CUSTOMER_ID = '01a13623-0530-782c-9eac-7631bd53bebd';
const REAL_OFFBOARDING_HOLDING_JSON = `{"memberId":"01a13623-0507-722c-9fb8-42713b6c1d9e","displayName":"安心商行客服同仁","block":null,"blockMessage":null,"holdings":{"assistants":0,"drafts":0,"knowledgeBases":1,"databases":0,"openIssues":0,"openCases":0,"borrowedSources":0},"requiredPermissions":["manage-data-sources"],"successorRequired":true,"candidates":[{"id":"01a13623-04bc-7a70-b386-0c0c67627152","displayName":"安心商行管理者","role":"smb-admin","eligible":true,"ineligibleReason":null,"missingPermissions":[],"canHandleIssues":false},{"id":"01a13623-0530-782c-9eac-7631bd53bebd","displayName":"外部客戶","role":"external-customer","eligible":false,"ineligibleReason":"external-customer","missingPermissions":[],"canHandleIssues":false}]}`;
const REAL_OFFBOARDING_NOTHING_JSON = `{"memberId":"01a13623-0530-782c-9eac-7631bd53bebd","displayName":"外部客戶","block":null,"blockMessage":null,"holdings":{"assistants":0,"drafts":0,"knowledgeBases":0,"databases":0,"openIssues":0,"openCases":0,"borrowedSources":0},"requiredPermissions":[],"successorRequired":false,"candidates":[{"id":"01a13623-04bc-7a70-b386-0c0c67627152","displayName":"安心商行管理者","role":"smb-admin","eligible":true,"ineligibleReason":null,"missingPermissions":[],"canHandleIssues":false},{"id":"01a13623-0507-722c-9fb8-42713b6c1d9e","displayName":"安心商行客服同仁","role":"internal-employee","eligible":true,"ineligibleReason":null,"missingPermissions":[],"canHandleIssues":false}]}`;
const REAL_DEACTIVATE_SUCCESSOR_REQUIRED_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"successor-not-eligible","message":"請選擇接手人。","errors":{"successorAccountId":["請選擇接手人。"]}}`;
const REAL_OFFBOARDING_ADMIN_REQUIRED_JSON = `{"memberId":"01a13623-04bc-7a70-b386-0c0c67627152","displayName":"安心商行管理者","block":"admin-required","blockMessage":"只有管理者能停用或重新啟用另一位管理者。","holdings":{"assistants":0,"drafts":0,"knowledgeBases":0,"databases":0,"openIssues":0,"openCases":0,"borrowedSources":0},"requiredPermissions":[],"successorRequired":false,"candidates":[{"id":"01a13623-0507-722c-9fb8-42713b6c1d9e","displayName":"安心商行客服同仁","role":"internal-employee","eligible":true,"ineligibleReason":null,"missingPermissions":[],"canHandleIssues":false},{"id":"01a13623-0530-782c-9eac-7631bd53bebd","displayName":"外部客戶","role":"external-customer","eligible":false,"ineligibleReason":"external-customer","missingPermissions":[],"canHandleIssues":false}]}`;
const REAL_DEACTIVATE_ADMIN_REQUIRED_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"admin-required","message":"只有管理者能停用或重新啟用另一位管理者。","errors":{"memberId":["只有管理者能停用或重新啟用另一位管理者。"]}}`;
const REAL_DEACTIVATE_NO_SUCCESSOR_JSON = `{"team":{"members":[{"id":"01a13623-04bc-7a70-b386-0c0c67627152","displayName":"安心商行管理者","role":"smb-admin","permissions":["manage-assistants","manage-data-sources","manage-publishing","read-consented-submissions"],"lockedPermissions":["manage-assistants"],"deactivatedAt":null},{"id":"01a13623-0507-722c-9fb8-42713b6c1d9e","displayName":"安心商行客服同仁","role":"internal-employee","permissions":["manage-assistants","manage-data-sources","read-consented-submissions","use-shared-assistants"],"lockedPermissions":[],"deactivatedAt":null},{"id":"01a13623-0530-782c-9eac-7631bd53bebd","displayName":"外部客戶","role":"external-customer","permissions":["submit-authorized-forms","use-shared-assistants","read-own-tracking"],"lockedPermissions":[],"deactivatedAt":"2026-10-13T16:40:45.928136+00:00"}],"savedAt":"2026-10-13T16:40:45.583379+00:00"},"transferred":{"assistants":0,"drafts":0,"knowledgeBases":0,"databases":0,"issuesReassigned":0,"issuesUnassigned":0,"cases":0}}`;
const REAL_REACTIVATE_JSON = `{"members":[{"id":"01a134c4-a7d7-70a8-a582-d9fd4fd594ff","displayName":"安心商行管理者","role":"smb-admin","permissions":["manage-assistants","manage-data-sources","manage-publishing","read-consented-submissions"],"lockedPermissions":["manage-assistants"],"deactivatedAt":null},{"id":"01a134c4-a815-7d05-917b-bdcbc73368b8","displayName":"安心商行客服同仁","role":"internal-employee","permissions":["manage-data-sources","read-consented-submissions","use-shared-assistants"],"lockedPermissions":[],"deactivatedAt":null},{"id":"01a134c4-a837-7127-b974-5e8aa17a6484","displayName":"外部客戶","role":"external-customer","permissions":["submit-authorized-forms","use-shared-assistants","read-own-tracking"],"lockedPermissions":[],"deactivatedAt":null}],"savedAt":"2026-10-13T10:18:25.784736+00:00"}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: ADMIN_ID,
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-assistants', 'manage-data-sources', 'manage-publishing', 'read-consented-submissions'],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

describe('HybridDemoRepository member offboarding (issue #356)', () => {
  it('reads the preview from the API: holdings, required permissions and every candidate with its reason', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getMemberOffboarding(R_MEMBER_ID));
    controller.expectOne({ method: 'GET', url: apiMemberOffboardingPath(R_MEMBER_ID) }).flush(JSON.parse(REAL_OFFBOARDING_HOLDING_JSON));

    expect(await result).toEqual({
      status: 'ready',
      data: {
        memberId: R_MEMBER_ID,
        displayName: '安心商行客服同仁',
        block: null,
        blockMessage: null,
        holdings: { assistants: 0, drafts: 0, knowledgeBases: 1, databases: 0, openIssues: 0, openCases: 0, borrowedSources: 0 },
        requiredPermissions: ['manage-data-sources'],
        successorRequired: true,
        candidates: [
          { id: R_ADMIN_ID, displayName: '安心商行管理者', role: 'smb-admin', eligible: true, ineligibleReason: null, missingPermissions: [], canHandleIssues: false },
          { id: R_CUSTOMER_ID, displayName: '外部客戶', role: 'external-customer', eligible: false, ineligibleReason: 'external-customer', missingPermissions: [], canHandleIssues: false },
        ],
      },
    });
    controller.verify();
  });

  it('posts the successor and returns the whole team with the member marked deactivated and what moved', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.deactivateMember(MEMBER_ID, ADMIN_ID));
    const request = controller.expectOne({ method: 'POST', url: apiMemberDeactivatePath(MEMBER_ID) });
    expect(request.request.body).toEqual({ successorAccountId: ADMIN_ID });
    request.flush(JSON.parse(REAL_DEACTIVATE_JSON));

    const outcome = await result;
    if (outcome.status !== 'ready') throw new Error(`expected ready, got ${outcome.status}`);
    expect(outcome.data.transferred).toEqual({
      assistants: 0, drafts: 0, knowledgeBases: 1, databases: 0, issuesReassigned: 0, issuesUnassigned: 0, cases: 0,
    });
    const member = outcome.data.team.members.find((candidate) => candidate.id === MEMBER_ID);
    expect(member?.deactivatedAt).toBe('2026-10-13T10:18:25.993171+00:00');
    expect(outcome.data.team.members.find((candidate) => candidate.id === ADMIN_ID)).toMatchObject({ isViewer: true, deactivatedAt: null });
    controller.verify();
  });

  it('turns the API’s 422 into validation-failed with the server’s message', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.deactivateMember(ADMIN_ID, MEMBER_ID));
    controller
      .expectOne({ method: 'POST', url: apiMemberDeactivatePath(ADMIN_ID) })
      .flush(JSON.parse(REAL_DEACTIVATE_SELF_422_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({ status: 'validation-failed', message: '不能停用自己的帳號。' });
  });

  it('reactivates and returns the team with the member back in use', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.reactivateMember(MEMBER_ID));
    controller.expectOne({ method: 'POST', url: apiMemberReactivatePath(MEMBER_ID) }).flush(JSON.parse(REAL_REACTIVATE_JSON));

    const outcome = await result;
    if (outcome.status !== 'ready') throw new Error(`expected ready, got ${outcome.status}`);
    expect(outcome.data.members.every((member) => member.deactivatedAt === null)).toBe(true);
  });

  it('gives the team permission-denied for the API’s 403, like the rest of the team panel', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getMemberOffboarding(MEMBER_ID));
    controller
      .expectOne({ method: 'GET', url: apiMemberOffboardingPath(MEMBER_ID) })
      .flush({ reason: 'team', message: '只有可管理助理與團隊的帳號可以查看團隊設定。' }, { status: 403, statusText: 'Forbidden' });

    expect((await result).status).toBe('permission-denied');
  });
});

describe('HybridDemoRepository member offboarding rules of issue #361', () => {
  it('reads that a member holding nothing needs no successor, and posts null for them', async () => {
    const { repository, controller } = setUp();
    const preview = firstValueFrom(repository.getMemberOffboarding(R_CUSTOMER_ID));
    controller.expectOne({ method: 'GET', url: apiMemberOffboardingPath(R_CUSTOMER_ID) }).flush(JSON.parse(REAL_OFFBOARDING_NOTHING_JSON));
    const view = await preview;
    if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
    expect(view.data.successorRequired).toBe(false);
    expect(view.data.holdings).toEqual({
      assistants: 0, drafts: 0, knowledgeBases: 0, databases: 0, openIssues: 0, openCases: 0, borrowedSources: 0,
    });

    const result = firstValueFrom(repository.deactivateMember(R_CUSTOMER_ID, null));
    const request = controller.expectOne({ method: 'POST', url: apiMemberDeactivatePath(R_CUSTOMER_ID) });
    expect(request.request.body).toEqual({ successorAccountId: null });
    request.flush(JSON.parse(REAL_DEACTIVATE_NO_SUCCESSOR_JSON));
    const outcome = await result;
    if (outcome.status !== 'ready') throw new Error(`expected ready, got ${outcome.status}`);
    expect(Object.values(outcome.data.transferred).every((count) => count === 0)).toBe(true);
    expect(outcome.data.team.members.find((member) => member.id === R_CUSTOMER_ID)?.deactivatedAt).toBe('2026-10-13T16:40:45.928136+00:00');
    controller.verify();
  });

  it('shows the API’s 請選擇接手人 when a member holding something is posted without one', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.deactivateMember(R_MEMBER_ID, null));
    controller
      .expectOne({ method: 'POST', url: apiMemberDeactivatePath(R_MEMBER_ID) })
      .flush(JSON.parse(REAL_DEACTIVATE_SUCCESSOR_REQUIRED_422_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({ status: 'validation-failed', message: '請選擇接手人。' });
  });

  it('reads admin-required for a manager previewed by a non-manager and shows the refusal', async () => {
    const { repository, controller } = setUp();
    const preview = firstValueFrom(repository.getMemberOffboarding(R_ADMIN_ID));
    controller.expectOne({ method: 'GET', url: apiMemberOffboardingPath(R_ADMIN_ID) }).flush(JSON.parse(REAL_OFFBOARDING_ADMIN_REQUIRED_JSON));
    expect(await preview).toMatchObject({
      status: 'ready',
      data: { block: 'admin-required', blockMessage: '只有管理者能停用或重新啟用另一位管理者。' },
    });

    const result = firstValueFrom(repository.deactivateMember(R_ADMIN_ID, R_MEMBER_ID));
    controller
      .expectOne({ method: 'POST', url: apiMemberDeactivatePath(R_ADMIN_ID) })
      .flush(JSON.parse(REAL_DEACTIVATE_ADMIN_REQUIRED_422_JSON), { status: 422, statusText: 'Unprocessable Content' });
    expect(await result).toEqual({ status: 'validation-failed', message: '只有管理者能停用或重新啟用另一位管理者。' });
  });
});
