import { TestBed } from '@angular/core/testing';
import { of, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { toDateTimeLocalValue } from '../../core/domain/case-due-time';
import type { CaseAction, CaseDetailView } from '../../core/domain/case.model';
import type { CaseGroupView } from '../../core/domain/case-settings.model';
import { CasesRepository, type CaseActionResult } from '../../core/repositories/cases.repository';
import { CaseActionsComponent } from './case-actions.component';

const GROUPS: readonly CaseGroupView[] = [
  { id: 'group-1', name: '設備組', archived: false, archivedAt: null, members: [], createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z' },
  { id: 'group-2', name: '採購組', archived: false, archivedAt: null, members: [], createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z' },
];

function detail(allowedActions: CaseAction[], cancelReasonRequired = true): CaseDetailView {
  return {
    case: {
      id: 'case-1', title: '冷藏庫溫度降不下來', description: '', status: 'in-progress', origin: 'manual',
      type: { id: 'type-1', name: '設備故障報修' }, group: { id: 'group-1', name: '設備組', archived: false },
      createdBy: { id: 'account-1', displayName: '客服同仁' }, owner: { id: 'account-2', displayName: '阿明' },
      dueAt: '2099-10-09T01:00:00Z', resolution: null, cancelReason: null, createdAt: '2026-10-06T01:00:00Z',
      updatedAt: '2026-10-06T02:00:00Z', acceptedAt: '2026-10-06T02:00:00Z', completedAt: null, cancelledAt: null, eventCount: 4,
    },
    events: [],
    links: { record: null, thread: null, assistantIssue: null, previousCase: null },
    allowedActions,
    cancelReasonRequired,
  };
}

async function setup(value: CaseDetailView, act: Observable<CaseActionResult> = of({ status: 'ready', data: value })) {
  TestBed.resetTestingModule();
  const repo = { act: vi.fn(() => act) };
  await TestBed.configureTestingModule({
    imports: [CaseActionsComponent],
    providers: [{ provide: CasesRepository, useValue: repo }],
  }).compileComponents();
  const fixture = TestBed.createComponent(CaseActionsComponent);
  fixture.componentRef.setInput('detail', value);
  fixture.componentRef.setInput('groups', GROUPS);
  const changed = vi.fn();
  const refresh = vi.fn();
  fixture.componentInstance.changed.subscribe(changed);
  fixture.componentInstance.refresh.subscribe(refresh);
  fixture.detectChanges();
  return { fixture, repo, changed, refresh };
}

type Fixture = Awaited<ReturnType<typeof setup>>['fixture'];

function root(fixture: Fixture): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function click(fixture: Fixture, selector: string): void {
  const button = root(fixture).querySelector<HTMLButtonElement>(selector);
  if (!button) throw new Error(`missing ${selector}`);
  button.click();
  fixture.detectChanges();
}

function type(fixture: Fixture, selector: string, value: string, event = 'input'): void {
  const field = root(fixture).querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector);
  if (!field) throw new Error(`missing ${selector}`);
  field.value = value;
  field.dispatchEvent(new Event(event));
  fixture.detectChanges();
}

function submit(fixture: Fixture): void {
  root(fixture).querySelector('[data-case-action-form]')?.dispatchEvent(new Event('submit'));
  fixture.detectChanges();
}

describe('CaseActionsComponent', () => {
  it('shows only the allowed actions and needs a resolution before completing', async () => {
    const { fixture, repo, changed } = await setup(detail(['request-info', 'complete', 'cancel', 'transfer', 'set-due', 'comment']));
    expect([...root(fixture).querySelectorAll('[data-case-action]')].map((button) => button.textContent?.trim()))
      .toEqual(['要求補件', '完成', '取消案件', '轉組', '調整時限', '補充說明']);

    click(fixture, '[data-case-action="complete"]');
    submit(fixture);
    expect(repo.act).not.toHaveBeenCalled();
    expect(root(fixture).querySelector('#case-action-text-error')?.textContent).toContain('請填寫處理結果。');

    type(fixture, '#case-action-text', '  已更換壓縮機 ');
    submit(fixture);
    expect(repo.act).toHaveBeenCalledWith('case-1', 'complete', { eventCount: 4, resolution: '已更換壓縮機' });
    expect(changed).toHaveBeenCalledOnce();
    expect(root(fixture).querySelector('[data-case-action-form]')).toBeNull();
  });

  it('sends accept with one click', async () => {
    const { fixture, repo } = await setup(detail(['accept']));
    click(fixture, '[data-case-action="accept"]');
    expect(repo.act).toHaveBeenCalledWith('case-1', 'accept', { eventCount: 4 });
  });

  it('lets the creator cancel before acceptance without a reason', async () => {
    const { fixture, repo } = await setup(detail(['cancel', 'comment'], false));
    click(fixture, '[data-case-action="cancel"]');
    expect(root(fixture).querySelector('label[for="case-action-text"]')?.textContent).toContain('取消原因（選填）');
    submit(fixture);
    expect(repo.act).toHaveBeenCalledWith('case-1', 'cancel', { eventCount: 4 });
  });

  it('transfers only to another group', async () => {
    const { fixture, repo } = await setup(detail(['transfer']));
    click(fixture, '[data-case-action="transfer"]');
    const options = [...root(fixture).querySelectorAll<HTMLOptionElement>('#case-action-group option')].map((option) => option.textContent?.trim());
    expect(options).toEqual(['請選擇', '採購組']);
    type(fixture, '#case-action-text', '需要採購零件');
    submit(fixture);
    expect(repo.act).toHaveBeenCalledWith('case-1', 'transfer', { eventCount: 4, note: '需要採購零件', groupId: 'group-2' });
  });

  it('refuses a new due time in the past before sending', async () => {
    const { fixture, repo } = await setup(detail(['set-due']));
    click(fixture, '[data-case-action="set-due"]');
    expect((root(fixture).querySelector('#case-action-due') as HTMLInputElement).value).toBe(toDateTimeLocalValue(new Date('2099-10-09T01:00:00Z')));

    type(fixture, '#case-action-due', toDateTimeLocalValue(new Date(Date.now() - 3600 * 1000)));
    expect(root(fixture).querySelector('#case-action-due-error')?.textContent).toContain('時限不能早於現在。');
    submit(fixture);
    expect(repo.act).not.toHaveBeenCalled();

    const future = new Date(Date.now() + 48 * 3600 * 1000);
    type(fixture, '#case-action-due', toDateTimeLocalValue(future));
    submit(fixture);
    expect(repo.act).toHaveBeenCalledWith('case-1', 'set-due', { eventCount: 4, dueAt: new Date(toDateTimeLocalValue(future)).toISOString() });
  });

  it('asks to refresh after 409 and shows 403 case-action and a 422 under its field', async () => {
    const stale = await setup(detail(['accept']), of({ status: 'changed', message: '這件案件剛被其他人更新，請重新整理後再試。' }));
    click(stale.fixture, '[data-case-action="accept"]');
    expect(root(stale.fixture).querySelector('[data-case-action-message]')?.textContent).toContain('剛被其他人更新');
    click(stale.fixture, '[data-case-action-reload]');
    expect(stale.refresh).toHaveBeenCalledOnce();
    expect(stale.changed).not.toHaveBeenCalled();

    const denied = await setup(detail(['complete']), of({ status: 'permission-denied', reason: 'case-action', message: '你不能對這件案件執行這個動作。' }));
    click(denied.fixture, '[data-case-action="complete"]');
    type(denied.fixture, '#case-action-text', '已修好');
    submit(denied.fixture);
    expect(root(denied.fixture).querySelector('[data-case-action-message]')?.textContent).toContain('你不能對這件案件執行這個動作。');

    const refused = await setup(detail(['request-info']), of({
      status: 'validation-failed', reason: 'note-required', message: '請說明需要補充哪些資料。', fieldErrors: { note: '請說明需要補充哪些資料。' },
    }));
    click(refused.fixture, '[data-case-action="request-info"]');
    type(refused.fixture, '#case-action-text', '補');
    submit(refused.fixture);
    expect(root(refused.fixture).querySelector('#case-action-text-error')?.textContent).toContain('請說明需要補充哪些資料。');
  });
});
