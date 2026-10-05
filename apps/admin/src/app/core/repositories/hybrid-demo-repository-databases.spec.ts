import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { createDatabaseField, type DatabaseFieldView } from '../domain/database.model';
import type { components } from '../api/api-schema';
import type { DemoKeyValueStorage } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import {
  API_DATABASE_TEMPLATES_PATH,
  API_DATABASES_PATH,
  API_UPCOMING_DATABASE_FEATURES,
  apiDatabaseAccessPath,
  apiDatabasePath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

type ApiDatabaseSummary = components['schemas']['DatabaseSummaryView'];
type ApiDatabaseTemplate = components['schemas']['DatabaseTemplateView'];

const ADMIN_ID = '0199a3c0-0000-7000-8000-000000000001';
const DATABASE_ID = '0199a3c0-0000-7000-8000-0000000000d1';

/**
 * 實際從 API 取得的 `GET /api/v1/databases/{id}` 回應（API 整合測試主機以「滿意度調查」模板建立後讀回，2026-10-03），
 * 原樣保留成字串再 `JSON.parse`，而不是依產生的型別手寫：確保 adapter 吃得下真實的鍵與 null。
 */
const REAL_DETAIL_JSON = `{"summary":{"id":"01a0ffc9-9dac-7a01-a9ae-3f268e9f53bf","name":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","templateId":"template-satisfaction","templateName":"滿意度調查","fieldCount":3,"formVersion":1,"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"createdAt":"2026-10-03T03:23:18.315844+00:00","updatedAt":"2026-10-03T03:23:18.315844+00:00","viewerCanManage":true},"form":{"id":"01a0ffc9-9dad-7ed6-88ba-1f1e89b0c021","versionNumber":1,"createdAt":"2026-10-03T03:23:18.315844+00:00","fields":[{"id":"field-overall-satisfaction","label":"整體滿意度","type":"scale","required":true,"options":[],"scale":{"min":1,"max":5,"minLabel":"很不滿意","maxLabel":"非常滿意"},"unit":""},{"id":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","required":false,"options":["商品品質","客服回應","配送速度"],"scale":null,"unit":""},{"id":"field-suggestion","label":"其他建議","type":"text","required":false,"options":[],"scale":null,"unit":""}]},"access":{"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.315844+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶"},"hasReadPermission":false,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":true,"candidates":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者","role":"smb-admin","hasReadPermission":true},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁","role":"internal-employee","hasReadPermission":true},{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶","role":"external-customer","hasReadPermission":false}],"lastChange":{"changedAt":"2026-10-03T03:23:18.396829+00:00","changedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}}}`;

/**
 * 同一個數據庫，由被指定且具備權限的「非擁有者」讀到的詳情（`viewerCanManage`、`viewerCanManageAccess`
 * 都是 false，`candidates` 為空）。2026-10-03 取自 API 整合測試主機，原樣保留。
 */
const REAL_MANAGER_DETAIL_JSON = `{"summary":{"id":"01a0ffc9-9dac-7a01-a9ae-3f268e9f53bf","name":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","templateId":"template-satisfaction","templateName":"滿意度調查","fieldCount":3,"formVersion":1,"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"createdAt":"2026-10-03T03:23:18.315844+00:00","updatedAt":"2026-10-03T03:23:18.315844+00:00","viewerCanManage":false},"form":{"id":"01a0ffc9-9dad-7ed6-88ba-1f1e89b0c021","versionNumber":1,"createdAt":"2026-10-03T03:23:18.315844+00:00","fields":[{"id":"field-overall-satisfaction","label":"整體滿意度","type":"scale","required":true,"options":[],"scale":{"min":1,"max":5,"minLabel":"很不滿意","maxLabel":"非常滿意"},"unit":""},{"id":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","required":false,"options":["商品品質","客服回應","配送速度"],"scale":null,"unit":""},{"id":"field-suggestion","label":"其他建議","type":"text","required":false,"options":[],"scale":null,"unit":""}]},"access":{"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.315844+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶"},"hasReadPermission":false,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":false,"candidates":[],"lastChange":{"changedAt":"2026-10-03T03:23:18.396829+00:00","changedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}}}`;

/**
 * 同一個數據庫的 `GET /api/v1/databases`，先由可讀紀錄的擁有者讀（三筆送出、撤回一筆後：2 筆、1 位對象），
 * 再由取消自己指定、因此不可讀的擁有者讀（沒有 `recordCount`／`subjectCount` 鍵，#177）。
 * 2026-10-05 取自 API 整合測試主機，原樣保留。
 */
const REAL_READER_LIST_JSON = `[{"id":"01a10b99-f171-71c1-9d58-56a0f978dd73","name":"門市客戶","purpose":"整理客戶聯絡方式與類型，方便後續服務。","templateId":"template-customer-profile","templateName":"客戶基本資料","fieldCount":4,"formVersion":1,"owner":{"id":"01a10b99-ec55-7b4d-b6e0-767ad3a7435c","displayName":"安心商行管理者"},"createdAt":"2026-10-05T10:26:40.624749+00:00","updatedAt":"2026-10-05T10:26:40.624749+00:00","viewerCanManage":true,"connectedAssistantNames":[],"recordCount":2,"subjectCount":1}]`;
const REAL_NON_READER_LIST_JSON = `[{"id":"01a10b99-f171-71c1-9d58-56a0f978dd73","name":"門市客戶","purpose":"整理客戶聯絡方式與類型，方便後續服務。","templateId":"template-customer-profile","templateName":"客戶基本資料","fieldCount":4,"formVersion":1,"owner":{"id":"01a10b99-ec55-7b4d-b6e0-767ad3a7435c","displayName":"安心商行管理者"},"createdAt":"2026-10-05T10:26:40.624749+00:00","updatedAt":"2026-10-05T10:26:40.624749+00:00","viewerCanManage":true,"connectedAssistantNames":[]}]`;

/** `PUT /api/v1/databases/{id}/access` 的真實回應（擁有者指定三個帳號後）。 */
const REAL_PUT_ACCESS_JSON = `{"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.315844+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶"},"hasReadPermission":false,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":true,"candidates":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者","role":"smb-admin","hasReadPermission":true},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁","role":"internal-employee","hasReadPermission":true},{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶","role":"external-customer","hasReadPermission":false}],"lastChange":{"changedAt":"2026-10-03T03:23:18.396829+00:00","changedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}}`;

/**
 * 實際從 API 取得的表單編輯與試填回應（API 整合測試主機以「滿意度調查」模板建立後呼叫，2026-10-03），
 * 原樣保留成字串再 `JSON.parse`：確保 adapter 吃得下真實的鍵（`scale: null`、`errors` 的鍵格式）。
 */
const REAL_SAVE_200_JSON = `{"id":"01a0ffca-135f-77c8-aa65-4aea75938186","versionNumber":2,"createdAt":"2026-10-03T03:23:48.44787+00:00","fields":[{"id":"field-overall-satisfaction","label":"整體滿意度","type":"scale","required":true,"options":[],"scale":{"min":1,"max":5,"minLabel":"很不滿意","maxLabel":"非常滿意"},"unit":""},{"id":"field-suggestion","label":"其他建議（改）","type":"text","required":false,"options":[],"scale":null,"unit":""},{"id":"field-amount","label":"金額","type":"number","required":false,"options":[],"scale":null,"unit":"元"}]}`;
const REAL_SAVE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請填寫欄位名稱。","errors":{"fields[0].label":["請填寫欄位名稱。"],"fields[1].options":["單選或多選至少需要 2 個選項。"]}}`;
const REAL_SAVE_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"form-version-changed","message":"這份表單已被更新過，請重新載入後再修改。你這次的修改尚未儲存。"}`;
const REAL_PREVIEW_200_JSON = `{"saved":false,"formVersion":2,"entries":[{"fieldId":"field-overall-satisfaction","label":"整體滿意度","display":"4 / 5"},{"fieldId":"field-suggestion","label":"其他建議（改）","display":"未填寫"},{"fieldId":"field-amount","label":"金額","display":"1,200 元"}]}`;
const REAL_PREVIEW_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"「整體滿意度」為必填。","errors":{"answers.field-amount":["「金額」請輸入數字。"],"answers.field-overall-satisfaction":["「整體滿意度」為必填。"]}}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const base = createMemoryStorage();
  /** mock storage 被寫入的鍵：API 模式的建立不能落到 mock 的資料庫上。 */
  const written: string[] = [];
  const storage: DemoKeyValueStorage = {
    ...base,
    setItem: (key, value) => {
      written.push(key);
      base.setItem(key, value);
    },
  };
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage, viewer: () => 'account-smb-admin' },
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
  return { repository, written, controller: TestBed.inject(HttpTestingController) };
}

function summary(overrides: Partial<ApiDatabaseSummary> = {}): ApiDatabaseSummary {
  return {
    id: DATABASE_ID,
    name: '門市滿意度',
    purpose: '收集客戶對服務的評分與建議。',
    templateId: 'template-satisfaction',
    templateName: '滿意度調查',
    fieldCount: 3,
    formVersion: 1,
    owner: { id: ADMIN_ID, displayName: '安心商行管理者' },
    createdAt: '2026-10-03T02:00:00+00:00',
    updatedAt: '2026-10-03T02:00:00+00:00',
    viewerCanManage: true,
    connectedAssistantNames: [],
    ...overrides,
  };
}

const FORBIDDEN_DATABASE = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4',
  title: 'Forbidden',
  status: 403,
  reason: 'database',
  message: '你沒有這個資料庫的存取權限，或它已不存在。',
};

describe('HybridDemoRepository databases (issue #142)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists templates from the API with their fields', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.listDatabaseTemplates());
    const template: ApiDatabaseTemplate = {
      id: 'template-progress',
      name: '症狀或進度追蹤',
      description: '為每位追蹤對象建立時間軸，比較本次、上次與首次的變化。',
      fields: [
        { id: 'field-check-date', label: '紀錄日期', type: 'date', required: true, options: [], scale: null, unit: '' },
        {
          id: 'field-condition-score',
          label: '狀況分數',
          type: 'scale',
          required: true,
          options: [],
          scale: { min: 0, max: 10, minLabel: '最差', maxLabel: '最好' },
          unit: '',
        },
      ],
    };
    controller.expectOne({ method: 'GET', url: API_DATABASE_TEMPLATES_PATH }).flush([template]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: 'template-progress',
          name: '症狀或進度追蹤',
          description: template.description,
          fields: [
            { id: 'field-check-date', label: '紀錄日期', type: 'date', required: true, options: [], scale: null, unit: '' },
            {
              id: 'field-condition-score',
              label: '狀況分數',
              type: 'scale',
              required: true,
              options: [],
              scale: { min: 0, max: 10, minLabel: '最差', maxLabel: '最好' },
              unit: '',
            },
          ],
        },
      ],
    });
  });

  it('turns a 403 on templates into the create permission-denied', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.listDatabaseTemplates());
    controller
      .expectOne(API_DATABASE_TEMPLATES_PATH)
      .flush({ ...FORBIDDEN_DATABASE, message: '只有可管理資料來源的帳號可以建立資料庫。' }, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'database',
      message: '只有可管理資料來源的帳號可以建立資料庫。',
    });
  });

  it('lists summaries with the owner and never a record count', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.listDatabaseSummaries());
    controller.expectOne({ method: 'GET', url: API_DATABASES_PATH }).flush([summary()]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: DATABASE_ID,
          name: '門市滿意度',
          purpose: '收集客戶對服務的評分與建議。',
          owner: { id: ADMIN_ID, displayName: '安心商行管理者' },
          viewerCanManage: true,
          templateName: '滿意度調查',
          fieldCount: 3,
          recordCount: null,
          subjectCount: null,
          connectedAssistantNames: [],
          updatedAt: '2026-10-03T02:00:00+00:00',
        },
      ],
    });
  });

  it('shows the counts a reader gets from the real list and null for a non-reader, whose JSON has no count keys (#177)', async () => {
    const { repository, controller } = setUp();
    const reader = firstValueFrom(repository.listDatabaseSummaries());
    controller.expectOne({ method: 'GET', url: API_DATABASES_PATH }).flush(JSON.parse(REAL_READER_LIST_JSON));
    expect(await reader).toMatchObject({ status: 'ready', data: [{ name: '門市客戶', recordCount: 2, subjectCount: 1 }] });

    const nonReader = firstValueFrom(repository.listDatabaseSummaries());
    controller.expectOne({ method: 'GET', url: API_DATABASES_PATH }).flush(JSON.parse(REAL_NON_READER_LIST_JSON));
    const outcome = await nonReader;
    expect(outcome).toMatchObject({ status: 'ready', data: [{ name: '門市客戶', recordCount: null, subjectCount: null }] });
  });

  it('creates through the API, not the mock: the request carries the template and name, and mock storage stays empty', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(
      repository.createDatabaseFromTemplate({ templateId: 'template-satisfaction', name: '門市滿意度' }),
    );
    const request = controller.expectOne({ method: 'POST', url: API_DATABASES_PATH });
    expect(request.request.body).toEqual({ templateId: 'template-satisfaction', name: '門市滿意度' });
    request.flush(summary(), { status: 201, statusText: 'Created' });

    const created = await result;
    expect(created).toMatchObject({ status: 'ready', data: { id: DATABASE_ID, templateName: '滿意度調查' } });
    expect(written.filter((key) => key.includes('database'))).toEqual([]);
  });

  it('turns a 422 into validation-failed with the server message, and a 403 into permission-denied', async () => {
    const { repository, controller } = setUp();
    const invalid = firstValueFrom(repository.createDatabaseFromTemplate({ templateId: 'template-blank', name: ' ' }));
    controller.expectOne(API_DATABASES_PATH).flush(
      { status: 422, message: '請輸入資料庫名稱。', errors: { name: ['請輸入資料庫名稱。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );
    expect(await invalid).toEqual({ status: 'validation-failed', message: '請輸入資料庫名稱。' });

    const denied = firstValueFrom(repository.createDatabaseFromTemplate({ templateId: 'template-blank', name: '名' }));
    controller
      .expectOne(API_DATABASES_PATH)
      .flush({ ...FORBIDDEN_DATABASE, message: '只有可管理資料來源的帳號可以建立資料庫。' }, { status: 403, statusText: 'Forbidden' });
    expect(await denied).toMatchObject({ status: 'permission-denied', reason: 'database' });
  });

  it('lets a server failure on create reach the screen as an error, so it can keep the input and retry', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.createDatabaseFromTemplate({ templateId: 'template-blank', name: '名' }));
    controller.expectOne(API_DATABASES_PATH).flush('boom', { status: 500, statusText: 'Internal Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });

  it('maps the real detail JSON: form fields, owner, and every M4 feature still upcoming', async () => {
    const { repository, controller } = setUp();
    const parsed = JSON.parse(REAL_DETAIL_JSON) as components['schemas']['DatabaseDetailView'];
    const result = firstValueFrom(repository.getDatabaseDetail(parsed.summary.id));
    controller.expectOne({ method: 'GET', url: apiDatabasePath(parsed.summary.id) }).flush(parsed);

    const detail = await result;
    if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
    expect(detail.data.summary).toMatchObject({
      name: parsed.summary.name,
      templateName: '滿意度調查',
      fieldCount: 3,
      recordCount: null,
      owner: parsed.summary.owner,
    });
    expect(detail.data.fields.map((field) => [field.id, field.type, field.scale])).toEqual([
      ['field-overall-satisfaction', 'scale', { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' }],
      ['field-liked-services', 'multiple-choice', null],
      ['field-suggestion', 'text', null],
    ]);
    expect(detail.data.fields[1].options).toEqual(['商品品質', '客服回應', '配送速度']);
    expect(detail.data.upcomingFeatures).toEqual(API_UPCOMING_DATABASE_FEATURES);
    expect(detail.data.connectedAssistants).toEqual([]);
    expect(detail.data.summary.viewerCanManage).toBe(true);
    expect(detail.data.upcomingFeatures).not.toContain('data-managers');
    // 擁有者（指定了三個帳號之後）：全部在「已指定」，外部客戶沒有帳號層級權限所以不在「目前可讀」。
    expect(detail.data.access).toMatchObject({
      owner: parsed.summary.owner,
      viewerIsDataManager: true,
      viewerCanReadRecords: true,
      viewerCanManageAccess: true,
      savedBy: parsed.summary.owner,
    });
    expect(detail.data.access.dataManagers).toHaveLength(3);
    expect(detail.data.access.effectiveReaders.map((reader) => reader.displayName)).toEqual([
      '安心商行管理者',
      '安心商行客服同仁',
    ]);
    expect(detail.data.access.candidates.map((candidate) => [candidate.roleLabel, candidate.hasReadPermission])).toEqual([
      ['管理者', true],
      ['內部同仁', true],
      ['外部客戶', false],
    ]);
  });

  it('maps a data manager’s view of the real JSON as read-only, with who changed it and when', async () => {
    const { repository, controller } = setUp();
    const parsed = JSON.parse(REAL_MANAGER_DETAIL_JSON) as components['schemas']['DatabaseDetailView'];
    const result = firstValueFrom(repository.getDatabaseDetail(parsed.summary.id));
    controller.expectOne({ method: 'GET', url: apiDatabasePath(parsed.summary.id) }).flush(parsed);

    const detail = await result;
    if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
    expect(detail.data.summary.viewerCanManage).toBe(false);
    const { access } = detail.data;
    expect(access.viewerCanManageAccess).toBe(false);
    expect(access.viewerCanReadRecords).toBe(true);
    expect(access.candidates).toEqual([]);
    // 已指定三個帳號，但外部客戶沒有帳號層級權限：在「已指定」、不在「目前可讀」。
    expect(access.dataManagers.map((manager) => manager.displayName)).toEqual([
      '安心商行管理者',
      '安心商行客服同仁',
      '安心商行外部客戶',
    ]);
    expect(access.effectiveReaders.map((reader) => reader.displayName)).toEqual(['安心商行管理者', '安心商行客服同仁']);
    expect(access.savedBy).toEqual({ id: parsed.summary.owner.id, displayName: '安心商行管理者' });
    expect(access.savedAt).toBe(parsed.access.lastChange?.changedAt);
  });

  it('tolerates a detail whose access omits the optional change instead of breaking the page', async () => {
    const { repository, controller } = setUp();
    const parsed = JSON.parse(REAL_DETAIL_JSON) as components['schemas']['DatabaseDetailView'];
    delete (parsed.access as { lastChange?: unknown }).lastChange;
    const result = firstValueFrom(repository.getDatabaseDetail(parsed.summary.id));
    controller.expectOne(apiDatabasePath(parsed.summary.id)).flush(parsed);

    const detail = await result;
    if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
    expect(detail.data.access).toMatchObject({ savedAt: null, savedBy: null });
  });

  it('designates through the API with the complete list, and never touches mock storage', async () => {
    const { repository, controller, written } = setUp();
    const parsed = JSON.parse(REAL_PUT_ACCESS_JSON) as components['schemas']['DatabaseAccessView'];
    const ids = parsed.dataManagers.map((manager) => manager.account.id);
    const result = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, ids));
    const request = controller.expectOne({ method: 'PUT', url: apiDatabaseAccessPath(DATABASE_ID) });
    expect(request.request.body).toEqual({ dataManagerAccountIds: ids });
    request.flush(parsed);

    const saved = await result;
    if (saved.status !== 'ready') throw new Error(`expected ready, got ${saved.status}`);
    expect(saved.data.dataManagers).toHaveLength(3);
    expect(saved.data.effectiveReaders).toHaveLength(2);
    expect(saved.data.viewerCanManageAccess).toBe(true);
    expect(saved.data.savedBy?.displayName).toBe('安心商行管理者');
    expect(written.filter((key) => key.includes('database'))).toEqual([]);
  });

  it('turns 422/409 on designation into validation-failed with the server message, and 403/404 into permission-denied', async () => {
    const { repository, controller } = setUp();
    const unknown = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, ['0199a3c0-0000-7000-8000-00000000ffff']));
    controller.expectOne(apiDatabaseAccessPath(DATABASE_ID)).flush(
      {
        status: 422,
        message: '有不認得的帳號，這次指定沒有儲存。',
        errors: { dataManagerAccountIds: ['有不認得的帳號，這次指定沒有儲存。'] },
      },
      { status: 422, statusText: 'Unprocessable Content' },
    );
    expect(await unknown).toEqual({ status: 'validation-failed', message: '有不認得的帳號，這次指定沒有儲存。' });

    const conflict = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, [ADMIN_ID]));
    controller.expectOne(apiDatabaseAccessPath(DATABASE_ID)).flush(
      { status: 409, reason: 'database-access-conflict', message: '資料管理者剛被其他人更新，請重新整理後再試一次。' },
      { status: 409, statusText: 'Conflict' },
    );
    expect(await conflict).toMatchObject({ status: 'validation-failed', message: '資料管理者剛被其他人更新，請重新整理後再試一次。' });

    const denied = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, [ADMIN_ID]));
    controller.expectOne(apiDatabaseAccessPath(DATABASE_ID)).flush(FORBIDDEN_DATABASE, { status: 403, statusText: 'Forbidden' });
    expect(await denied).toEqual({
      status: 'permission-denied',
      reason: 'database',
      message: '你沒有這個資料庫的存取權限，或它已不存在。',
    });

    const notGuid = firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN_ID]));
    controller.expectOne(apiDatabaseAccessPath('database-orders')).flush(null, { status: 404, statusText: 'Not Found' });
    expect(await notGuid).toMatchObject({ status: 'permission-denied', reason: 'database' });

    const serverError = firstValueFrom(repository.updateDatabaseAccess(DATABASE_ID, [ADMIN_ID]));
    controller.expectOne(apiDatabaseAccessPath(DATABASE_ID)).flush('boom', { status: 500, statusText: 'Internal Server Error' });
    await expect(serverError).rejects.toMatchObject({ status: 500 });
  });

  it('turns a 403 and a non-GUID 404 on the detail into the same database permission-denied', async () => {
    const { repository, controller } = setUp();
    const forbidden = firstValueFrom(repository.getDatabaseDetail(DATABASE_ID));
    controller.expectOne(apiDatabasePath(DATABASE_ID)).flush(FORBIDDEN_DATABASE, { status: 403, statusText: 'Forbidden' });
    const notGuid = firstValueFrom(repository.getDatabaseDetail('database-orders'));
    controller.expectOne(apiDatabasePath('database-orders')).flush(null, { status: 404, statusText: 'Not Found' });

    expect(await forbidden).toEqual({
      status: 'permission-denied',
      reason: 'database',
      message: '你沒有這個資料庫的存取權限，或它已不存在。',
    });
    expect(await notGuid).toEqual(await forbidden);
  });
});

describe('HybridDemoRepository form editing and trial fill (issue #143)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const formPath = `${apiDatabasePath(DATABASE_ID)}/form`;
  const previewPath = `${formPath}/preview`;
  const field = (id: `field-${string}`, label: string, type: DatabaseFieldView['type'] = 'text'): DatabaseFieldView =>
    createDatabaseField(id, type, { label });

  it('PUTs the form with the base version and maps the saved version back, writing nothing to mock storage', async () => {
    const { repository, controller, written } = setUp();
    const fields: DatabaseFieldView[] = [
      { ...field('field-overall-satisfaction', '整體滿意度', 'scale'), required: true, scale: { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' } },
      field('field-suggestion', '其他建議（改）'),
      { ...field('field-amount', '金額', 'number'), unit: '元' },
    ];
    const result = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, fields, 1));

    const request = controller.expectOne({ method: 'PUT', url: formPath });
    expect(request.request.body).toEqual({
      baseVersionNumber: 1,
      fields: [
        { id: 'field-overall-satisfaction', label: '整體滿意度', type: 'scale', required: true, options: [], scale: { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' }, unit: '' },
        { id: 'field-suggestion', label: '其他建議（改）', type: 'text', required: false, options: [], scale: null, unit: '' },
        { id: 'field-amount', label: '金額', type: 'number', required: false, options: [], scale: null, unit: '元' },
      ],
    });
    request.flush(JSON.parse(REAL_SAVE_200_JSON));

    const saved = await result;
    expect(saved.status).toBe('ready');
    if (saved.status !== 'ready') return;
    expect(saved.data.formVersion).toBe(2);
    expect(saved.data.fields.map((item) => [item.id, item.type, item.scale, item.unit])).toEqual([
      ['field-overall-satisfaction', 'scale', { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' }, ''],
      ['field-suggestion', 'text', null, ''],
      ['field-amount', 'number', null, '元'],
    ]);
    expect(written).toEqual([]);
  });

  it('maps a 422 key like fields[1].options back to the field sent at that position', async () => {
    const { repository, controller } = setUp();
    const fields = [field('field-overall-satisfaction', ' ', 'scale'), field('field-liked-services', '喜歡的服務', 'multiple-choice')];
    const result = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, fields, 1));
    controller.expectOne({ method: 'PUT', url: formPath }).flush(JSON.parse(REAL_SAVE_422_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '請填寫欄位名稱。',
      errors: [
        { fieldId: 'field-overall-satisfaction', message: '請填寫欄位名稱。' },
        { fieldId: 'field-liked-services', message: '單選或多選至少需要 2 個選項。' },
      ],
    });
  });

  it('treats a form-level 422 (fields, baseVersionNumber) as an error without a field', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, [], 1));
    controller.expectOne({ method: 'PUT', url: formPath }).flush(
      { message: '表單至少需要一個欄位。', errors: { fields: ['表單至少需要一個欄位。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '表單至少需要一個欄位。',
      errors: [{ fieldId: null, message: '表單至少需要一個欄位。' }],
    });
  });

  it('turns 409 form-version-changed into conflict with the server message, and 403 into database denied', async () => {
    const { repository, controller } = setUp();
    const conflict = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, [field('field-a', '名')], 1));
    controller.expectOne({ method: 'PUT', url: formPath }).flush(JSON.parse(REAL_SAVE_409_JSON), { status: 409, statusText: 'Conflict' });
    const denied = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, [field('field-a', '名')], 1));
    controller.expectOne({ method: 'PUT', url: formPath }).flush(FORBIDDEN_DATABASE, { status: 403, statusText: 'Forbidden' });

    expect(await conflict).toEqual({
      status: 'conflict',
      message: '這份表單已被更新過，請重新載入後再修改。你這次的修改尚未儲存。',
    });
    expect(await denied).toEqual({
      status: 'permission-denied',
      reason: 'database',
      message: '你沒有這個資料庫的存取權限，或它已不存在。',
    });
  });

  it('lets a server failure through as an error so the screen keeps the draft', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateDatabaseFields(DATABASE_ID, [field('field-a', '名')], 1));
    controller.expectOne({ method: 'PUT', url: formPath }).flush(null, { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });

  it('POSTs the answers as sent and maps the preview, saying nothing was saved', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(
      repository.previewDatabaseEntry(DATABASE_ID, { 'field-overall-satisfaction': '4', 'field-amount': '1200', 'field-tags': ['甲', '乙'] }),
    );

    const request = controller.expectOne({ method: 'POST', url: previewPath });
    expect(request.request.body).toEqual({
      answers: { 'field-overall-satisfaction': '4', 'field-amount': '1200', 'field-tags': ['甲', '乙'] },
    });
    request.flush(JSON.parse(REAL_PREVIEW_200_JSON));

    expect(await result).toEqual({
      status: 'ready',
      data: {
        saved: false,
        formVersion: 2,
        entries: [
          { fieldId: 'field-overall-satisfaction', label: '整體滿意度', display: '4 / 5' },
          { fieldId: 'field-suggestion', label: '其他建議（改）', display: '未填寫' },
          { fieldId: 'field-amount', label: '金額', display: '1,200 元' },
        ],
      },
    });
    expect(written).toEqual([]);
  });

  it('maps answers.<field id> keys of a preview 422 to the fields', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.previewDatabaseEntry(DATABASE_ID, { 'field-amount': 'abc' }));
    controller.expectOne({ method: 'POST', url: previewPath }).flush(JSON.parse(REAL_PREVIEW_422_JSON), { status: 422, statusText: 'Unprocessable Content' });

    const outcome = await result;
    expect(outcome.status).toBe('validation-failed');
    if (outcome.status !== 'validation-failed') return;
    expect(outcome.message).toBe('「整體滿意度」為必填。');
    expect([...outcome.errors].sort((a, b) => String(a.fieldId).localeCompare(String(b.fieldId)))).toEqual([
      { fieldId: 'field-amount', message: '「金額」請輸入數字。' },
      { fieldId: 'field-overall-satisfaction', message: '「整體滿意度」為必填。' },
    ]);
  });

  it('turns a 403 and a non-GUID 404 on the preview into database denied', async () => {
    const { repository, controller } = setUp();
    const forbidden = firstValueFrom(repository.previewDatabaseEntry(DATABASE_ID, {}));
    controller.expectOne({ method: 'POST', url: previewPath }).flush(FORBIDDEN_DATABASE, { status: 403, statusText: 'Forbidden' });
    const notGuid = firstValueFrom(repository.previewDatabaseEntry('database-orders', {}));
    controller.expectOne({ method: 'POST', url: `${apiDatabasePath('database-orders')}/form/preview` }).flush(null, { status: 404, statusText: 'Not Found' });

    expect(await forbidden).toMatchObject({ status: 'permission-denied', reason: 'database' });
    expect(await notGuid).toEqual(await forbidden);
  });

  it('reads the form version from the detail so a save can carry it', async () => {
    const { repository, controller } = setUp();
    const detail = firstValueFrom(repository.getDatabaseDetail(DATABASE_ID));
    const parsed = JSON.parse(REAL_DETAIL_JSON);
    parsed.form.versionNumber = 3;
    controller.expectOne(apiDatabasePath(DATABASE_ID)).flush(parsed);

    const result = await detail;
    expect(result.status === 'ready' && result.data.formVersion).toBe(3);
    expect(API_UPCOMING_DATABASE_FEATURES).not.toContain('form-editing');
  });
});
