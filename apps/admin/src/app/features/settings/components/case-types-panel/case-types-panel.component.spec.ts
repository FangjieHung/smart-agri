import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { dueHoursFromInput, dueInputFromHours, formatDueHours } from '../../../../core/domain/case-due-time';
import { CaseSettingsRepository } from '../../../../core/repositories/case-settings.repository';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { CaseTypesPanelComponent } from './case-types-panel.component';

type Fixture = { detectChanges(): void; whenStable(): Promise<unknown> };

async function render(accountId: AccountId = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({
    imports: [CaseTypesPanelComponent],
    providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }],
  });
  const fixture = TestBed.createComponent(CaseTypesPanelComponent);
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: TestBed.inject(CaseSettingsRepository) };
}

async function settle(fixture: Fixture): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function typeRow(host: HTMLElement, name: string): HTMLElement {
  const element = host.querySelector<HTMLElement>(`[data-case-type="${name}"]`);
  if (!element) throw new Error(`missing type ${name}`);
  return element;
}

function required<T extends Element>(scope: ParentNode, selector: string): T {
  const element = scope.querySelector<T>(selector);
  if (!element) throw new Error(`missing ${selector}`);
  return element;
}

function button(scope: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(scope.querySelectorAll<HTMLButtonElement>('button')).find((candidate) =>
    candidate.textContent?.trim() === text);
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

function type(element: Element, value: string): void {
  const input = element as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function choose(element: Element, value: string): void {
  const select = element as HTMLSelectElement;
  select.value = value;
  select.dispatchEvent(new Event('change'));
}

function status(host: HTMLElement): string {
  return host.querySelector('[role="status"]')?.textContent?.trim() ?? '';
}

describe('case due time conversion (issue #247)', () => {
  it('turns days into hours and keeps the 1–2,160 hour range', () => {
    expect(dueHoursFromInput('3', 'days')).toBe(72);
    expect(dueHoursFromInput(' 90 ', 'days')).toBe(2160);
    expect(dueHoursFromInput('91', 'days')).toBe('range');
    expect(dueHoursFromInput('0', 'days')).toBe('range');
    expect(dueHoursFromInput('1', 'hours')).toBe(1);
    expect(dueHoursFromInput('2160', 'hours')).toBe(2160);
    expect(dueHoursFromInput('2161', 'hours')).toBe('range');
    expect(dueHoursFromInput('0', 'hours')).toBe('range');
    expect(dueHoursFromInput('1.5', 'days')).toBe('integer');
    expect(dueHoursFromInput('', 'hours')).toBe('integer');
    expect(dueHoursFromInput('-3', 'hours')).toBe('integer');

    expect(dueInputFromHours(72)).toEqual({ value: '3', unit: 'days' });
    expect(dueInputFromHours(36)).toEqual({ value: '36', unit: 'hours' });
    expect([formatDueHours(72), formatDueHours(36), formatDueHours(2160)]).toEqual(['3 天', '36 小時', '90 天']);
  });
});

describe('CaseTypesPanelComponent (issue #247)', () => {
  it('is shown to the manager only, with inactive types and their defaults', async () => {
    const admin = await render();
    expect(admin.host.querySelector('[data-case-types-panel]')).not.toBeNull();
    expect(typeRow(admin.host, '設備故障報修').textContent).toContain('預設承辦組：設備組');
    expect(typeRow(admin.host, '設備故障報修').textContent).toContain('預設處理時限：3 天');
    const stocktake = typeRow(admin.host, '倉儲盤點差異').textContent?.replace(/\s+/g, ' ') ?? '';
    expect(stocktake).toContain('已停用');
    expect(stocktake).toContain('舊倉儲組（已封存）');
    expect(stocktake).toContain('36 小時');
    TestBed.resetTestingModule();

    for (const accountId of ['account-internal-employee', 'account-external-customer']) {
      const { host } = await render(accountId);
      expect(host.querySelector('[data-case-types-panel]'), accountId).toBeNull();
      TestBed.resetTestingModule();
    }
  });

  it('creates a type with the handling time entered in days and sends it in hours', async () => {
    const { fixture, host, repository } = await render();
    const create = vi.spyOn(repository, 'createCaseType');

    button(host, '新增案件類型').click();
    await settle(fixture);
    const form = required<HTMLFormElement>(host, '[data-case-type-form]');
    const groups = Array.from(required<HTMLSelectElement>(form, '#case-type-group').options).map((option) => option.textContent?.trim());
    expect(groups).toEqual(['設備組', '採購組']);
    type(required(form, '#case-type-name'), '冷藏庫異常');
    type(required(form, '#case-type-description'), '冷藏庫溫度超過設定值。');
    choose(required(form, '#case-type-group'), 'case-group-purchasing');
    type(required(form, '#case-type-due'), '5');
    await settle(fixture);
    expect(required(form, '#case-type-due-hint').textContent).toContain('共 120 小時');
    button(form, '建立案件類型').click();
    await settle(fixture);

    expect(create).toHaveBeenCalledWith({
      name: '冷藏庫異常', description: '冷藏庫溫度超過設定值。', defaultGroupId: 'case-group-purchasing', defaultDueHours: 120, isActive: true,
    });
    expect(status(host)).toBe('已建立案件類型「冷藏庫異常」。');
    expect(typeRow(host, '冷藏庫異常').textContent).toContain('預設處理時限：5 天');
    expect(typeRow(host, '冷藏庫異常').textContent).toContain('預設承辦組：採購組');
  });

  it('enters the time in hours, and shows a time out of range or not an integer under the field without sending', async () => {
    const { fixture, host, repository } = await render();
    const create = vi.spyOn(repository, 'createCaseType');

    button(host, '新增案件類型').click();
    await settle(fixture);
    const form = required<HTMLFormElement>(host, '[data-case-type-form]');
    type(required(form, '#case-type-name'), '急件');
    type(required(form, '#case-type-due'), '91');
    button(form, '建立案件類型').click();
    await settle(fixture);
    expect(required(host, '#case-type-due-error').textContent).toContain('預設處理時限請在 1 到 2,160 小時（90 天）之間。');
    expect(required(host, '#case-type-due').getAttribute('aria-invalid')).toBe('true');

    type(required(host, '#case-type-due'), '1.5');
    button(required(host, '[data-case-type-form]'), '建立案件類型').click();
    await settle(fixture);
    expect(required(host, '#case-type-due-error').textContent).toContain('預設處理時限請輸入整數。');
    expect(create).not.toHaveBeenCalled();

    choose(required(host, '#case-type-due-unit'), 'hours');
    type(required(host, '#case-type-due'), '2160');
    button(required(host, '[data-case-type-form]'), '建立案件類型').click();
    await settle(fixture);
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: '急件', defaultDueHours: 2160 }));
    expect(typeRow(host, '急件').textContent).toContain('90 天');
  });

  it('shows the server\'s field errors under their fields (blank name, taken name, description too long)', async () => {
    const { fixture, host } = await render();

    button(host, '新增案件類型').click();
    await settle(fixture);
    type(required(host, '#case-type-description'), '說'.repeat(501));
    button(required(host, '[data-case-type-form]'), '建立案件類型').click();
    await settle(fixture);
    expect(required(host, '#case-type-name-error').textContent).toContain('請輸入案件類型名稱。');
    expect(required(host, '#case-type-description-error').textContent).toContain('說明請在 500 個字以內。');

    type(required(host, '#case-type-name'), '設備故障報修');
    type(required(host, '#case-type-description'), '');
    button(required(host, '[data-case-type-form]'), '建立案件類型').click();
    await settle(fixture);
    expect(required(host, '#case-type-name-error').textContent).toContain('已經有同名的案件類型');
    expect(host.querySelector('#case-type-description-error')).toBeNull();
  });

  it('edits a type in place, deactivates it and reactivates it', async () => {
    const { fixture, host } = await render();

    button(typeRow(host, '設備故障報修'), '編輯「設備故障報修」').click();
    await settle(fixture);
    const form = required<HTMLFormElement>(typeRow(host, '設備故障報修'), '[data-case-type-form]');
    expect(required<HTMLInputElement>(form, '#case-type-due').value).toBe('3');
    expect(required<HTMLSelectElement>(form, '#case-type-due-unit').value).toBe('days');
    choose(required(form, '#case-type-due-unit'), 'hours');
    type(required(form, '#case-type-due'), '36');
    button(form, '儲存案件類型').click();
    await settle(fixture);
    expect(status(host)).toBe('已儲存案件類型「設備故障報修」。');
    expect(typeRow(host, '設備故障報修').textContent).toContain('36 小時');

    button(typeRow(host, '設備故障報修'), '停用「設備故障報修」').click();
    await settle(fixture);
    expect(status(host)).toContain('已停用「設備故障報修」');
    expect(typeRow(host, '設備故障報修').textContent).toContain('已停用');

    button(typeRow(host, '設備故障報修'), '重新啟用「設備故障報修」').click();
    await settle(fixture);
    expect(status(host)).toContain('已重新啟用「設備故障報修」');
  });

  it('will not reactivate a type whose default group is archived, and says why', async () => {
    const { fixture, host } = await render();

    button(typeRow(host, '倉儲盤點差異'), '重新啟用「倉儲盤點差異」').click();
    await settle(fixture);

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('這個承辦組已封存，請選擇其他承辦組。');
    expect(typeRow(host, '倉儲盤點差異').textContent).toContain('已停用');

    // Editing it lists the archived group as its current choice, marked as archived.
    button(typeRow(host, '倉儲盤點差異'), '編輯「倉儲盤點差異」').click();
    await settle(fixture);
    const select = required<HTMLSelectElement>(host, '#case-type-group');
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual(['設備組', '採購組', '舊倉儲組（已封存）']);
    expect(select.value).toBe('case-group-old-warehouse');
  });
});
