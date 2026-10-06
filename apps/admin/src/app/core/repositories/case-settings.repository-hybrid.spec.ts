import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  API_CASE_GROUPS_PATH,
  API_CASE_TYPES_PATH,
  apiCaseGroupPath,
  apiCaseTypePath,
  CaseSettingsRepository,
} from './case-settings.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-06 from the real API (issue #246): `dotnet run` of this branch on port 5262 against
// a throw-away PostgreSQL database (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by
// `scratchpad/246/record.py`. The bodies are pasted unchanged; never edit them to match the types.
/** admin-create: HTTP 201. */
const CREATE_JSON = `{"id":"01a1115f-3aa7-72a8-b3b0-d6f28cbedbac","name":"設備組","archived":false,"archivedAt":null,"members":[],"createdAt":"2026-10-06T13:20:16.039036+00:00","updatedAt":"2026-10-06T13:20:16.039036+00:00"}`;
/** admin-create-taken-422: HTTP 422. */
const CREATE_TAKEN_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-name-taken","message":"已經有同名的承辦組，請換一個名稱。","errors":{"name":["已經有同名的承辦組，請換一個名稱。"]}}`;
/** admin-create-blank-422: HTTP 422. */
const CREATE_BLANK_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請輸入承辦組名稱。","errors":{"name":["請輸入承辦組名稱。"]}}`;
/** admin-archive-second: HTTP 200. */
const ARCHIVE_JSON = `{"id":"01a1115f-3aef-7491-969e-cb1638c0462c","name":"舊倉儲組","archived":true,"archivedAt":"2026-10-06T13:20:16.122375+00:00","members":[],"createdAt":"2026-10-06T13:20:16.111737+00:00","updatedAt":"2026-10-06T13:20:16.122375+00:00"}`;
/** admin-get-all: HTTP 200. */
const ADMIN_LIST_JSON = `{"groups":[{"id":"01a1115f-3aa7-72a8-b3b0-d6f28cbedbac","name":"設備組","archived":false,"archivedAt":null,"members":[],"createdAt":"2026-10-06T13:20:16.039036+00:00","updatedAt":"2026-10-06T13:20:16.039036+00:00"},{"id":"01a1115f-3aef-7491-969e-cb1638c0462c","name":"舊倉儲組","archived":true,"archivedAt":"2026-10-06T13:20:16.122375+00:00","members":[],"createdAt":"2026-10-06T13:20:16.111737+00:00","updatedAt":"2026-10-06T13:20:16.122375+00:00"}],"canManage":true,"candidates":[{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者","role":"smb-admin"},{"id":"01a1115e-d9e4-7fa2-80c4-0b1677c89cf0","displayName":"安心商行客服同仁","role":"internal-employee"}]}`;
/** admin-members: HTTP 200. */
const MEMBERS_JSON = `{"id":"01a1115f-3aa7-72a8-b3b0-d6f28cbedbac","name":"設備組","archived":false,"archivedAt":null,"members":[{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},{"id":"01a1115e-d9e4-7fa2-80c4-0b1677c89cf0","displayName":"安心商行客服同仁"}],"createdAt":"2026-10-06T13:20:16.039036+00:00","updatedAt":"2026-10-06T13:20:16.039036+00:00"}`;
/** admin-members-external-422: HTTP 422. */
const MEMBERS_EXTERNAL_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"member-not-eligible","message":"成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。","errors":{"accountIds":["成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。"]}}`;
/** admin-rename: HTTP 200. */
const RENAME_JSON = `{"id":"01a1115f-3aa7-72a8-b3b0-d6f28cbedbac","name":"設備維修組","archived":false,"archivedAt":null,"members":[{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},{"id":"01a1115e-d9e4-7fa2-80c4-0b1677c89cf0","displayName":"安心商行客服同仁"}],"createdAt":"2026-10-06T13:20:16.039036+00:00","updatedAt":"2026-10-06T13:20:16.212824+00:00"}`;
/** admin-member-changes: HTTP 200. */
const MEMBER_CHANGES_JSON = `[{"id":"01a1115f-3b3a-7eaa-962a-b5a7e8595e0c","account":{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},"added":true,"changedBy":{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},"changedAt":"2026-10-06T13:20:16.179715+00:00"},{"id":"01a1115f-3b37-7c15-813e-aa67267bff6d","account":{"id":"01a1115e-d9e4-7fa2-80c4-0b1677c89cf0","displayName":"安心商行客服同仁"},"added":true,"changedBy":{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},"changedAt":"2026-10-06T13:20:16.179715+00:00"}]`;
/** admin-unarchive-second: HTTP 200. */
const UNARCHIVE_JSON = `{"id":"01a1115f-3aef-7491-969e-cb1638c0462c","name":"舊倉儲組","archived":false,"archivedAt":null,"members":[],"createdAt":"2026-10-06T13:20:16.111737+00:00","updatedAt":"2026-10-06T13:20:16.23373+00:00"}`;
/** internal-get: HTTP 200. */
const INTERNAL_LIST_JSON = `{"groups":[{"id":"01a1115f-3aa7-72a8-b3b0-d6f28cbedbac","name":"設備維修組","archived":false,"archivedAt":null,"members":[{"id":"01a1115e-d9a5-730b-8607-8a54f3449881","displayName":"安心商行管理者"},{"id":"01a1115e-d9e4-7fa2-80c4-0b1677c89cf0","displayName":"安心商行客服同仁"}],"createdAt":"2026-10-06T13:20:16.039036+00:00","updatedAt":"2026-10-06T13:20:16.212824+00:00"},{"id":"01a1115f-3aef-7491-969e-cb1638c0462c","name":"舊倉儲組","archived":false,"archivedAt":null,"members":[],"createdAt":"2026-10-06T13:20:16.111737+00:00","updatedAt":"2026-10-06T13:20:16.23373+00:00"}],"canManage":false,"candidates":[]}`;
/** internal-create-403: HTTP 403. */
const INTERNAL_CREATE_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** external-get-403: HTTP 403. */
const EXTERNAL_LIST_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"案件功能只開放組織內部帳號使用。"}`;

// Recorded 2026-10-06 from the real API (issue #247): `dotnet run` of this branch on port 5262 against
// a throw-away PostgreSQL database (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by `scratchpad/247/record.py`
// (groups 設備組 and 舊倉儲組 created first; 舊倉儲組 archived after the inactive type was created).
// The bodies are pasted unchanged; never edit them to match the types.
/** admin-type-create: HTTP 201. */
const TYPE_CREATE_JSON = `{"id":"01a111b6-e24b-70ab-a136-327c19c1c4ea","name":"設備故障報修","description":"冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。","defaultGroup":{"id":"01a111b6-e205-784b-a3a9-142961e382b3","name":"設備組","archived":false},"defaultDueHours":72,"isActive":true,"createdAt":"2026-10-06T14:56:00.587364+00:00","updatedAt":"2026-10-06T14:56:00.587364+00:00"}`;
/** admin-type-create-inactive: HTTP 201. */
const TYPE_CREATE_INACTIVE_JSON = `{"id":"01a111b6-e25a-7bb2-b69c-e4fadaf1bbe9","name":"倉儲盤點差異","description":"盤點數量與系統不符。","defaultGroup":{"id":"01a111b6-e240-7559-9e2c-471249068406","name":"舊倉儲組","archived":false},"defaultDueHours":36,"isActive":false,"createdAt":"2026-10-06T14:56:00.602576+00:00","updatedAt":"2026-10-06T14:56:00.602576+00:00"}`;
/** admin-type-create-taken-422: HTTP 422. */
const TYPE_TAKEN_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-type-name-taken","message":"已經有同名的案件類型，請換一個名稱。","errors":{"name":["已經有同名的案件類型，請換一個名稱。"]}}`;
/** admin-type-create-invalid-422: HTTP 422. */
const TYPE_INVALID_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請輸入案件類型名稱。","errors":{"defaultDueHours":["預設處理時限請在 1 到 2,160 小時（90 天）之間。"],"description":["說明請在 500 個字以內。"],"name":["請輸入案件類型名稱。"]}}`;
/** admin-type-create-2161-422: HTTP 422. */
const TYPE_2161_HOURS_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"預設處理時限請在 1 到 2,160 小時（90 天）之間。","errors":{"defaultDueHours":["預設處理時限請在 1 到 2,160 小時（90 天）之間。"]}}`;
/** admin-type-create-archived-group-422: HTTP 422. */
const TYPE_ARCHIVED_GROUP_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-archived","message":"這個承辦組已封存，請選擇其他承辦組。","errors":{"defaultGroupId":["這個承辦組已封存，請選擇其他承辦組。"]}}`;
/** admin-group-archive-in-use-422: HTTP 422. */
const GROUP_IN_USE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-in-use","message":"「設備組」是啟用中的案件類型「設備故障報修」的預設承辦組。請先替這些類型換一個承辦組，或停用它們，再封存。","errors":{"caseTypes":["「設備組」是啟用中的案件類型「設備故障報修」的預設承辦組。請先替這些類型換一個承辦組，或停用它們，再封存。"]}}`;
/** admin-type-update: HTTP 200. */
const TYPE_UPDATE_JSON = `{"id":"01a111b6-e24b-70ab-a136-327c19c1c4ea","name":"設備故障報修","description":"冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。","defaultGroup":{"id":"01a111b6-e205-784b-a3a9-142961e382b3","name":"設備組","archived":false},"defaultDueHours":48,"isActive":true,"createdAt":"2026-10-06T14:56:00.587364+00:00","updatedAt":"2026-10-06T14:56:00.634659+00:00"}`;
/** admin-type-reactivate-archived-422: HTTP 422. */
const TYPE_REACTIVATE_ARCHIVED_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-archived","message":"這個承辦組已封存，請選擇其他承辦組。","errors":{"defaultGroupId":["這個承辦組已封存，請選擇其他承辦組。"]}}`;
/** admin-types-all: HTTP 200. */
const TYPES_ADMIN_ALL_JSON = `{"types":[{"id":"01a111b6-e24b-70ab-a136-327c19c1c4ea","name":"設備故障報修","description":"冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。","defaultGroup":{"id":"01a111b6-e205-784b-a3a9-142961e382b3","name":"設備組","archived":false},"defaultDueHours":48,"isActive":true,"createdAt":"2026-10-06T14:56:00.587364+00:00","updatedAt":"2026-10-06T14:56:00.634659+00:00"},{"id":"01a111b6-e25a-7bb2-b69c-e4fadaf1bbe9","name":"倉儲盤點差異","description":"盤點數量與系統不符。","defaultGroup":{"id":"01a111b6-e240-7559-9e2c-471249068406","name":"舊倉儲組","archived":true},"defaultDueHours":36,"isActive":false,"createdAt":"2026-10-06T14:56:00.602576+00:00","updatedAt":"2026-10-06T14:56:00.602576+00:00"}],"canManage":true}`;
/** internal-types: HTTP 200. */
const TYPES_INTERNAL_JSON = `{"types":[{"id":"01a111b6-e24b-70ab-a136-327c19c1c4ea","name":"設備故障報修","description":"冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。","defaultGroup":{"id":"01a111b6-e205-784b-a3a9-142961e382b3","name":"設備組","archived":false},"defaultDueHours":48,"isActive":true,"createdAt":"2026-10-06T14:56:00.587364+00:00","updatedAt":"2026-10-06T14:56:00.634659+00:00"}],"canManage":false}`;
/** internal-type-create-403: HTTP 403. */
const TYPE_INTERNAL_CREATE_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** external-types-403: HTTP 403. */
const TYPES_EXTERNAL_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"案件功能只開放組織內部帳號使用。"}`;

const GROUP_ID = '01a1115f-3aa7-72a8-b3b0-d6f28cbedbac';
const ARCHIVED_ID = '01a1115f-3aef-7491-969e-cb1638c0462c';
const ADMIN_ID = '01a1115e-d9a5-730b-8607-8a54f3449881';
const INTERNAL_ID = '01a1115e-d9e4-7fa2-80c4-0b1677c89cf0';

function apiRepository() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return { repository: TestBed.inject(CaseSettingsRepository), http: TestBed.inject(HttpTestingController) };
}

function flushError(http: HttpTestingController, url: string, status: number, statusText: string, body: string): void {
  http.expectOne(url).flush(JSON.parse(body), { status, statusText });
}

describe('CaseSettingsRepository (API mode, recorded responses)', () => {
  it('reads the manager\'s full list with candidates, and an employee\'s list without them', async () => {
    const { repository, http } = apiRepository();

    const admin = firstValueFrom(repository.listCaseGroups({ includeArchived: true }));
    const adminRequest = http.expectOne((request) => request.url === API_CASE_GROUPS_PATH);
    expect(adminRequest.request.params.get('includeArchived')).toBe('true');
    adminRequest.flush(JSON.parse(ADMIN_LIST_JSON));
    const adminView = await admin;
    expect(adminView.status).toBe('ready');
    if (adminView.status !== 'ready') return;
    expect(adminView.data.canManage).toBe(true);
    expect(adminView.data.groups.map((group) => [group.name, group.archived])).toEqual([['設備組', false], ['舊倉儲組', true]]);
    expect(adminView.data.candidates).toEqual([
      { id: ADMIN_ID, displayName: '安心商行管理者', role: 'smb-admin' },
      { id: INTERNAL_ID, displayName: '安心商行客服同仁', role: 'internal-employee' },
    ]);

    const member = firstValueFrom(repository.listCaseGroups());
    const memberRequest = http.expectOne((request) => request.url === API_CASE_GROUPS_PATH);
    expect(memberRequest.request.params.has('includeArchived')).toBe(false);
    memberRequest.flush(JSON.parse(INTERNAL_LIST_JSON));
    const memberView = await member;
    if (memberView.status !== 'ready') throw new Error(memberView.status);
    expect(memberView.data.canManage).toBe(false);
    expect(memberView.data.candidates).toEqual([]);
    expect(memberView.data.groups[0].members.map((account) => account.displayName)).toEqual(['安心商行管理者', '安心商行客服同仁']);
    http.verify();
  });

  it('turns an external customer\'s 403 case and an employee\'s 403 organization-settings into permission-denied', async () => {
    const { repository, http } = apiRepository();

    const list = firstValueFrom(repository.listCaseGroups());
    flushError(http, API_CASE_GROUPS_PATH, 403, 'Forbidden', EXTERNAL_LIST_403_JSON);
    expect(await list).toEqual({ status: 'permission-denied', reason: 'case', message: '案件功能只開放組織內部帳號使用。' });

    const create = firstValueFrom(repository.createCaseGroup('品保組'));
    flushError(http, API_CASE_GROUPS_PATH, 403, 'Forbidden', INTERNAL_CREATE_403_JSON);
    expect(await create).toEqual({ status: 'permission-denied', reason: 'organization-settings', message: '只有管理者可以變更組織設定。' });
    http.verify();
  });

  it('creates and renames a group, and shows a taken or blank name as validation-failed', async () => {
    const { repository, http } = apiRepository();

    const created = firstValueFrom(repository.createCaseGroup('設備組'));
    const createRequest = http.expectOne(API_CASE_GROUPS_PATH);
    expect([createRequest.request.method, createRequest.request.body]).toEqual(['POST', { name: '設備組' }]);
    createRequest.flush(JSON.parse(CREATE_JSON), { status: 201, statusText: 'Created' });
    expect(await created).toMatchObject({ status: 'ready', data: { id: GROUP_ID, name: '設備組', archived: false, members: [] } });

    const taken = firstValueFrom(repository.createCaseGroup('設備組'));
    flushError(http, API_CASE_GROUPS_PATH, 422, 'Unprocessable Content', CREATE_TAKEN_422_JSON);
    expect(await taken).toEqual({ status: 'validation-failed', message: '已經有同名的承辦組，請換一個名稱。' });

    const blank = firstValueFrom(repository.createCaseGroup('  '));
    flushError(http, API_CASE_GROUPS_PATH, 422, 'Unprocessable Content', CREATE_BLANK_422_JSON);
    expect(await blank).toEqual({ status: 'validation-failed', message: '請輸入承辦組名稱。' });

    const renamed = firstValueFrom(repository.renameCaseGroup(GROUP_ID, '設備維修組'));
    const renameRequest = http.expectOne(apiCaseGroupPath(GROUP_ID));
    expect([renameRequest.request.method, renameRequest.request.body]).toEqual(['PUT', { name: '設備維修組' }]);
    renameRequest.flush(JSON.parse(RENAME_JSON));
    expect(await renamed).toMatchObject({ status: 'ready', data: { name: '設備維修組' } });
    http.verify();
  });

  it('archives and unarchives through the :archive and :unarchive actions', async () => {
    const { repository, http } = apiRepository();

    const archived = firstValueFrom(repository.setCaseGroupArchived(ARCHIVED_ID, true));
    const archiveRequest = http.expectOne(`${apiCaseGroupPath(ARCHIVED_ID)}:archive`);
    expect(archiveRequest.request.method).toBe('POST');
    archiveRequest.flush(JSON.parse(ARCHIVE_JSON));
    expect(await archived).toMatchObject({ status: 'ready', data: { archived: true, archivedAt: '2026-10-06T13:20:16.122375+00:00' } });

    const unarchived = firstValueFrom(repository.setCaseGroupArchived(ARCHIVED_ID, false));
    http.expectOne(`${apiCaseGroupPath(ARCHIVED_ID)}:unarchive`).flush(JSON.parse(UNARCHIVE_JSON));
    expect(await unarchived).toMatchObject({ status: 'ready', data: { archived: false, archivedAt: null } });
    http.verify();
  });

  it('replaces the members, refuses an external customer, and reads the history', async () => {
    const { repository, http } = apiRepository();

    const saved = firstValueFrom(repository.updateCaseGroupMembers(GROUP_ID, [INTERNAL_ID, ADMIN_ID]));
    const membersRequest = http.expectOne(`${apiCaseGroupPath(GROUP_ID)}/members`);
    expect([membersRequest.request.method, membersRequest.request.body]).toEqual(['PUT', { accountIds: [INTERNAL_ID, ADMIN_ID] }]);
    membersRequest.flush(JSON.parse(MEMBERS_JSON));
    const view = await saved;
    if (view.status !== 'ready') throw new Error(view.status);
    expect(view.data.members.map((member) => member.id)).toEqual([ADMIN_ID, INTERNAL_ID]);

    const refused = firstValueFrom(repository.updateCaseGroupMembers(GROUP_ID, ['external']));
    flushError(http, `${apiCaseGroupPath(GROUP_ID)}/members`, 422, 'Unprocessable Content', MEMBERS_EXTERNAL_422_JSON);
    expect(await refused).toEqual({
      status: 'validation-failed',
      message: '成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。',
    });

    const changes = firstValueFrom(repository.listCaseGroupMemberChanges(GROUP_ID));
    http.expectOne(`${apiCaseGroupPath(GROUP_ID)}/member-changes`).flush(JSON.parse(MEMBER_CHANGES_JSON));
    const history = await changes;
    if (history.status !== 'ready') throw new Error(history.status);
    expect(history.data.map((change) => [change.account.displayName, change.added, change.changedBy.displayName])).toEqual([
      ['安心商行管理者', true, '安心商行管理者'],
      ['安心商行客服同仁', true, '安心商行管理者'],
    ]);
    http.verify();
  });

  it('maps a concurrent member update to conflict and keeps other failures as errors', async () => {
    const { repository, http } = apiRepository();

    const conflict = firstValueFrom(repository.updateCaseGroupMembers(GROUP_ID, []));
    http.expectOne(`${apiCaseGroupPath(GROUP_ID)}/members`).flush(
      { reason: 'case-group-members-conflict', message: '承辦組成員剛被其他人更新，請重新整理後再試一次。' },
      { status: 409, statusText: 'Conflict' },
    );
    expect(await conflict).toEqual({ status: 'conflict', message: '承辦組成員剛被其他人更新，請重新整理後再試一次。' });

    const broken = firstValueFrom(repository.listCaseGroups());
    http.expectOne(API_CASE_GROUPS_PATH).flush('', { status: 500, statusText: 'Server Error' });
    await expect(broken).rejects.toMatchObject({ status: 500 });
    http.verify();
  });
});

const REPAIR_TYPE_ID = '01a111b6-e24b-70ab-a136-327c19c1c4ea';
const STOCKTAKE_TYPE_ID = '01a111b6-e25a-7bb2-b69c-e4fadaf1bbe9';
const EQUIPMENT_GROUP_ID = '01a111b6-e205-784b-a3a9-142961e382b3';
const WAREHOUSE_GROUP_ID = '01a111b6-e240-7559-9e2c-471249068406';
const REPAIR_DESCRIPTION = '冷藏庫、溫控或灌溉設備故障，需要派人到場檢修。';

describe('CaseSettingsRepository case types (API mode, recorded responses, issue #247)', () => {
  it('reads the manager\'s list with inactive types, and an employee\'s active types only', async () => {
    const { repository, http } = apiRepository();

    const admin = firstValueFrom(repository.listCaseTypes({ includeInactive: true }));
    const adminRequest = http.expectOne((request) => request.url === API_CASE_TYPES_PATH);
    expect(adminRequest.request.params.get('includeInactive')).toBe('true');
    adminRequest.flush(JSON.parse(TYPES_ADMIN_ALL_JSON));
    const adminView = await admin;
    if (adminView.status !== 'ready') throw new Error(adminView.status);
    expect(adminView.data.canManage).toBe(true);
    expect(adminView.data.types.map((type) => [type.name, type.isActive, type.defaultGroup.name, type.defaultGroup.archived, type.defaultDueHours]))
      .toEqual([['設備故障報修', true, '設備組', false, 48], ['倉儲盤點差異', false, '舊倉儲組', true, 36]]);

    const member = firstValueFrom(repository.listCaseTypes());
    const memberRequest = http.expectOne((request) => request.url === API_CASE_TYPES_PATH);
    expect(memberRequest.request.params.has('includeInactive')).toBe(false);
    memberRequest.flush(JSON.parse(TYPES_INTERNAL_JSON));
    const memberView = await member;
    if (memberView.status !== 'ready') throw new Error(memberView.status);
    expect([memberView.data.canManage, memberView.data.types.map((type) => type.id)]).toEqual([false, [REPAIR_TYPE_ID]]);
    expect(memberView.data.types[0].defaultGroup).toEqual({ id: EQUIPMENT_GROUP_ID, name: '設備組', archived: false });
    http.verify();
  });

  it('turns an external customer\'s 403 case and an employee\'s 403 organization-settings into permission-denied', async () => {
    const { repository, http } = apiRepository();

    const list = firstValueFrom(repository.listCaseTypes());
    flushError(http, API_CASE_TYPES_PATH, 403, 'Forbidden', TYPES_EXTERNAL_403_JSON);
    expect(await list).toEqual({ status: 'permission-denied', reason: 'case', message: '案件功能只開放組織內部帳號使用。' });

    const create = firstValueFrom(repository.createCaseType({
      name: '品保', description: '', defaultGroupId: EQUIPMENT_GROUP_ID, defaultDueHours: 24, isActive: true,
    }));
    flushError(http, API_CASE_TYPES_PATH, 403, 'Forbidden', TYPE_INTERNAL_CREATE_403_JSON);
    expect(await create).toEqual({ status: 'permission-denied', reason: 'organization-settings', message: '只有管理者可以變更組織設定。' });
    http.verify();
  });

  it('creates and updates a type with every field, in hours', async () => {
    const { repository, http } = apiRepository();
    const input = {
      name: '設備故障報修', description: REPAIR_DESCRIPTION, defaultGroupId: EQUIPMENT_GROUP_ID, defaultDueHours: 72, isActive: true,
    };

    const created = firstValueFrom(repository.createCaseType(input));
    const createRequest = http.expectOne(API_CASE_TYPES_PATH);
    expect([createRequest.request.method, createRequest.request.body]).toEqual(['POST', input]);
    createRequest.flush(JSON.parse(TYPE_CREATE_JSON), { status: 201, statusText: 'Created' });
    expect(await created).toMatchObject({ status: 'ready', data: { id: REPAIR_TYPE_ID, defaultDueHours: 72, isActive: true } });

    const inactive = firstValueFrom(repository.createCaseType({
      name: '倉儲盤點差異', description: '盤點數量與系統不符。', defaultGroupId: WAREHOUSE_GROUP_ID, defaultDueHours: 36, isActive: false,
    }));
    http.expectOne(API_CASE_TYPES_PATH).flush(JSON.parse(TYPE_CREATE_INACTIVE_JSON), { status: 201, statusText: 'Created' });
    expect(await inactive).toMatchObject({ status: 'ready', data: { id: STOCKTAKE_TYPE_ID, isActive: false } });

    const updated = firstValueFrom(repository.updateCaseType(REPAIR_TYPE_ID, { ...input, defaultDueHours: 48 }));
    const updateRequest = http.expectOne(apiCaseTypePath(REPAIR_TYPE_ID));
    expect([updateRequest.request.method, updateRequest.request.body.defaultDueHours]).toEqual(['PUT', 48]);
    updateRequest.flush(JSON.parse(TYPE_UPDATE_JSON));
    expect(await updated).toMatchObject({ status: 'ready', data: { defaultDueHours: 48, updatedAt: '2026-10-06T14:56:00.634659+00:00' } });
    http.verify();
  });

  it('shows every field error, the 2,161-hour limit, a taken name and an archived group as validation-failed', async () => {
    const { repository, http } = apiRepository();
    const input = { name: '急件', description: '', defaultGroupId: EQUIPMENT_GROUP_ID, defaultDueHours: 24, isActive: true };

    const invalid = firstValueFrom(repository.createCaseType({ ...input, name: '  ', description: '說'.repeat(501), defaultDueHours: 0 }));
    flushError(http, API_CASE_TYPES_PATH, 422, 'Unprocessable Content', TYPE_INVALID_422_JSON);
    expect(await invalid).toEqual({
      status: 'validation-failed',
      message: '請輸入案件類型名稱。',
      fieldErrors: {
        name: '請輸入案件類型名稱。',
        description: '說明請在 500 個字以內。',
        defaultDueHours: '預設處理時限請在 1 到 2,160 小時（90 天）之間。',
      },
    });

    const tooLong = firstValueFrom(repository.createCaseType({ ...input, defaultDueHours: 2161 }));
    flushError(http, API_CASE_TYPES_PATH, 422, 'Unprocessable Content', TYPE_2161_HOURS_422_JSON);
    expect(await tooLong).toMatchObject({ fieldErrors: { defaultDueHours: '預設處理時限請在 1 到 2,160 小時（90 天）之間。' } });

    const taken = firstValueFrom(repository.createCaseType({ ...input, name: '設備故障報修' }));
    flushError(http, API_CASE_TYPES_PATH, 422, 'Unprocessable Content', TYPE_TAKEN_422_JSON);
    expect(await taken).toEqual({
      status: 'validation-failed', message: '已經有同名的案件類型，請換一個名稱。', fieldErrors: { name: '已經有同名的案件類型，請換一個名稱。' },
    });

    const archived = firstValueFrom(repository.createCaseType({ ...input, defaultGroupId: WAREHOUSE_GROUP_ID }));
    flushError(http, API_CASE_TYPES_PATH, 422, 'Unprocessable Content', TYPE_ARCHIVED_GROUP_422_JSON);
    expect(await archived).toMatchObject({ fieldErrors: { defaultGroupId: '這個承辦組已封存，請選擇其他承辦組。' } });

    const reactivate = firstValueFrom(repository.updateCaseType(STOCKTAKE_TYPE_ID, {
      name: '倉儲盤點差異', description: '盤點數量與系統不符。', defaultGroupId: WAREHOUSE_GROUP_ID, defaultDueHours: 36, isActive: true,
    }));
    flushError(http, apiCaseTypePath(STOCKTAKE_TYPE_ID), 422, 'Unprocessable Content', TYPE_REACTIVATE_ARCHIVED_422_JSON);
    expect(await reactivate).toMatchObject({ status: 'validation-failed', fieldErrors: { defaultGroupId: '這個承辦組已封存，請選擇其他承辦組。' } });
    http.verify();
  });

  it('refuses to archive the default group of an active type with the server\'s message naming the type', async () => {
    const { repository, http } = apiRepository();

    const archived = firstValueFrom(repository.setCaseGroupArchived(EQUIPMENT_GROUP_ID, true));
    flushError(http, `${apiCaseGroupPath(EQUIPMENT_GROUP_ID)}:archive`, 422, 'Unprocessable Content', GROUP_IN_USE_422_JSON);

    expect(await archived).toEqual({
      status: 'validation-failed',
      message: '「設備組」是啟用中的案件類型「設備故障報修」的預設承辦組。請先替這些類型換一個承辦組，或停用它們，再封存。',
    });
    http.verify();
  });
});
