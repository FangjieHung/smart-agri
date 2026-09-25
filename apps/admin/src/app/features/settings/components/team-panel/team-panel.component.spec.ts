import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import type { TeamView } from '../../../../core/domain/team.model';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { TeamPanelComponent } from './team-panel.component';

async function render(accountId: AccountId = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-23T02:00:00.000Z'),
    viewer: () => activeAccountId(),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];
  TestBed.configureTestingModule({ imports: [TeamPanelComponent], providers });
  const fixture = TestBed.createComponent(TeamPanelComponent);
  // 非同步契約：第一次畫面是 loading，resource 讀完才出現成員。
  await fixture.whenStable();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository };
}

/** 只換掉 `getTeam`，用來觀察請求還沒回來、或失敗時的畫面。 */
async function renderWith(getTeam: () => ReturnType<MockDemoRepository['getTeam']>) {
  const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
  vi.spyOn(repository, 'getTeam').mockImplementation(getTeam);
  TestBed.configureTestingModule({
    imports: [TeamPanelComponent],
    providers: [
      { provide: DEMO_REPOSITORY, useValue: repository },
      {
        provide: DemoSessionService,
        useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') },
      },
    ],
  });
  const fixture = TestBed.createComponent(TeamPanelComponent);
  // 不用 whenStable：請求還沒回來時 resource 會讓它一直等下去。
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function button(host: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(host.querySelectorAll('button')).find((candidate) =>
    candidate.textContent?.includes(text),
  );
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

describe('TeamPanelComponent', () => {
  it('shows the loading state until the team arrives', async () => {
    const response = new Subject<RepositoryView<TeamView>>();
    const { fixture, host } = await renderWith(() => response);

    expect(host.querySelector('app-state-panel')?.textContent).toContain('正在載入團隊成員');

    const team = await firstReady();
    response.next(team);
    response.complete();
    await fixture.whenStable();
    expect(host.querySelectorAll('.member')).toHaveLength(3);
  });

  it('shows an error instead of an endless spinner when the team cannot be read', async () => {
    const { fixture, host } = await renderWith(() => throwError(() => new Error('503')));
    await fixture.whenStable();

    expect(host.querySelector('app-state-panel')?.textContent).toContain('目前無法載入團隊成員');
    expect(host.querySelectorAll('.member')).toHaveLength(0);
  });

  it('lists every member with the role and what that role can do', async () => {
    const { host } = await render();
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

  it('refuses the whole panel for an account without manage-assistants', async () => {
    const { host } = await render('account-internal-employee');

    expect(host.querySelector('app-state-panel')?.textContent).toContain('無法查看團隊設定');
    expect(host.querySelectorAll('.member')).toHaveLength(0);
    expect(host.querySelector('button')).toBeNull();
    // 不洩漏其他成員的名字。
    expect(host.textContent).not.toContain('外部客戶');
  });

  it('says which permissions are actually checked and which are only declared', async () => {
    const { fixture, host } = await render();
    button(host, '變更 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    const boxes = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
    expect(boxes).toHaveLength(7);
    const editor = host.querySelector('.member__editor');
    expect(editor?.textContent).toContain('尚未接到行為');
    expect(editor?.textContent).toContain('canOpenInPlatform()');
  });

  it('saves a member’s permissions through the repository, confirms it and reloads', async () => {
    const { fixture, host, repository } = await render();
    const getTeam = vi.spyOn(repository, 'getTeam');
    button(host, '變更 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    const record = host.querySelector<HTMLInputElement>(
      '#permission-account-internal-employee-read-consented-submissions',
    );
    expect(record?.checked).toBe(true);
    record?.click();
    fixture.detectChanges();
    button(host, '儲存 安心商行客服同仁 的權限').click();
    await fixture.whenStable();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已更新 安心商行客服同仁 的權限');
    // 儲存成功後重新讀取團隊，而不是遞增 revision。
    expect(getTeam).toHaveBeenCalledOnce();
    expect(host.querySelectorAll('.member')[1].textContent).not.toContain('查看同意提交的紀錄');
    const accounts = repository.listAccounts();
    if (accounts.status !== 'ready') throw new Error('expected ready');
    expect(
      accounts.data.find((account) => account.id === 'account-internal-employee')?.permissions,
    ).toEqual(['use-shared-assistants']);
  });

  it('will not let the acting admin uncheck their own manage-assistants', async () => {
    const { fixture, host } = await render();
    button(host, '變更 安心商行管理者 的權限').click();
    fixture.detectChanges();

    const own = host.querySelector<HTMLInputElement>(
      '#permission-account-smb-admin-manage-assistants',
    );
    expect(own?.disabled).toBe(true);
    expect(host.querySelector('.member__editor')?.textContent).toContain('不可移除');
  });
});

/** 管理者看到的真實團隊資料，讓替換掉的 `getTeam` 回傳可以渲染的內容。 */
function firstReady(): Promise<RepositoryView<TeamView>> {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    viewer: () => 'account-smb-admin',
  });
  return firstValueFrom(repository.getTeam());
}
