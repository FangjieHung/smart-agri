import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AssistantIssueOpenedCaseView, OpenCaseFromIssueResult } from '../../../core/domain/assistant-issue.model';
import { toDateTimeLocalValue } from '../../../core/domain/case-due-time';
import type { CaseGroupListView, CaseTypeListView } from '../../../core/domain/case-settings.model';
import { AssistantIssuesRepository } from '../../../core/repositories/assistant-issues.repository';
import { CaseSettingsRepository } from '../../../core/repositories/case-settings.repository';
import { OpenCaseDialogComponent } from './open-case-dialog.component';

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

const OPENED = { caseId: 'case-9', issue: { issue: {}, events: [], linkedCase: { caseId: 'case-9', canOpen: true } } } as unknown as AssistantIssueOpenedCaseView;

async function render(result: OpenCaseFromIssueResult = { status: 'ready', data: OPENED }, question: string | null = '冷藏庫的溫度一直降不下來？') {
  TestBed.resetTestingModule();
  const openCase = vi.fn(() => of(result));
  await TestBed.configureTestingModule({
    imports: [OpenCaseDialogComponent],
    providers: [
      { provide: AssistantIssuesRepository, useValue: { openCase } },
      {
        provide: CaseSettingsRepository,
        useValue: {
          listCaseTypes: () => of({ status: 'ready', data: TYPES }),
          listCaseGroups: () => of({ status: 'ready', data: GROUPS }),
        },
      },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OpenCaseDialogComponent);
  fixture.componentRef.setInput('issueId', 'issue-1');
  fixture.componentRef.setInput('issueTitle', `  ${'冷'.repeat(130)}  `);
  fixture.componentRef.setInput('question', question);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const field = <T extends HTMLElement>(selector: string): T => {
    const found = host.querySelector<T>(selector);
    if (!found) throw new Error(`missing ${selector}`);
    return found;
  };
  const set = (selector: string, value: string, event = 'change') => {
    const input = field<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(selector);
    input.value = value;
    input.dispatchEvent(new Event(event));
    fixture.detectChanges();
  };
  return { fixture, host, field, set, openCase };
}

describe('OpenCaseDialogComponent (issue #252)', () => {
  it('is a labelled modal dialog that prefills the issue title (cut to 120) and the question copy', async () => {
    const { host, field } = await render();
    const dialog = field('[role="dialog"]');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(host.querySelector('#open-case-confirm-title')?.textContent).toBe('另開案件');
    expect(host.querySelector('#open-case-confirm-detail')?.textContent).toContain('非助理問題');
    expect(field<HTMLInputElement>('#issue-case-title').value).toBe('冷'.repeat(120));
    expect(field<HTMLTextAreaElement>('#issue-case-description').value).toBe('冷藏庫的溫度一直降不下來？');
    expect(host.querySelector('.confirm-open-case')?.textContent).toBe('建立案件並結案');
  });

  it('fills in the group and due time from the type, and sends the edited fields with the dialog\'s options', async () => {
    const { fixture, host, field, set, openCase } = await render();
    const opened = vi.fn();
    fixture.componentInstance.opened.subscribe(opened);

    const before = Date.now();
    set('#issue-case-type', 'type-1');
    expect(field<HTMLSelectElement>('#issue-case-group').value).toBe('group-1');
    const due = new Date(field<HTMLInputElement>('#issue-case-due').value).getTime();
    expect(due).toBeGreaterThanOrEqual(new Date(toDateTimeLocalValue(new Date(before + 72 * 3600_000))).getTime());
    set('#issue-case-group', 'group-2');
    set('#issue-case-title', '派人檢查冷藏庫', 'input');
    set('#issue-case-description', '  請到場檢查。 ', 'input');
    host.querySelector<HTMLButtonElement>('.confirm-open-case')?.click();

    expect(openCase).toHaveBeenCalledTimes(1);
    expect(openCase).toHaveBeenCalledWith(
      'issue-1',
      expect.objectContaining({ typeId: 'type-1', groupId: 'group-2', title: '派人檢查冷藏庫', description: '請到場檢查。' }),
      { activeTypeIds: ['type-1'], groupIds: ['group-1', 'group-2'] },
    );
    expect(opened).toHaveBeenCalledWith(OPENED);
  });

  it('checks the required fields and a due time in the past before sending', async () => {
    const { fixture, host, set, openCase } = await render();
    set('#issue-case-title', '  ', 'input');
    host.querySelector<HTMLButtonElement>('.confirm-open-case')?.click();
    fixture.detectChanges();
    expect(openCase).not.toHaveBeenCalled();
    expect(host.querySelector('#issue-case-type-error')?.textContent).toBe('請選擇案件類型。');
    expect(host.querySelector('#issue-case-title-error')?.textContent).toBe('請輸入案件標題。');

    set('#issue-case-type', 'type-1');
    set('#issue-case-due', toDateTimeLocalValue(new Date(Date.now() - 3600_000)), 'input');
    expect(host.querySelector('#issue-case-due-error')?.textContent).toBe('時限不能早於現在。');
    expect(host.querySelector('#issue-case-due')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('shows the API\'s refusal under its field and hands a conflict to the page', async () => {
    const refused = await render({
      status: 'validation-failed', reason: 'case-type-inactive', message: '這個案件類型已停用或不存在，請選擇其他類型。',
      fieldErrors: { typeId: '這個案件類型已停用或不存在，請選擇其他類型。' },
    });
    refused.set('#issue-case-type', 'type-1');
    refused.host.querySelector<HTMLButtonElement>('.confirm-open-case')?.click();
    refused.fixture.detectChanges();
    expect(refused.host.querySelector('#issue-case-type-error')?.textContent).toBe('這個案件類型已停用或不存在，請選擇其他類型。');
    expect(refused.host.querySelector('[role="alert"]')?.textContent).toContain('已停用');

    const conflicted = await render({ status: 'conflict', reason: 'case-already-opened', message: '這個處理事項已經另開過案件。', caseId: 'case-3' });
    const onConflict = vi.fn();
    conflicted.fixture.componentInstance.conflicted.subscribe(onConflict);
    conflicted.set('#issue-case-type', 'type-1');
    conflicted.host.querySelector<HTMLButtonElement>('.confirm-open-case')?.click();
    expect(onConflict).toHaveBeenCalledWith({ message: '這個處理事項已經另開過案件。', caseId: 'case-3' });
  });

  it('cancels with the cancel button and Escape', async () => {
    const { fixture, host, field } = await render(undefined, null);
    const cancelled = vi.fn();
    fixture.componentInstance.cancelled.subscribe(cancelled);
    expect(field<HTMLTextAreaElement>('#issue-case-description').value).toBe('');
    host.querySelector<HTMLButtonElement>('.confirm-cancel')?.click();
    field('[role="dialog"]').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(cancelled).toHaveBeenCalledTimes(2);
  });
});
