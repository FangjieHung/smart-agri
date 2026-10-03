import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
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

/** `PUT /api/v1/databases/{id}/access` 的真實回應（擁有者指定三個帳號後）。 */
const REAL_PUT_ACCESS_JSON = `{"owner":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"dataManagers":[{"account":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.315844+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"},"hasReadPermission":true,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}},{"account":{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶"},"hasReadPermission":false,"assignedAt":"2026-10-03T03:23:18.396829+00:00","assignedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}],"effectiveReaders":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁"}],"viewerIsDataManager":true,"viewerCanReadRecords":true,"viewerCanManageAccess":true,"candidates":[{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者","role":"smb-admin","hasReadPermission":true},{"id":"01a0ffc9-964f-7adb-9f84-cc09119c6274","displayName":"安心商行客服同仁","role":"internal-employee","hasReadPermission":true},{"id":"01a0ffc9-9675-7a51-8c16-b3ee037c94aa","displayName":"安心商行外部客戶","role":"external-customer","hasReadPermission":false}],"lastChange":{"changedAt":"2026-10-03T03:23:18.396829+00:00","changedBy":{"id":"01a0ffc9-95f1-7210-bf64-13d8af38a841","displayName":"安心商行管理者"}}}`;

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
