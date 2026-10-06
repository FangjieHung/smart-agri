import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { CaseSettingsRepository } from '../../../../core/repositories/case-settings.repository';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { CaseGroupsPanelComponent } from './case-groups-panel.component';

type Fixture = { detectChanges(): void; whenStable(): Promise<unknown> };

async function render(accountId: AccountId = 'account-smb-admin', providers: Provider[] = []) {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({
    imports: [CaseGroupsPanelComponent],
    providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }, ...providers],
  });
  const fixture = TestBed.createComponent(CaseGroupsPanelComponent);
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: TestBed.inject(CaseSettingsRepository) };
}

async function settle(fixture: Fixture): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function group(host: HTMLElement, name: string): HTMLElement {
  const element = host.querySelector<HTMLElement>(`[data-case-group="${name}"]`);
  if (!element) throw new Error(`missing group ${name}`);
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

function type(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function status(host: HTMLElement): string {
  return host.querySelector('[role="status"]')?.textContent?.trim() ?? '';
}

describe('CaseGroupsPanelComponent (issue #246)', () => {
  it('is shown to the manager and to nobody else', async () => {
    const admin = await render();
    expect(admin.host.querySelector('[data-case-groups-panel]')).not.toBeNull();
    expect(admin.host.textContent).toContain('設備組');
    expect(admin.host.textContent).toContain('舊倉儲組');
    expect(group(admin.host, '舊倉儲組').textContent).toContain('已封存');
    TestBed.resetTestingModule();

    for (const accountId of ['account-internal-employee', 'account-external-customer']) {
      const { host } = await render(accountId);
      expect(host.querySelector('[data-case-groups-panel]'), accountId).toBeNull();
      TestBed.resetTestingModule();
    }
  });

  it('creates a group and shows a blank, too long or taken name under the field', async () => {
    const { fixture, host } = await render();
    const input = required<HTMLInputElement>(host, '#case-group-new-name');
    const create = button(host, '建立承辦組');

    create.click();
    await settle(fixture);
    expect(host.querySelector('#case-group-new-name-error')?.textContent).toContain('請輸入承辦組名稱。');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('case-group-new-name-error');

    type(input, '組'.repeat(41));
    create.click();
    await settle(fixture);
    expect(host.querySelector('#case-group-new-name-error')?.textContent).toContain('承辦組名稱請在 40 個字以內。');

    type(input, ' 設備組 ');
    create.click();
    await settle(fixture);
    expect(host.querySelector('#case-group-new-name-error')?.textContent).toContain('已經有同名的承辦組');

    type(input, '品保組');
    create.click();
    await settle(fixture);
    expect(status(host)).toBe('已建立承辦組「品保組」。');
    expect(group(host, '品保組').textContent).toContain('尚無成員');
    expect(input.value).toBe('');
  });

  it('renames, archives and unarchives a group back and forth', async () => {
    const { fixture, host } = await render();

    button(group(host, '採購組'), '改名「採購組」').click();
    await settle(fixture);
    const renameInput = required<HTMLInputElement>(group(host, '採購組'), 'input[type="text"]');
    type(renameInput, '採購與倉儲組');
    button(group(host, '採購組'), '儲存名稱').click();
    await settle(fixture);
    expect(status(host)).toBe('已將承辦組改名為「採購與倉儲組」。');

    button(group(host, '採購與倉儲組'), '封存「採購與倉儲組」').click();
    await settle(fixture);
    expect(status(host)).toContain('已封存「採購與倉儲組」');
    expect(group(host, '採購與倉儲組').textContent).toContain('已封存');

    button(group(host, '採購與倉儲組'), '取消封存「採購與倉儲組」').click();
    await settle(fixture);
    expect(status(host)).toContain('已取消封存「採購與倉儲組」');
    expect(group(host, '採購與倉儲組').textContent).not.toContain('已封存');
  });

  it('edits the members from internal accounts only and lists each change in the history', async () => {
    const { fixture, host } = await render();

    button(group(host, '設備組'), '編輯「設備組」的成員').click();
    await settle(fixture);
    const editor = required<HTMLElement>(host, '[data-case-group-members-editor]');
    const labels = Array.from(editor.querySelectorAll('label')).map((label) => label.textContent?.replace(/\s+/g, ' ').trim());
    expect(labels).toEqual(['安心商行管理者 管理者', '安心商行客服同仁 內部同仁']);
    expect(editor.textContent).not.toContain('外部客戶');

    const boxes = editor.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
    expect(Array.from(boxes).map((box) => box.checked)).toEqual([false, true]);
    boxes[0].click();
    boxes[1].click();
    button(editor, '儲存成員').click();
    await settle(fixture);
    expect(status(host)).toBe('已儲存「設備組」的成員（1 人）。');
    expect(group(host, '設備組').textContent).toContain('成員：安心商行管理者');

    const historyToggle = button(group(host, '設備組'), '「設備組」的成員異動');
    historyToggle.click();
    await settle(fixture);
    expect(historyToggle.getAttribute('aria-expanded')).toBe('true');
    const rows = Array.from(host.querySelectorAll('[data-case-group-history] li')).map((row) => row.textContent?.replace(/\s+/g, ' ').trim());
    expect(rows).toHaveLength(3);
    expect(rows[0]).toMatch(/安心商行管理者 (加入了 安心商行管理者|移除了 安心商行客服同仁)/);
    expect(rows[2]).toContain('加入了 安心商行客服同仁');
  });

  it('shows a member list the server refuses and changes nothing', async () => {
    const refused = vi.fn(() => of({ status: 'validation-failed' as const, message: '成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。' }));
    const { fixture, host, repository } = await render();
    repository.updateCaseGroupMembers = refused;

    button(group(host, '設備組'), '編輯「設備組」的成員').click();
    await settle(fixture);
    button(required<HTMLElement>(host, '[data-case-group-members-editor]'), '儲存成員').click();
    await settle(fixture);

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('成員只能是組織內部的帳號');
    expect(host.querySelector('[data-case-group-members-editor]')).not.toBeNull();
    expect(group(host, '設備組').textContent).toContain('成員：安心商行客服同仁');
  });
});
