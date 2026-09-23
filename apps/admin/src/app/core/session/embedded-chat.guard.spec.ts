import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { createMemoryStorage } from '../repositories/memory-storage';
import { AnonymousVisitorService } from './anonymous-visitor.service';
import { DemoSessionService } from './demo-session.service';
import { embeddedChatGuard } from './embedded-chat.guard';

/** 每個測試都用自己的記憶體儲存，避免共用瀏覽器 sessionStorage 互相污染。 */
function configure(timeoutMs?: number, now?: () => number) {
  TestBed.configureTestingModule({
    providers: [
      {
        provide: Router,
        useValue: {
          createUrlTree: () => {
            throw new Error('嵌入的對話頁不可以轉址到工作區');
          },
        },
      },
      {
        provide: DemoSessionService,
        useFactory: () =>
          new DemoSessionService({ storage: createMemoryStorage(), timeoutMs, now }),
      },
      {
        provide: AnonymousVisitorService,
        useFactory: () => new AnonymousVisitorService({ storage: createMemoryStorage() }),
      },
    ],
  });
}

function run(): boolean | unknown {
  return TestBed.runInInjectionContext(() => embeddedChatGuard({} as never, {} as never));
}

describe('embeddedChatGuard', () => {
  it('lets a visitor with no demo persona in and gives this tab a visitor id', () => {
    configure();

    expect(run()).toBe(true);
    expect(TestBed.inject(AnonymousVisitorService).visitorId()).not.toBeNull();
  });

  it('keeps the chosen demo persona and does not mint a visitor id', () => {
    configure();
    TestBed.inject(DemoSessionService).switchAccount('account-external-customer');

    expect(run()).toBe(true);
    expect(TestBed.inject(AnonymousVisitorService).visitorId()).toBeNull();
  });

  it('falls back to an anonymous visitor once the demo session has timed out', () => {
    let clock = 1_000_000;
    configure(1000, () => clock);
    const session = TestBed.inject(DemoSessionService);
    session.switchAccount('account-external-customer');
    clock += 1001;

    expect(run()).toBe(true);
    expect(session.activeAccountId()).toBeNull();
    expect(TestBed.inject(AnonymousVisitorService).visitorId()).not.toBeNull();
  });
});
