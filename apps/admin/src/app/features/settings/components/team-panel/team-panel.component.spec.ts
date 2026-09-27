import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogState } from '@angular/material/dialog';
import { firstValueFrom, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import type { TeamView } from '../../../../core/domain/team.model';
import type { CreateMemberResult, RepositoryView } from '../../../../core/repositories/demo-repository';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { ApiSessionService } from '../../../../core/session/api-session.service';
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

/** 對話框內容由 CDK overlay 掛在 `document.body`，不在元件自己的 host 底下。 */
function documentButton(text: string): HTMLButtonElement {
  const found = Array.from(document.querySelectorAll('button')).find((candidate) =>
    candidate.textContent?.includes(text),
  );
  if (!found) throw new Error(`missing button ${text} in document`);
  return found;
}

function typeInto(selector: string, value: string): void {
  const field = document.querySelector<HTMLInputElement | HTMLSelectElement>(selector);
  if (!field) throw new Error(`missing ${selector}`);
  field.value = value;
  field.dispatchEvent(new Event(field.tagName === 'SELECT' ? 'change' : 'input'));
}

async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
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

  it('re-reads the signed-in identity after saving the viewer’s own permissions', async () => {
    const { fixture, host } = await render();
    const refreshIdentity = vi
      .spyOn(TestBed.inject(ApiSessionService), 'refreshIdentity')
      .mockResolvedValue(undefined);
    button(host, '變更 安心商行管理者 的權限').click();
    fixture.detectChanges();

    host.querySelector<HTMLInputElement>('#permission-account-smb-admin-manage-publishing')?.click();
    fixture.detectChanges();
    button(host, '儲存 安心商行管理者 的權限').click();
    await fixture.whenStable();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已更新 安心商行管理者 的權限');
    expect(refreshIdentity).toHaveBeenCalledOnce();
  });

  it('does not re-read the signed-in identity after saving someone else’s permissions', async () => {
    const { fixture, host } = await render();
    const refreshIdentity = vi
      .spyOn(TestBed.inject(ApiSessionService), 'refreshIdentity')
      .mockResolvedValue(undefined);
    button(host, '變更 安心商行客服同仁 的權限').click();
    fixture.detectChanges();

    host
      .querySelector<HTMLInputElement>('#permission-account-internal-employee-read-consented-submissions')
      ?.click();
    fixture.detectChanges();
    button(host, '儲存 安心商行客服同仁 的權限').click();
    await fixture.whenStable();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已更新 安心商行客服同仁 的權限');
    expect(refreshIdentity).not.toHaveBeenCalled();
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

describe('TeamPanelComponent adding a member (issue #52)', () => {
  it('creates a member from the dialog, reloads the team and shows the one-time password once', async () => {
    const { fixture, host } = await render();

    button(host, '新增成員').click();
    await settle(fixture);

    typeInto('#new-member-login-name', 'new-hire');
    typeInto('#new-member-display-name', '新進同仁');
    typeInto('#new-member-role', 'internal-employee');
    document
      .querySelector<HTMLFormElement>('.create-panel')
      ?.dispatchEvent(new Event('submit'));
    await settle(fixture);

    // 新增對話框開始關閉（動畫結束前 DOM 可能還在，用對話框自己的狀態判斷），
    // 換成顯示一次性密碼的對話框；一開始的新增回饋也出現。
    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain('已新增成員「新進同仁」');
    const passwordField = document.querySelector<HTMLInputElement>('#new-member-password');
    expect(passwordField?.value).toMatch(/^Demo-/);
    expect(document.body.textContent).toContain('已新增「新進同仁」');
    const dialogStates = TestBed.inject(MatDialog).openDialogs.map((ref) => ref.getState());
    expect(dialogStates).toContain(MatDialogState.OPEN);

    // 新成員確實併入團隊清單。
    expect(host.textContent).toContain('新進同仁');

    documentButton('關閉').click();
    await settle(fixture);
    // 密碼對話框關閉後不再顯示於任何開著的對話框（一次性顯示，不留備份）。
    expect(TestBed.inject(MatDialog).openDialogs.map((ref) => ref.getState())).not.toContain(MatDialogState.OPEN);
  });

  it('offers 新增成員 only to an account that can see the team panel at all', async () => {
    const { host } = await render('account-internal-employee');
    expect(
      Array.from(host.querySelectorAll('button')).some((candidate) => candidate.textContent?.includes('新增成員')),
    ).toBe(false);
  });

  it('requires a login name and a display name before calling the repository', async () => {
    const { fixture, host, repository } = await render();
    const create = vi.spyOn(repository, 'createMember');

    button(host, '新增成員').click();
    await settle(fixture);
    document.querySelector<HTMLFormElement>('.create-panel')?.dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(create).not.toHaveBeenCalled();
    expect(document.querySelector('#add-member-error[role="alert"]')?.textContent).toContain('請輸入登入名稱。');
  });

  it('shows the repository’s validation message and blocks a second submit while in flight', async () => {
    const { fixture, host, repository } = await render();
    const response = new Subject<CreateMemberResult>();
    const create = vi.spyOn(repository, 'createMember').mockReturnValue(response);

    button(host, '新增成員').click();
    await settle(fixture);
    typeInto('#new-member-login-name', 'duplicate-name');
    typeInto('#new-member-display-name', '重複的人');
    const form = document.querySelector<HTMLFormElement>('.create-panel') as HTMLFormElement;
    form.dispatchEvent(new Event('submit'));
    await settle(fixture);

    // 送出中（回應還沒回來）再送出一次不會呼叫第二次：`creatingMember` signal 擋下重複送出。
    form.dispatchEvent(new Event('submit'));
    expect(create).toHaveBeenCalledTimes(1);

    response.next({ status: 'validation-failed', message: '這個登入名稱在目前組織已經有人使用，請改用其他名稱。' });
    response.complete();
    await settle(fixture);

    expect(document.querySelector('#add-member-error[role="alert"]')?.textContent).toContain(
      '這個登入名稱在目前組織已經有人使用',
    );
    // 對話框仍開著（沒有因失敗而關閉）。
    expect(document.querySelector('#new-member-login-name')).not.toBeNull();
  });
});
