import { DemoSessionService } from './demo-session.service';

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
