import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NEVER, of, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { toDateTimeLocalValue } from '../../core/domain/case-due-time';
import type { CaseDetailView, CaseSummaryView } from '../../core/domain/case.model';
import type { CaseGroupListView, CaseTypeListView } from '../../core/domain/case-settings.model';
import { CaseSettingsRepository } from '../../core/repositories/case-settings.repository';
import { CasesRepository, type CaseActionResult, type CreateCaseResult } from '../../core/repositories/cases.repository';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { CasesPageComponent } from './cases-page.component';

const summary: CaseSummaryView = {
  id: 'case-1', title: '冷藏庫溫度降不下來', status: 'pending', origin: 'manual',
  type: { id: 'type-1', name: '設備故障報修' }, group: { id: 'group-1', name: '設備組', archived: false },
  createdBy: { id: 'account-1', displayName: '客服同仁' }, owner: null,
  dueAt: '2026-10-09T01:00:00Z', createdAt: '2026-10-06T01:00:00Z', updatedAt: '2026-10-06T01:00:00Z',
};

function detail(links: Partial<CaseDetailView['links']> = {}, overrides: Partial<Omit<CaseDetailView, 'links'>> = {}): CaseDetailView {
  return {
    allowedActions: ['accept'],
    cancelReasonRequired: true,
    case: {
      ...summary, description: '二號冷藏庫維持在 9 度。', resolution: null, cancelReason: null,
      acceptedAt: null, completedAt: null, cancelledAt: null, eventCount: 1,
    },
    events: [{
      id: 'event-1', ordinal: 1, action: 'created', actor: { id: 'account-1', displayName: '客服同仁' }, at: '2026-10-06T01:00:00Z',
      note: null, status: 'pending', owner: null, fromGroup: null, toGroup: { id: 'group-1', name: '設備組', archived: false },
      dueAt: '2026-10-09T01:00:00Z',
    }],
    links: { record: null, thread: null, assistantIssue: null, previousCase: null, ...links },
    ...overrides,
  };
}

const TYPES: CaseTypeListView = {
  canManage: false,
  types: [{
    id: 'type-1', name: '設備故障報修', description: '', defaultGroup: { id: 'group-1', name: '設備組', archived: false },
    defaultDueHours: 72, isActive: true, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
  }],
};

const GROUPS: CaseGroupListView = {
  canManage: false,
  candidates: [],
  groups: [
    { id: 'group-1', name: '設備組', archived: false, archivedAt: null, members: [], createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z' },
    { id: 'group-2', name: '採購組', archived: false, archivedAt: null, members: [], createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z' },
  ],
};

async function setup(options: {
  list?: Observable<RepositoryView<readonly CaseSummaryView[]>>;
  get?: Observable<RepositoryView<CaseDetailView>>;
  create?: Observable<CreateCaseResult>;
  act?: Observable<CaseActionResult>;
  url?: string;
  settle?: boolean;
} = {}) {
  TestBed.resetTestingModule();
  const repo = {
    list: vi.fn(() => options.list ?? of({ status: 'ready' as const, data: [summary] })),
    get: vi.fn(() => options.get ?? of({ status: 'ready' as const, data: detail() })),
    create: vi.fn(() => options.create ?? of({ status: 'ready' as const, data: detail() } as CreateCaseResult)),
    act: vi.fn(() => options.act ?? of({ status: 'ready' as const, data: detail() } as CaseActionResult)),
  };
  await TestBed.configureTestingModule({
    imports: [CasesPageComponent],
    providers: [
      provideRouter([{ path: 'app/cases', component: CasesPageComponent }]),
      { provide: CasesRepository, useValue: repo },
      {
        provide: CaseSettingsRepository,
        useValue: {
          listCaseTypes: () => of({ status: 'ready', data: TYPES }),
          listCaseGroups: () => of({ status: 'ready', data: GROUPS }),
        },
      },
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } },
    ],
  }).compileComponents();
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(options.url ?? '/app/cases', CasesPageComponent);
  const fixture = harness.fixture;
  fixture.detectChanges();
  if (options.settle !== false) await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, repo, router: TestBed.inject(Router) };
}

type Fixture = Awaited<ReturnType<typeof setup>>['fixture'];

function text(fixture: Fixture): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

function element<T extends HTMLElement>(fixture: Fixture, selector: string): T {
  const found = (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  if (!found) throw new Error(`missing ${selector}`);
  return found;
}

function change(fixture: Fixture, selector: string, value: string, event = 'change'): void {
  const input = element<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(fixture, selector);
  input.value = value;
  input.dispatchEvent(new Event(event));
  fixture.detectChanges();
}

describe('CasesPageComponent', () => {
  it('lists open cases by default and passes each filter to the repository', async () => {
    const { fixture, repo } = await setup();
    expect(repo.list).toHaveBeenLastCalledWith({ scope: 'all', status: 'open', typeId: undefined, groupId: undefined });
    expect(text(fixture)).toContain('冷藏庫溫度降不下來');
    expect(text(fixture)).toContain('待受理');

    const selects = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLSelectElement>('.cases-filters select');
    for (const [index, value] of [[0, 'my-groups'], [1, 'closed'], [2, 'type-1'], [3, 'group-2']] as const) {
      selects[index].value = value;
      selects[index].dispatchEvent(new Event('change'));
    }
    fixture.detectChanges();
    await fixture.whenStable();
    expect(repo.list).toHaveBeenLastCalledWith({ scope: 'my-groups', status: 'closed', typeId: 'type-1', groupId: 'group-2' });
  });

  it('starts from the filters in the address (the home page card) and filters overdue cases (issue #250)', async () => {
    const owned = await setup({ url: '/app/cases?scope=owned&overdue=true' });
    expect(owned.repo.list).toHaveBeenLastCalledWith({ scope: 'owned', status: 'open', typeId: undefined, groupId: undefined, overdue: true });
    expect(element<HTMLInputElement>(owned.fixture, '[data-case-overdue-filter]').checked).toBe(true);
    const selects = (owned.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLSelectElement>('.cases-filters select');
    expect(selects[0].value).toBe('owned');

    const checkbox = element<HTMLInputElement>(owned.fixture, '[data-case-overdue-filter]');
    checkbox.checked = false;
    checkbox.dispatchEvent(new Event('change'));
    owned.fixture.detectChanges();
    await owned.fixture.whenStable();
    expect(owned.repo.list).toHaveBeenLastCalledWith({ scope: 'owned', status: 'open', typeId: undefined, groupId: undefined });

    const pending = await setup({ url: '/app/cases?scope=my-groups&status=pending&overdue=true' });
    expect(pending.repo.list).toHaveBeenLastCalledWith({ scope: 'my-groups', status: 'pending', typeId: undefined, groupId: undefined, overdue: true });

    const unknown = await setup({ url: '/app/cases?scope=everything&status=whatever&overdue=yes' });
    expect(unknown.repo.list).toHaveBeenLastCalledWith({ scope: 'all', status: 'open', typeId: undefined, groupId: undefined });
  });

  it('marks an open case past its due time as 已逾期, and never a closed one', async () => {
    const { fixture } = await setup({
      list: of({
        status: 'ready',
        data: [
          { ...summary, id: 'late', title: '逾期的待補件', status: 'awaiting-info', dueAt: '2020-01-01T00:00:00Z' },
          { ...summary, id: 'on-time', title: '還沒到時限', dueAt: '2099-01-01T00:00:00Z' },
          { ...summary, id: 'done', title: '已完成的舊案件', status: 'completed', dueAt: '2020-01-01T00:00:00Z' },
        ],
      }),
    });
    const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.case-item'));
    expect(items.map((item) => item.querySelector('.case-overdue')?.textContent ?? null)).toEqual(['已逾期', null, null]);
  });

  it('distinguishes loading, permission and empty states', async () => {
    const loading = await setup({ list: NEVER, settle: false });
    expect(text(loading.fixture)).toContain('正在載入案件');

    const denied = await setup({ list: of({ status: 'permission-denied', reason: 'case', message: '案件功能只開放組織內部帳號使用。' }) });
    expect(text(denied.fixture)).toContain('無法查看案件');

    const empty = await setup({ list: of({ status: 'ready', data: [] }) });
    expect(text(empty.fixture)).toContain('目前沒有符合條件的案件');
  });

  it('selects a case through ?case= and shows its facts, history and links only as openable or not', async () => {
    const { fixture, repo, router } = await setup({
      url: '/app/cases?case=case-1',
      get: of({
        status: 'ready',
        data: detail({
          thread: { assistantId: 'assistant-1', threadId: 'thread-1', canOpen: false },
          record: { databaseId: 'database-1', submissionId: 'submission-1', state: 'withdrawn', canRead: false, databaseName: '客戶資料庫' },
        }),
      }),
    });
    expect(repo.get).toHaveBeenCalledWith('case-1');
    const shown = text(fixture);
    expect(shown).toContain('二號冷藏庫維持在 9 度。');
    expect(shown).toContain('尚未受理');
    expect(shown).toContain('建立案件');
    expect(shown).toContain('這個對話無法開啟');
    expect(shown).toContain('紀錄已撤回');
    expect(element(fixture, '[data-case-link="thread"]').querySelector('a')).toBeNull();

    element<HTMLButtonElement>(fixture, '.case-item').click();
    await fixture.whenStable();
    expect(router.url).toBe('/app/cases?case=case-1');
  });

  it('links an openable conversation and a readable record', async () => {
    const { fixture } = await setup({
      url: '/app/cases?case=case-1',
      get: of({
        status: 'ready',
        data: detail({
          thread: { assistantId: 'assistant-1', threadId: 'thread-1', canOpen: true },
          record: { databaseId: 'database-1', submissionId: 'submission-1', state: 'available', canRead: true, databaseName: '客戶資料庫' },
        }),
      }),
    });
    expect(element(fixture, '[data-case-link="thread"] a').getAttribute('href')).toBe('/app/chat/assistant-1/thread-1');
    expect(element(fixture, '[data-case-link="record"] a').getAttribute('href')).toBe('/app/databases/database-1/records');
  });

  it('shows the source issue of a case opened from one, as a link only when you may open the issue (issue #252)', async () => {
    const openable = await setup({
      url: '/app/cases?case=case-1',
      get: of({ status: 'ready', data: detail({ assistantIssue: { issueId: 'issue-7', canOpen: true } }) }),
    });
    expect(element(openable.fixture, '[data-case-link="issue"] a').getAttribute('href')).toBe('/app/issues?issue=issue-7');
    expect(text(openable.fixture)).toContain('來源處理事項');

    const hidden = await setup({
      url: '/app/cases?case=case-1',
      get: of({ status: 'ready', data: detail({ assistantIssue: { issueId: 'issue-7', canOpen: false } }) }),
    });
    expect(element(hidden.fixture, '[data-case-link="issue"]').querySelector('a')).toBeNull();
    expect(element(hidden.fixture, '[data-case-link="issue"]').textContent).toContain('你沒有開啟它的權限');
  });

  it('shows a case opened by a database submission as created by that database, without a person, and a withdrawn record', async () => {
    const base = detail({ record: { databaseId: 'database-1', submissionId: 'submission-1', state: 'withdrawn', canRead: false, databaseName: '客戶資料庫' } });
    const { fixture } = await setup({
      url: '/app/cases?case=case-1',
      get: of({
        status: 'ready',
        data: {
          ...base,
          case: { ...base.case, origin: 'database-submission', createdBy: null, title: '客戶資料庫：新紀錄' },
          events: base.events.map((event) => ({ ...event, actor: null })),
        },
      }),
    });
    expect(element(fixture, '[data-case-created-by]').textContent?.trim()).toBe('由數據庫「客戶資料庫」自動建立');
    expect(element(fixture, '[data-case-link="record"]').textContent).toContain('紀錄已撤回');
    expect(element(fixture, '[data-case-link="record"]').querySelector('a')).toBeNull();
    expect(element(fixture, '[data-case-history]').textContent).toContain('系統');
  });

  it('shows a case it may not see as permission denied', async () => {
    const { fixture } = await setup({
      url: '/app/cases?case=case-9',
      get: of({ status: 'permission-denied', reason: 'case', message: '你沒有這個案件的存取權限，或它已不存在。' }),
    });
    expect(text(fixture)).toContain('無法查看這件案件');
  });

  it('fills in the group and due time from the type, and refuses a due time in the past before sending', async () => {
    const { fixture, repo, router } = await setup();
    element<HTMLButtonElement>(fixture, '[data-case-create]').click();
    fixture.detectChanges();

    const before = Date.now();
    change(fixture, '#case-type', 'type-1');
    expect(element<HTMLSelectElement>(fixture, '#case-group').value).toBe('group-1');
    const due = new Date(element<HTMLInputElement>(fixture, '#case-due').value).getTime();
    expect(Math.abs(due - (before + 72 * 3600 * 1000))).toBeLessThan(2 * 60 * 1000);

    change(fixture, '#case-group', 'group-2');
    change(fixture, '#case-due', toDateTimeLocalValue(new Date(Date.now() - 3600 * 1000)), 'input');
    expect(text(fixture)).toContain('時限不能早於現在。');
    change(fixture, '#case-title', '溫控器顯示錯誤代碼', 'input');
    element<HTMLFormElement>(fixture, '[data-case-form]').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(repo.create).not.toHaveBeenCalled();

    const future = new Date(Date.now() + 5 * 3600 * 1000);
    change(fixture, '#case-due', toDateTimeLocalValue(future), 'input');
    expect(text(fixture)).not.toContain('時限不能早於現在。');
    element<HTMLFormElement>(fixture, '[data-case-form]').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(repo.create).toHaveBeenCalledWith({
      typeId: 'type-1', groupId: 'group-2', dueAt: new Date(toDateTimeLocalValue(future)).toISOString(),
      title: '溫控器顯示錯誤代碼', description: '',
    });
    expect(router.url).toBe('/app/cases?case=case-1');
    expect(fixture.nativeElement.querySelector('[data-case-form]')).toBeNull();
  });

  it('shows the API\'s refusal under its field', async () => {
    const { fixture } = await setup({
      create: of({
        status: 'validation-failed', reason: 'case-type-inactive', message: '這個案件類型已停用或不存在，請選擇其他類型。',
        fieldErrors: { typeId: '這個案件類型已停用或不存在，請選擇其他類型。' },
      }),
    });
    element<HTMLButtonElement>(fixture, '[data-case-create]').click();
    fixture.detectChanges();
    change(fixture, '#case-type', 'type-1');
    change(fixture, '#case-title', '標題', 'input');
    element<HTMLFormElement>(fixture, '[data-case-form]').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(element(fixture, '#case-type-error').textContent).toContain('這個案件類型已停用或不存在');
  });

  it('shows only the actions the API allows, sends the shown eventCount and reloads the detail and the list', async () => {
    const { fixture, repo } = await setup({ url: '/app/cases?case=case-1' });
    const buttons = [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('[data-case-action]')];
    expect(buttons.map((button) => button.textContent?.trim())).toEqual(['受理']);
    const gets = repo.get.mock.calls.length;
    const lists = repo.list.mock.calls.length;

    buttons[0].click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(repo.act).toHaveBeenCalledWith('case-1', 'accept', { eventCount: 1 });
    expect(repo.get.mock.calls.length).toBeGreaterThan(gets);
    expect(repo.list.mock.calls.length).toBeGreaterThan(lists);
  });

  it('shows a closed case without actions and opens a new case from it, linked to the old one', async () => {
    const closed = detail({}, { allowedActions: [], cancelReasonRequired: true });
    const completedCase = { ...closed.case, status: 'completed' as const, resolution: '已更換軸承。', completedAt: '2026-10-07T01:00:00Z' };
    const { fixture, repo } = await setup({
      url: '/app/cases?case=case-1',
      get: of({ status: 'ready', data: { ...closed, case: completedCase } }),
    });
    expect(fixture.nativeElement.querySelector('[data-case-actions]')).toBeNull();
    expect(text(fixture)).toContain('已更換軸承。');

    element<HTMLButtonElement>(fixture, '[data-case-follow-up]').click();
    fixture.detectChanges();
    expect(element(fixture, '[data-case-follow-up-note]').textContent).toContain('冷藏庫溫度降不下來');
    expect(element<HTMLSelectElement>(fixture, '#case-type').value).toBe('type-1');
    expect(element<HTMLInputElement>(fixture, '#case-title').value).toBe('冷藏庫溫度降不下來');

    element<HTMLFormElement>(fixture, '[data-case-form]').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(repo.create).toHaveBeenCalledWith(expect.objectContaining({
      typeId: 'type-1', groupId: 'group-1', title: '冷藏庫溫度降不下來', previousCaseId: 'case-1',
    }));
  });

  it('shows who did what and when on the timeline, a transfer with both groups', async () => {
    const base = detail();
    const { fixture } = await setup({
      url: '/app/cases?case=case-1',
      get: of({
        status: 'ready',
        data: {
          ...base,
          events: [
            ...base.events,
            {
              id: 'event-2', ordinal: 2, action: 'accepted', actor: { id: 'account-2', displayName: '阿明' }, at: '2026-10-06T02:00:00Z',
              note: null, status: 'in-progress', owner: { id: 'account-2', displayName: '阿明' }, fromGroup: null, toGroup: null, dueAt: null,
            },
            {
              id: 'event-3', ordinal: 3, action: 'transferred', actor: { id: 'account-2', displayName: '阿明' }, at: '2026-10-06T03:00:00Z',
              note: '需要採購零件', status: 'pending', owner: null,
              fromGroup: { id: 'group-1', name: '設備組', archived: false }, toGroup: { id: 'group-2', name: '採購組', archived: false }, dueAt: null,
            },
          ],
        },
      }),
    });
    const items = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-case-history] li')].map((item) => item.textContent?.replace(/\s+/g, ' ').trim());
    expect(items[1]).toContain('阿明');
    expect(items[1]).toContain('受理');
    expect(items[2]).toContain('轉組（設備組 → 採購組）：需要採購零件');
    const times = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-case-history] time')].map((time) => time.getAttribute('datetime'));
    expect(times).toEqual(['2026-10-06T01:00:00Z', '2026-10-06T02:00:00Z', '2026-10-06T03:00:00Z']);
  });
});
