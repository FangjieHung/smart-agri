import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import type { ApiChangePasswordResult } from '../../core/session/api-session.service';
import { ApiSessionService } from '../../core/session/api-session.service';
import {
  ChangePasswordPageComponent,
  CHANGE_PASSWORD_INCOMPLETE_MESSAGE,
  CHANGE_PASSWORD_MISMATCH_MESSAGE,
  CHANGE_PASSWORD_UNAVAILABLE_MESSAGE,
} from './change-password-page.component';

function createApiSession(changePasswordResult: ApiChangePasswordResult = { outcome: 'success' }) {
  return {
    changePassword: vi
      .fn<ApiSessionService['changePassword']>()
      .mockResolvedValue(changePasswordResult),
    completePasswordChange: vi.fn<ApiSessionService['completePasswordChange']>().mockResolvedValue(undefined),
  };
}

async function renderPage(apiSession: ReturnType<typeof createApiSession>, navigateByUrl = vi.fn()) {
  await TestBed.configureTestingModule({
    imports: [ChangePasswordPageComponent],
    providers: [
      { provide: Router, useValue: { navigateByUrl } },
      { provide: ApiSessionService, useValue: apiSession },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ChangePasswordPageComponent);
  fixture.detectChanges();
  return fixture;
}

/** 送出流程是 Promise 鏈，等一個 macrotask 再更新畫面。 */
async function settle(fixture: ComponentFixture<ChangePasswordPageComponent>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
  fixture.detectChanges();
}

function type(page: HTMLElement, selector: string, value: string): void {
  const input = page.querySelector(selector) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function submit(page: HTMLElement): void {
  (page.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
}

function fillForm(page: HTMLElement, current: string, next: string, confirm: string): void {
  type(page, '#change-password-current', current);
  type(page, '#change-password-new', next);
  type(page, '#change-password-confirm', confirm);
}

describe('ChangePasswordPageComponent', () => {
  it('shows the password rules and the title', async () => {
    const fixture = await renderPage(createApiSession());
    const page = fixture.nativeElement as HTMLElement;

    expect(page.querySelector('h1')?.textContent).toBe('設定新密碼');
    expect(page.textContent).toContain('至少 12 個字元');
    expect(page.textContent).toContain('至少一個符號');
  });

  it('refuses to submit with a blank field, without calling the API', async () => {
    const apiSession = createApiSession();
    const fixture = await renderPage(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'New-Own-Secret-42z', '');
    submit(page);
    await settle(fixture);

    expect(apiSession.changePassword).not.toHaveBeenCalled();
    expect(page.querySelector('.change-password__error')?.textContent).toBe(
      CHANGE_PASSWORD_INCOMPLETE_MESSAGE,
    );
  });

  it('checks that the new password and its confirmation match before calling the API', async () => {
    const apiSession = createApiSession();
    const fixture = await renderPage(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'New-Own-Secret-42z', 'something-else');
    submit(page);
    await settle(fixture);

    expect(apiSession.changePassword).not.toHaveBeenCalled();
    expect(page.querySelector('#change-password-confirm-error')?.textContent).toBe(
      CHANGE_PASSWORD_MISMATCH_MESSAGE,
    );
  });

  it('sends the current and new password, then advances to the workspace on success', async () => {
    const apiSession = createApiSession({ outcome: 'success' });
    const navigateByUrl = vi.fn();
    const fixture = await renderPage(apiSession, navigateByUrl);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'New-Own-Secret-42z', 'New-Own-Secret-42z');
    submit(page);
    await settle(fixture);

    expect(apiSession.changePassword).toHaveBeenCalledWith('One-Time-Pass-7x', 'New-Own-Secret-42z');
    expect(apiSession.completePasswordChange).toHaveBeenCalledOnce();
    expect(navigateByUrl).toHaveBeenCalledWith('/app/home', { replaceUrl: true });
  });

  it('shows the field errors from a 422 response, one message per broken rule', async () => {
    const apiSession = createApiSession({
      outcome: 'invalid',
      errors: { newPassword: ['密碼長度至少需要 12 個字元。', '密碼須包含至少一個數字。'] },
    });
    const fixture = await renderPage(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'short', 'short');
    submit(page);
    await settle(fixture);

    const messages = [...page.querySelectorAll('#change-password-new-error li')].map((li) => li.textContent);
    expect(messages).toEqual(['密碼長度至少需要 12 個字元。', '密碼須包含至少一個數字。']);
    expect(apiSession.completePasswordChange).not.toHaveBeenCalled();
  });

  it('shows the current-password error when the current password is wrong', async () => {
    const apiSession = createApiSession({
      outcome: 'invalid',
      errors: { currentPassword: ['密碼不正確。'] },
    });
    const fixture = await renderPage(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'wrong', 'New-Own-Secret-42z', 'New-Own-Secret-42z');
    submit(page);
    await settle(fixture);

    expect(page.querySelector('#change-password-current-error')?.textContent).toContain('密碼不正確。');
  });

  it('shows a generic message when the server is unavailable', async () => {
    const apiSession = createApiSession({ outcome: 'unavailable' });
    const fixture = await renderPage(apiSession);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'New-Own-Secret-42z', 'New-Own-Secret-42z');
    submit(page);
    await settle(fixture);

    expect(page.querySelector('.change-password__error')?.textContent).toBe(
      CHANGE_PASSWORD_UNAVAILABLE_MESSAGE,
    );
  });

  it('does not navigate when the token expires during the confirmation refresh (401 already sent it to login)', async () => {
    const apiSession = createApiSession({ outcome: 'success' });
    apiSession.completePasswordChange.mockRejectedValue(new Error('401'));
    const navigateByUrl = vi.fn();
    const fixture = await renderPage(apiSession, navigateByUrl);
    const page = fixture.nativeElement as HTMLElement;

    fillForm(page, 'One-Time-Pass-7x', 'New-Own-Secret-42z', 'New-Own-Secret-42z');
    submit(page);
    await settle(fixture);

    expect(navigateByUrl).not.toHaveBeenCalled();
  });
});
