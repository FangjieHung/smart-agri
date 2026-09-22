import { createMemoryStorage } from '../repositories/memory-storage';
import {
  DEMO_SESSION_STORAGE_KEY,
  DemoSessionService,
} from './demo-session.service';

describe('DemoSessionService', () => {
  it('discards the previous account view state when switching accounts', () => {
    const session = new DemoSessionService();

    session.switchAccount('account-smb-admin');
    session.updateViewState({
      assistantId: 'assistant-customer-service',
      assistantTab: 'data-sources',
      returnPath: '/app/assistants/assistant-customer-service/data-sources',
    });
    session.switchAccount('account-internal-employee');

    expect(session.activeAccountId()).toBe('account-internal-employee');
    expect(session.viewState()).toEqual({
      assistantId: null,
      assistantTab: null,
      returnPath: null,
    });
  });

  it('keeps separate service instances isolated', () => {
    const adminSession = new DemoSessionService();
    const employeeSession = new DemoSessionService();

    adminSession.switchAccount('account-smb-admin');
    adminSession.updateViewState({
      assistantId: 'assistant-customer-service',
      assistantTab: 'overview',
    });
    employeeSession.switchAccount('account-internal-employee');

    expect(adminSession.activeAccountId()).toBe('account-smb-admin');
    expect(adminSession.viewState().assistantTab).toBe('overview');
    expect(employeeSession.activeAccountId()).toBe('account-internal-employee');
    expect(employeeSession.viewState().assistantId).toBeNull();
  });

  it('marks the session as a visual demo without real authentication', () => {
    const session = new DemoSessionService();

    expect(session.securityNotice).toContain('視覺 Demo');
    expect(session.securityNotice).toContain('不提供真實驗證');
    expect(session.securityNotice).toContain('不使用真實資料');
    expect(session.securityNotice).toContain('不連接真實 AI');
  });
});

describe('DemoSessionService persistence', () => {
  it('restores the active account from per-tab storage on a reload', () => {
    const storage = createMemoryStorage();

    const first = new DemoSessionService({ storage });
    first.switchAccount('account-smb-admin');

    const reloaded = new DemoSessionService({ storage });

    expect(reloaded.activeAccountId()).toBe('account-smb-admin');
    expect(reloaded.sessionExpired()).toBe(false);
  });

  it('does not restore a session from another browser tab storage', () => {
    const tabA = createMemoryStorage();
    const tabB = createMemoryStorage();

    new DemoSessionService({ storage: tabA }).switchAccount('account-smb-admin');

    expect(new DemoSessionService({ storage: tabB }).activeAccountId()).toBeNull();
  });

  it('expires a restored session that has been idle past the demo timeout', () => {
    const storage = createMemoryStorage();
    let clock = 1_000_000;
    const timeoutMs = 30 * 60 * 1000;

    new DemoSessionService({ storage, timeoutMs, now: () => clock }).switchAccount(
      'account-smb-admin',
    );
    clock += timeoutMs + 1;

    const reloaded = new DemoSessionService({ storage, timeoutMs, now: () => clock });

    expect(reloaded.activeAccountId()).toBeNull();
    expect(reloaded.sessionExpired()).toBe(true);
    expect(reloaded.timeoutNotice).toContain('Demo 登入已逾時');
  });

  it('extends the session while the account stays active', () => {
    const storage = createMemoryStorage();
    let clock = 1_000_000;
    const timeoutMs = 30 * 60 * 1000;
    const session = new DemoSessionService({ storage, timeoutMs, now: () => clock });

    session.switchAccount('account-smb-admin');
    clock += timeoutMs - 1;
    expect(session.refreshActivity()).toBe(true);

    clock += timeoutMs - 1;

    expect(session.refreshActivity()).toBe(true);
    expect(session.activeAccountId()).toBe('account-smb-admin');
  });

  it('reports an expired session once the idle timeout passes', () => {
    const storage = createMemoryStorage();
    let clock = 1_000_000;
    const session = new DemoSessionService({ storage, timeoutMs: 1000, now: () => clock });

    session.switchAccount('account-smb-admin');
    session.updateViewState({ assistantId: 'assistant-customer-service' });
    clock += 1001;

    expect(session.refreshActivity()).toBe(false);
    expect(session.activeAccountId()).toBeNull();
    expect(session.sessionExpired()).toBe(true);
    expect(session.viewState().assistantId).toBeNull();
    expect(storage.getItem(DEMO_SESSION_STORAGE_KEY)).toBeNull();
  });

  it('clears the expired flag when a new demo persona is chosen', () => {
    const storage = createMemoryStorage();
    let clock = 1_000_000;
    const session = new DemoSessionService({ storage, timeoutMs: 1000, now: () => clock });

    session.switchAccount('account-smb-admin');
    clock += 1001;
    session.refreshActivity();
    session.switchAccount('account-internal-employee');

    expect(session.sessionExpired()).toBe(false);
    expect(session.refreshActivity()).toBe(true);
  });

  it('drops the stored session when the demo persona signs out', () => {
    const storage = createMemoryStorage();
    const session = new DemoSessionService({ storage });

    session.switchAccount('account-smb-admin');
    session.clearSession();

    expect(storage.getItem(DEMO_SESSION_STORAGE_KEY)).toBeNull();
    expect(new DemoSessionService({ storage }).activeAccountId()).toBeNull();
  });

  it('ignores an unreadable stored session instead of crashing the demo', () => {
    const storage = createMemoryStorage();
    storage.setItem(DEMO_SESSION_STORAGE_KEY, 'not-json');

    const session = new DemoSessionService({ storage });

    expect(session.activeAccountId()).toBeNull();
    expect(session.sessionExpired()).toBe(false);
  });
});
