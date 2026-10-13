import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogState } from '@angular/material/dialog';
import { firstValueFrom, of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { NO_HOLDINGS, type TeamView } from '../../../../core/domain/team.model';
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
    expect(boxes).toHaveLength(8);
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

  // issue #283：`<select [value]>` 在 `@for` 還沒產生選項時就套用，瀏覽器改選第一個選項（管理者），
  // 畫面寫「管理者」、送出的卻是 `newRole` 的內部同仁。
  it('opens with 內部同仁 both on screen and in the submitted request when the role is left alone', async () => {
    const { fixture, host, repository } = await render();
    const create = vi.spyOn(repository, 'createMember');

    button(host, '新增成員').click();
    await settle(fixture);

    const role = document.querySelector<HTMLSelectElement>('#new-member-role') as HTMLSelectElement;
    expect(role.value).toBe('internal-employee');
    expect(role.selectedOptions[0]?.textContent?.trim()).toBe('內部同仁');

    typeInto('#new-member-login-name', 'new-hire');
    typeInto('#new-member-display-name', '新進同仁');
    document.querySelector<HTMLFormElement>('.create-panel')?.dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(create).toHaveBeenCalledOnce();
    expect(create.mock.calls[0][0].role).toBe('internal-employee');
  });

  it('submits 管理者 after the role is changed to it, and opens with 內部同仁 again next time', async () => {
    const { fixture, host, repository } = await render();
    const create = vi.spyOn(repository, 'createMember');

    button(host, '新增成員').click();
    await settle(fixture);
    typeInto('#new-member-login-name', 'new-admin');
    typeInto('#new-member-display-name', '新管理者');
    typeInto('#new-member-role', 'smb-admin');
    await settle(fixture);
    const role = document.querySelector<HTMLSelectElement>('#new-member-role') as HTMLSelectElement;
    expect(role.selectedOptions[0]?.textContent?.trim()).toBe('管理者');
    document.querySelector<HTMLFormElement>('.create-panel')?.dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(create).toHaveBeenCalledOnce();
    expect(create.mock.calls[0][0].role).toBe('smb-admin');

    // 再開一次：預設回到內部同仁，畫面與狀態一致。
    documentButton('關閉').click();
    await settle(fixture);
    button(host, '新增成員').click();
    await settle(fixture);
    const reopened = Array.from(document.querySelectorAll<HTMLSelectElement>('#new-member-role')).at(-1);
    expect(reopened?.value).toBe('internal-employee');
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

  // ---------- 停用成員並指定接手人（issue #356） ----------

  it('offers 停用 for everyone but the viewer', async () => {
    const { host } = await render();
    expect(() => button(host, '停用 安心商行客服同仁')).not.toThrow();
    expect(() => button(host, '停用 安心商行管理者')).toThrow();
  });

  it('deactivates a member: shows what moves, only eligible successors are selectable, then marks the row 已停用', async () => {
    const { fixture, host, repository } = await render();
    const deactivate = vi.spyOn(repository, 'deactivateMember');

    button(host, '停用 安心商行客服同仁').click();
    await settle(fixture);

    const dialog = document.querySelector<HTMLFormElement>('.create-panel') as HTMLFormElement;
    expect(dialog.textContent).toContain('停用「安心商行客服同仁」');
    expect(dialog.textContent).toContain('知識庫 1 個');
    expect(dialog.textContent).toContain('數據庫 1 個');
    expect(dialog.textContent).toContain('私人對話不會轉移');
    expect(dialog.textContent).toContain('具備「管理資料來源」權限');
    const admin = document.querySelector<HTMLInputElement>('#successor-account-smb-admin') as HTMLInputElement;
    const customer = document.querySelector<HTMLInputElement>('#successor-account-external-customer') as HTMLInputElement;
    expect(admin.disabled).toBe(false);
    expect(customer.disabled).toBe(true);
    expect(dialog.textContent).toContain('外部客戶不能接手同仁的工作。');
    expect(documentButton('停用並轉移').disabled).toBe(true);

    admin.click();
    admin.dispatchEvent(new Event('change'));
    await settle(fixture);
    documentButton('停用並轉移').click();
    await settle(fixture);

    expect(deactivate).toHaveBeenCalledWith('account-internal-employee', 'account-smb-admin');
    expect(TestBed.inject(MatDialog).openDialogs.map((ref) => ref.getState())).not.toContain(MatDialogState.OPEN);
    expect(host.querySelector('.feedback')?.textContent).toContain('已停用「安心商行客服同仁」：知識庫 1 個、數據庫 1 個已轉給「安心商行管理者」。');
    const row = Array.from(host.querySelectorAll('.member')).find((item) => item.textContent?.includes('安心商行客服同仁'));
    expect(row?.querySelector('.member__status')?.textContent).toContain('已停用');
    expect(() => button(host, '重新啟用 安心商行客服同仁')).not.toThrow();
    expect(() => button(host, '停用 安心商行客服同仁')).toThrow();
  });

  it('reactivates a deactivated member from the list', async () => {
    const { fixture, host, repository } = await render();
    await firstValueFrom(repository.deactivateMember('account-internal-employee', 'account-smb-admin'));
    fixture.componentInstance['teamResource'].reload();
    await settle(fixture);

    button(host, '重新啟用 安心商行客服同仁').click();
    await settle(fixture);

    expect(host.querySelector('.feedback')?.textContent).toContain('已重新啟用「安心商行客服同仁」');
    expect(host.querySelector('.member__status')).toBeNull();
  });

  it('shows why a member cannot be deactivated instead of the successor list', async () => {
    const { fixture, host, repository } = await render();
    vi.spyOn(repository, 'getMemberOffboarding').mockReturnValue(of({
      status: 'ready',
      data: {
        memberId: 'account-internal-employee', displayName: '安心商行客服同仁', block: 'last-admin',
        blockMessage: '這是組織裡最後一位使用中的管理者，停用後就沒有人能管理組織設定。請先新增另一位管理者再停用。',
        holdings: NO_HOLDINGS, requiredPermissions: [], candidates: [],
      },
    }));

    button(host, '停用 安心商行客服同仁').click();
    await settle(fixture);

    expect(document.querySelector('.create-panel [role="alert"]')?.textContent).toContain('最後一位使用中的管理者');
    expect(document.querySelector('input[name="successor"]')).toBeNull();
    expect(() => documentButton('停用並轉移')).toThrow();
    documentButton('取消').click();
    await settle(fixture);
  });

  it('keeps the dialog open with the repository’s message when deactivation is refused', async () => {
    const { fixture, host, repository } = await render();
    vi.spyOn(repository, 'deactivateMember').mockReturnValue(of({ status: 'validation-failed', message: '接手人必須是同一組織裡仍在使用中的內部成員，並具備管理這些資產需要的權限。' }));

    button(host, '停用 安心商行客服同仁').click();
    await settle(fixture);
    const admin = document.querySelector<HTMLInputElement>('#successor-account-smb-admin') as HTMLInputElement;
    admin.click();
    admin.dispatchEvent(new Event('change'));
    await settle(fixture);
    documentButton('停用並轉移').click();
    await settle(fixture);

    expect(document.querySelector('.create-panel > [role="alert"]')?.textContent).toContain('接手人必須是');
    expect(TestBed.inject(MatDialog).openDialogs.map((ref) => ref.getState())).toContain(MatDialogState.OPEN);
    documentButton('取消').click();
    await settle(fixture);
  });
});
