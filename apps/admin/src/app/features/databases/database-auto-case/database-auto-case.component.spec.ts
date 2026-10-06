import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import type { DatabaseAutoCaseOptionView } from '../../../core/domain/case-settings.model';
import type { DatabaseDetailView } from '../../../core/domain/database.model';
import type { DemoRepository } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { autoCaseReadHint, DatabaseAutoCaseComponent } from './database-auto-case.component';

type Fixture = { detectChanges(): void; whenStable(): Promise<unknown> };

/** 用 Demo 模式的 `CaseSettingsRepository`（與 API 相同的契約）；mock 數據庫目前只有管理者讀得到紀錄。 */
async function render(accountId: AccountId = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  const detail = {
    summary: { id: 'database-customer-records', name: '客戶資料庫' },
    access: { effectiveReaders: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }] },
  } as unknown as DatabaseDetailView;
  const demo: Pick<DemoRepository, 'getDatabaseDetail'> = { getDatabaseDetail: () => of({ status: 'ready', data: detail }) };
  TestBed.configureTestingModule({
    imports: [DatabaseAutoCaseComponent],
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: DEMO_REPOSITORY, useValue: demo },
    ],
  });
  const fixture = TestBed.createComponent(DatabaseAutoCaseComponent);
  fixture.componentRef.setInput('databaseId', 'database-customer-records');
  fixture.componentRef.setInput('readerKey', 'account-smb-admin');
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

async function settle(fixture: Fixture): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function required<T extends Element>(scope: ParentNode, selector: string): T {
  const element = scope.querySelector<T>(selector);
  if (!element) throw new Error(`missing ${selector}`);
  return element;
}

function choose(host: HTMLElement, value: string): void {
  const select = required<HTMLSelectElement>(host, '#auto-case-type');
  select.value = value;
  select.dispatchEvent(new Event('change'));
}

function option(overrides: Partial<DatabaseAutoCaseOptionView>): DatabaseAutoCaseOptionView {
  return {
    id: 'type-1', name: '設備故障報修', group: { id: 'group-1', name: '設備組', archived: false }, defaultDueHours: 72,
    memberCount: 2, unreadableMemberCount: 0, ...overrides,
  };
}

describe('autoCaseReadHint (issue #255)', () => {
  it('says how many members of the group cannot read the records', () => {
    expect(autoCaseReadHint(option({ unreadableMemberCount: 2 }))).toBe(
      '承辦組中有 2 人無法讀取這個數據庫的紀錄。他們看得到案件，但要開啟紀錄，仍需由擁有者指定為資料管理者，且帳號具備「查看同意提交的紀錄」權限。');
    expect(autoCaseReadHint(option({}))).toBe('承辦組「設備組」的成員都能讀取這個數據庫的紀錄。');
    expect(autoCaseReadHint(option({ memberCount: 0 }))).toBe('承辦組「設備組」目前沒有成員，案件開出後沒有人能受理。');
  });
});

describe('DatabaseAutoCaseComponent (issue #255)', () => {
  it('lets the manager choose an active type, warns about the members who cannot read the records, and saves', async () => {
    const { fixture, host } = await render();

    const form = required<HTMLElement>(host, '[data-database-auto-case]');
    expect(form.querySelector('h3')?.textContent).toBe('送出後自動開案');
    expect(form.textContent).toContain('不會複製紀錄內容');
    const select = required<HTMLSelectElement>(host, '#auto-case-type');
    expect(Array.from(select.options).map((item) => item.textContent?.trim())).toEqual(['不自動開案', '設備故障報修（設備組・3 天）']);
    expect(select.value).toBe('');
    expect(host.querySelector('[data-auto-case-read-hint]')).toBeNull();
    const save = required<HTMLButtonElement>(host, 'button[type="submit"]');
    expect(save.disabled).toBe(true);

    choose(host, 'case-type-equipment-repair');
    await settle(fixture);
    expect(required(host, '[data-auto-case-read-hint]').textContent?.trim()).toBe(
      '承辦組中有 1 人無法讀取這個數據庫的紀錄。他們看得到案件，但要開啟紀錄，仍需由擁有者指定為資料管理者，且帳號具備「查看同意提交的紀錄」權限。');
    expect(save.disabled).toBe(false);

    save.click();
    await settle(fixture);
    expect(required(host, '.feedback').textContent?.trim()).toBe('已設定：每筆新紀錄送出後，自動建立一件「設備故障報修」案件。');
    expect(required<HTMLSelectElement>(host, '#auto-case-type').value).toBe('case-type-equipment-repair');
    expect(required<HTMLButtonElement>(host, 'button[type="submit"]').disabled).toBe(true);

    choose(host, '');
    await settle(fixture);
    required<HTMLButtonElement>(host, 'button[type="submit"]').click();
    await settle(fixture);
    expect(required(host, '.feedback').textContent?.trim()).toBe('已關閉送出後自動開案。');
  });

  it('is not shown to anyone but the manager', async () => {
    for (const accountId of ['account-internal-employee', 'account-external-customer'] as const) {
      TestBed.resetTestingModule();
      const { host } = await render(accountId);
      expect(host.querySelector('[data-database-auto-case]')).toBeNull();
      expect(host.textContent?.trim()).toBe('');
    }
  });
});
