import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import { API_ORGANIZATION_USAGE_PATH, HybridDemoRepository } from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 以下回應都是 2026-10-06 從 API 整合測試主機實際取得（上限 1,000 tokens，依序累計 400／800／1,100 tokens 後
 * 各讀一次；另一個沒有 `manage-publishing` 的帳號讀到 403），原樣保留成字串再 `JSON.parse`，不是依產生的型別
 * 手寫（issue #203）。
 */
const REAL_NORMAL_JSON = `{"month":"2026-10","usedTokens":400,"limitTokens":1000,"state":"normal"}`;
const REAL_NEAR_JSON = `{"month":"2026-10","usedTokens":800,"limitTokens":1000,"state":"near"}`;
const REAL_EXCEEDED_JSON = `{"month":"2026-10","usedTokens":1100,"limitTokens":1000,"state":"exceeded"}`;
const REAL_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"publishing","message":"你沒有這個助理的發布設定存取權限，或它已不存在。"}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '0199a3c0-0000-7000-8000-0000000000a1',
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-publishing'],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

describe('HybridDemoRepository organization usage (issue #203)', () => {
  it.each([
    ['normal', REAL_NORMAL_JSON, 400],
    ['near', REAL_NEAR_JSON, 800],
    ['exceeded', REAL_EXCEEDED_JSON, 1100],
  ])('maps the real %s response as is', async (state, json, used) => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationUsage());
    controller.expectOne({ method: 'GET', url: API_ORGANIZATION_USAGE_PATH }).flush(JSON.parse(json));

    expect(await result).toEqual({
      status: 'ready',
      data: { month: '2026-10', usedTokens: used, limitTokens: 1000, state },
    });
    controller.verify();
  });

  it('turns the real 403 into a permission-denied view instead of an error', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationUsage());
    controller
      .expectOne(API_ORGANIZATION_USAGE_PATH)
      .flush(JSON.parse(REAL_FORBIDDEN_JSON), { status: 403, statusText: 'Forbidden' });

    const outcome = await result;
    expect(outcome.status).toBe('permission-denied');
    if (outcome.status !== 'permission-denied') return;
    expect(outcome.reason).toBe('publishing');
  });

  it('lets a server failure surface as an error (not as "nothing to show")', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationUsage());
    controller.expectOne(API_ORGANIZATION_USAGE_PATH).flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });
});
