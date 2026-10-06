import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_CASE_GROUPS_PATH, apiCaseGroupPath, CaseSettingsRepository } from './case-settings.repository';
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
