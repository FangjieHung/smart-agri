import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { TeamPanelComponent } from './team-panel.component';

function render(accountId: AccountId = 'account-smb-admin') {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-23T02:00:00.000Z'),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(accountId) } },
  ];
  TestBed.configureTestingModule({ imports: [TeamPanelComponent], providers });
  const fixture = TestBed.createComponent(TeamPanelComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository };
}

function button(host: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(host.querySelectorAll('button')).find((candidate) =>
    candidate.textContent?.includes(text),
  );
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

describe('TeamPanelComponent', () => {
  it('lists every member with the role and what that role can do', () => {
    const { host } = render();
    const members = Array.from(host.querySelectorAll('.member'));

    expect(members).toHaveLength(3);
    expect(members[0].textContent).toContain('安心商行管理者');
    expect(members[0].textContent).toContain('目前的身分');
    expect(members[0].textContent).toContain('建立與設定助理');
    expect(members[1].textContent).toContain('安心商行客服同仁');
    expect(members[1].textContent).toContain('使用團隊分享的助理');
    // 沒有編輯器展開時只顯示目前的權限。
    expect(host.querySelectorAll('input[type="checkbox"]')).toHaveLength(0);
    expect(host.textContent).toContain('不是真實的身分管理');
  });

  it('refuses the whole panel for an account without manage-assistants', () => {
    const { host } = render('account-internal-employee');

    expect(host.querySelector('app-state-panel')?.textContent).toContain('無法查看團隊設定');
    expect(host.querySelectorAll('.member')).toHaveLength(0);
    expect(host.querySelector('button')).toBeNull();
    // 不洩漏其他成員的名字。
    expect(host.textContent).not.toContain('外部客戶');
  });

  it('says which permissions are actually checked and which are only declared', () => {
    const { fixture, host } = render();
    button(host, '變更 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    const boxes = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
    expect(boxes).toHaveLength(7);
    const editor = host.querySelector('.member__editor');
    expect(editor?.textContent).toContain('尚未接到行為');
    expect(editor?.textContent).toContain('canOpenInPlatform()');
  });

  it('saves a member’s permissions through the repository and confirms it', () => {
    const { fixture, host, repository } = render();
    button(host, '變更 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    const record = host.querySelector<HTMLInputElement>(
      '#permission-account-internal-employee-read-consented-submissions',
    );
    expect(record?.checked).toBe(true);
    record?.click();
    fixture.detectChanges();
    button(host, '儲存 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已更新 安心商行客服同仁 的權限');
    const accounts = repository.listAccounts();
    if (accounts.status !== 'ready') throw new Error('expected ready');
    expect(
      accounts.data.find((account) => account.id === 'account-internal-employee')?.permissions,
    ).toEqual(['use-shared-assistants']);
  });

  it('will not let the acting admin uncheck their own manage-assistants', () => {
    const { fixture, host } = render();
    button(host, '變更 安心商行管理者 的權限').click();
    fixture.detectChanges();

    const own = host.querySelector<HTMLInputElement>(
      '#permission-account-smb-admin-manage-assistants',
    );
    expect(own?.disabled).toBe(true);
    expect(host.querySelector('.member__editor')?.textContent).toContain('不可移除');
  });
});
