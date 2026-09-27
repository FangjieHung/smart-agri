import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DEMO_SEED } from '../repositories/demo-seed';
import { createMemoryStorage } from '../repositories/memory-storage';
import { MockDemoRepository } from '../repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../repositories/tokens';
import { AssistantPermissionsService } from './assistant-permissions.service';
import { DemoSessionService } from './demo-session.service';

/** 首頁與助理清單共用的「是否可以建立新助理」判斷（mock 模式，讀 `listAccounts()`）。 */
describe('AssistantPermissionsService', () => {
  function create(accountId: string): AssistantPermissionsService {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() }) },
        { provide: DemoSessionService, useValue: { activeAccountId: () => accountId } },
      ],
    });
    return TestBed.inject(AssistantPermissionsService);
  }

  it('allows an account with manage-assistants to create assistants', () => {
    const service = create('account-smb-admin');
    expect(service.canCreateAssistant()).toBe(true);
  });

  it('denies an account without manage-assistants', () => {
    const service = create('account-internal-employee');
    expect(service.canCreateAssistant()).toBe(false);
  });

  it('denies the external customer account', () => {
    const service = create('account-external-customer');
    expect(service.canCreateAssistant()).toBe(false);
  });
});
