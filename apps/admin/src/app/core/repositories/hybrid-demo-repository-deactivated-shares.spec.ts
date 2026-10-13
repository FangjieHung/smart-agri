import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  apiAssistantPlatformSharingPath,
  apiAssistantPublishingPath,
  apiDatabaseAccessPath,
  apiDatabasePath,
  apiKnowledgeBasePath,
  apiKnowledgeSharingPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const ADMIN_ID = '01a1367b-9255-7d1a-900d-054cffc2c583';
const MEMBER_ID = '01a1367b-9290-7848-b1ff-4bc1139c0820';
const CUSTOMER_ID = '01a1367b-92b4-764a-8f76-c75dc39a2494';
const ASSISTANT_ID = 'a27dab0c-7aa3-4460-8f25-597cb02efb6a';
const KNOWLEDGE_BASE_ID = '01a1367c-4657-799e-895e-9dcfd8b60571';
const DATABASE_ID = '01a1367c-468b-7ab9-86f0-002debefa50e';

/*
 * 以下回應都是 2026-10-14 從本機 API（Development、暫用的資料庫，`migrate` 的開發 seed）實際取得（issue #363）：
 * 管理者把一個助理分享給客服同仁與外部客戶、建立一個知識庫並指定分享給兩人、建立一個數據庫並指定自己與客服同仁為
 * 資料管理者（助理直接寫進資料庫，因為開發 seed 沒有助理），再不選接手人停用客服同仁（名下沒有東西）；
 * 管理者讀三個頁面，之後在三個選單各自取消客服同仁並儲存（知識庫另外重讀一次詳情）。
 * 原樣保留成字串再 `JSON.parse`（`String.raw`：嵌入碼裡的 `\"` 要保持原樣），不是依產生的型別手寫。
 */
const REAL_PUBLISHING_JSON = String.raw`{"assistantId":"a27dab0c-7aa3-4460-8f25-597cb02efb6a","assistantName":"門市問答助理","platform":{"channel":{"id":"channel-platform:a27dab0c-7aa3-4460-8f25-597cb02efb6a","assistantId":"a27dab0c-7aa3-4460-8f25-597cb02efb6a","ownerAccountId":"01a1367b-9255-7d1a-900d-054cffc2c583","name":"門市問答助理","type":"platform","status":"published","statusDetail":"已分享給組織內 2 位成員。","updatedAt":"2026-10-13T18:17:50.880553+00:00"},"usagePath":"/app/chat/a27dab0c-7aa3-4460-8f25-597cb02efb6a","allowedAccountIds":["01a1367b-9290-7848-b1ff-4bc1139c0820","01a1367b-92b4-764a-8f76-c75dc39a2494"],"candidates":[{"id":"01a1367b-9290-7848-b1ff-4bc1139c0820","displayName":"安心商行客服同仁","deactivated":true},{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","deactivated":false}]},"website":{"channel":{"id":"channel-website:a27dab0c-7aa3-4460-8f25-597cb02efb6a","assistantId":"a27dab0c-7aa3-4460-8f25-597cb02efb6a","ownerAccountId":"01a1367b-9255-7d1a-900d-054cffc2c583","name":"門市問答助理","type":"website","status":"not-configured","statusDetail":"尚未設定官網嵌入。","updatedAt":"2026-10-13T18:17:50.880553+00:00"},"displayName":"門市問答助理","welcomeMessage":"您好，我是門市問答助理，有什麼可以協助？","brandColor":"forest","position":"bottom-right","allowedDomains":[],"domains":[],"state":"draft","servingState":"not-published","servingMessage":"尚未設定官網嵌入。","acceptanceStatus":"not-accepted","nonOwnedKnowledgeBases":[],"embedCode":"<script src=\"http://localhost:5153/embed.js\" data-assistant=\"a27dab0c-7aa3-4460-8f25-597cb02efb6a\" async></script>","publishedAt":null,"revision":0},"line":{"channel":{"id":"channel-line:a27dab0c-7aa3-4460-8f25-597cb02efb6a","assistantId":"a27dab0c-7aa3-4460-8f25-597cb02efb6a","ownerAccountId":"01a1367b-9255-7d1a-900d-054cffc2c583","name":"門市問答助理","type":"line","status":"not-configured","statusDetail":"尚未填寫 LINE 官方帳號連接資訊。","updatedAt":"2026-10-13T18:17:50.880553+00:00"},"officialAccountId":"","channelId":"","welcomeMessage":"您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。","nonTextReply":"目前只能回答文字問題。","channelSecret":{"configured":false,"lastFour":null,"updatedAt":null},"accessToken":{"configured":false,"lastFour":null,"updatedAt":null},"webhookUrl":"http://localhost:5153/api/v1/line/webhook/a27dab0c-7aa3-4460-8f25-597cb02efb6a","checks":[{"check":"access-token","label":"Channel access token 與官方帳號","state":"pending","message":"尚未測試。"},{"check":"webhook-endpoint","label":"設定 Webhook 網址","state":"pending","message":"尚未測試。"},{"check":"webhook-test","label":"Webhook 連線測試","state":"pending","message":"尚未測試。"}],"connectionCheckedAt":null,"state":"draft","servingState":"not-published","servingMessage":"尚未填寫 LINE 官方帳號連接資訊。","acceptanceStatus":"not-accepted","nonOwnedKnowledgeBases":[],"publishedAt":null,"pushFallbackCount":0,"revision":0}}`;
const REAL_PLATFORM_REMOVED_JSON = String.raw`{"channel":{"id":"channel-platform:a27dab0c-7aa3-4460-8f25-597cb02efb6a","assistantId":"a27dab0c-7aa3-4460-8f25-597cb02efb6a","ownerAccountId":"01a1367b-9255-7d1a-900d-054cffc2c583","name":"門市問答助理","type":"platform","status":"published","statusDetail":"已分享給組織內 1 位成員。","updatedAt":"2026-10-13T18:17:50.880553+00:00"},"usagePath":"/app/chat/a27dab0c-7aa3-4460-8f25-597cb02efb6a","allowedAccountIds":["01a1367b-92b4-764a-8f76-c75dc39a2494"],"candidates":[{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","deactivated":false}]}`;
const REAL_KNOWLEDGE_DETAIL_JSON = String.raw`{"summary":{"id":"01a1367c-4657-799e-895e-9dcfd8b60571","name":"門市作業手冊","purpose":"","documentCount":0,"faqCount":0,"statusCounts":{"queued":0,"processing":0,"ready":0,"partially-readable":0,"failed":0},"inEffectCount":0,"awaitingApprovalCount":0,"disabledCount":0,"sharingScope":"specific-accounts","updatedAt":"2026-10-13T18:17:56.603771+00:00","viewerCanManage":true},"documents":[],"sharing":{"scope":"specific-accounts","sharedWithAccountIds":["01a1367b-9290-7848-b1ff-4bc1139c0820","01a1367b-92b4-764a-8f76-c75dc39a2494"],"allowOriginalDownload":false},"shareTargets":[{"id":"01a1367b-9290-7848-b1ff-4bc1139c0820","displayName":"安心商行客服同仁","deactivated":true},{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","deactivated":false}]}`;
const REAL_SHARING_REMOVED_JSON = String.raw`{"scope":"specific-accounts","sharedWithAccountIds":["01a1367b-92b4-764a-8f76-c75dc39a2494"],"allowOriginalDownload":false}`;
const REAL_KNOWLEDGE_DETAIL_AFTER_JSON = String.raw`{"summary":{"id":"01a1367c-4657-799e-895e-9dcfd8b60571","name":"門市作業手冊","purpose":"","documentCount":0,"faqCount":0,"statusCounts":{"queued":0,"processing":0,"ready":0,"partially-readable":0,"failed":0},"inEffectCount":0,"awaitingApprovalCount":0,"disabledCount":0,"sharingScope":"specific-accounts","updatedAt":"2026-10-13T18:17:56.896141+00:00","viewerCanManage":true},"documents":[],"sharing":{"scope":"specific-accounts","sharedWithAccountIds":["01a1367b-92b4-764a-8f76-c75dc39a2494"],"allowOriginalDownload":false},"shareTargets":[{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","deactivated":false}]}`;
const REAL_DATABASE_DETAIL_JSON = String.raw`{"summary":{"id":"01a1367c-468b-7ab9-86f0-002debefa50e","name":"門市回報","purpose":"從一個文字欄位開始，自行設計要收集的欄位。","templateId":"template-blank","templateName":"空白模板","fieldCount":1,"formVersion":1,"owner":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},"createdAt":"2026-10-13T18:17:56.618936+00:00","updatedAt":"2026-10-13T18:17:56.618936+00:00","viewerCanManage":true,"archivedAt":null,"connectedAssistantNames":[],"recordCount":0,"subjectCount":0},"form":{"id":"01a1367c-468c-7e16-aca2-2daa31da9f7a","versionNumber":1,"createdAt":"2026-10-13T18:17:56.618936+00:00","fields":[{"id":"field-item","label":"項目名稱","type":"text","required":true,"options":[],"scale":null,"unit":""}]},"access":{"owner":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-13T18:17:56.618936+00:00","assignedBy":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}},{"account":{"id":"01a1367b-9290-7848-b1ff-4bc1139c0820","displayName":"安心商行客服同仁"},"hasReadPermission":true,"assignedAt":"2026-10-13T18:17:56.658806+00:00","assignedBy":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},{"id":"01a1367b-9290-7848-b1ff-4bc1139c0820","displayName":"安心商行客服同仁"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":true,"candidates":[{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者","role":"smb-admin","hasReadPermission":true,"deactivated":false},{"id":"01a1367b-9290-7848-b1ff-4bc1139c0820","displayName":"安心商行客服同仁","role":"internal-employee","hasReadPermission":true,"deactivated":true},{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","role":"external-customer","hasReadPermission":false,"deactivated":false}],"lastChange":{"changedAt":"2026-10-13T18:17:56.658806+00:00","changedBy":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}}},"connectedAssistants":[]}`;
const REAL_ACCESS_REMOVED_JSON = String.raw`{"owner":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-13T18:17:56.618936+00:00","assignedBy":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":true,"candidates":[{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者","role":"smb-admin","hasReadPermission":true,"deactivated":false},{"id":"01a1367b-92b4-764a-8f76-c75dc39a2494","displayName":"外部客戶","role":"external-customer","hasReadPermission":false,"deactivated":false}],"lastChange":{"changedAt":"2026-10-13T18:17:56.905276+00:00","changedBy":{"id":"01a1367b-9255-7d1a-900d-054cffc2c583","displayName":"安心商行管理者"}}}`;

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

describe('HybridDemoRepository deactivated share targets (issue #363)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists the deactivated member the assistant is still shared with, flagged, until the owner removes it', async () => {
    const { repository, controller } = setUp();

    const read = firstValueFrom(repository.getAssistantPublishing(ASSISTANT_ID));
    controller.expectOne({ method: 'GET', url: apiAssistantPublishingPath(ASSISTANT_ID) }).flush(JSON.parse(REAL_PUBLISHING_JSON));
    const view = await read;
    if (view.status !== 'ready') throw new Error(view.status);
    expect(view.data.platform.allowedAccountIds).toEqual([MEMBER_ID, CUSTOMER_ID]);
    expect(view.data.platform.candidates.map(({ id, deactivated }) => ({ id, deactivated }))).toEqual([
      { id: MEMBER_ID, deactivated: true },
      { id: CUSTOMER_ID, deactivated: false },
    ]);

    const saved = firstValueFrom(repository.updatePlatformSharing(ASSISTANT_ID, [CUSTOMER_ID]));
    const request = controller.expectOne({ method: 'PUT', url: apiAssistantPlatformSharingPath(ASSISTANT_ID) });
    expect(request.request.body).toEqual({ accountIds: [CUSTOMER_ID] });
    request.flush(JSON.parse(REAL_PLATFORM_REMOVED_JSON));
    const result = await saved;
    if (result.status !== 'ready') throw new Error(result.status);
    expect(result.data.allowedAccountIds).toEqual([CUSTOMER_ID]);
    expect(result.data.candidates.map(({ id, deactivated }) => ({ id, deactivated }))).toEqual([{ id: CUSTOMER_ID, deactivated: false }]);
  });

  it('lists the deactivated member the knowledge base is still shared with, flagged, until the owner removes it', async () => {
    const { repository, controller } = setUp();

    const read = firstValueFrom(repository.getKnowledgeBaseDetail(KNOWLEDGE_BASE_ID));
    controller.expectOne({ method: 'GET', url: apiKnowledgeBasePath(KNOWLEDGE_BASE_ID) }).flush(JSON.parse(REAL_KNOWLEDGE_DETAIL_JSON));
    const detail = await read;
    if (detail.status !== 'ready') throw new Error(detail.status);
    expect(detail.data.sharing.sharedWithAccountIds).toEqual([MEMBER_ID, CUSTOMER_ID]);
    expect(detail.data.shareTargets).toEqual([
      { id: MEMBER_ID, displayName: '安心商行客服同仁', deactivated: true },
      { id: CUSTOMER_ID, displayName: '外部客戶', deactivated: false },
    ]);

    const saved = firstValueFrom(
      repository.updateKnowledgeSharing(KNOWLEDGE_BASE_ID, {
        scope: 'specific-accounts',
        sharedWithAccountIds: [CUSTOMER_ID],
        allowOriginalDownload: false,
      }),
    );
    controller.expectOne({ method: 'PUT', url: apiKnowledgeSharingPath(KNOWLEDGE_BASE_ID) }).flush(JSON.parse(REAL_SHARING_REMOVED_JSON));
    const result = await saved;
    if (result.status !== 'ready') throw new Error(result.status);
    expect(result.data.sharedWithAccountIds).toEqual([CUSTOMER_ID]);

    const reread = firstValueFrom(repository.getKnowledgeBaseDetail(KNOWLEDGE_BASE_ID));
    controller
      .expectOne({ method: 'GET', url: apiKnowledgeBasePath(KNOWLEDGE_BASE_ID) })
      .flush(JSON.parse(REAL_KNOWLEDGE_DETAIL_AFTER_JSON));
    const after = await reread;
    if (after.status !== 'ready') throw new Error(after.status);
    expect(after.data.shareTargets).toEqual([{ id: CUSTOMER_ID, displayName: '外部客戶', deactivated: false }]);
  });

  it('lists the deactivated data manager among the candidates, flagged, until the owner removes it', async () => {
    const { repository, controller } = setUp();

    const read = firstValueFrom(repository.getDatabaseDetail(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: apiDatabasePath(DATABASE_ID) }).flush(JSON.parse(REAL_DATABASE_DETAIL_JSON));
    const detail = await read;
    if (detail.status !== 'ready') throw new Error(detail.status);
    expect(detail.data.access.dataManagers.map((manager) => manager.id)).toEqual([ADMIN_ID, MEMBER_ID]);
    expect(detail.data.access.candidates.map(({ id, deactivated }) => ({ id, deactivated }))).toEqual([
      { id: ADMIN_ID, deactivated: false },
      { id: MEMBER_ID, deactivated: true },
      { id: CUSTOMER_ID, deactivated: false },
    ]);

    const saved = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, [ADMIN_ID]));
    controller.expectOne({ method: 'PUT', url: apiDatabaseAccessPath(DATABASE_ID) }).flush(JSON.parse(REAL_ACCESS_REMOVED_JSON));
    const result = await saved;
    if (result.status !== 'ready') throw new Error(result.status);
    expect(result.data.dataManagers.map((manager) => manager.id)).toEqual([ADMIN_ID]);
    expect(result.data.candidates.some((candidate) => candidate.deactivated)).toBe(false);
    expect(result.data.candidates.map((candidate) => candidate.id)).not.toContain(MEMBER_ID);
  });
});
