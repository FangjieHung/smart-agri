import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { DemoScenario } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

function repositoryFor(viewer: AccountId, scenario?: DemoScenario): MockDemoRepository {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-10-06T02:00:00.000Z'),
    viewer: () => viewer,
  });
  if (scenario !== undefined) repository.setScenario(scenario);
  return repository;
}

describe('MockDemoRepository organization usage (issue #203)', () => {
  it('shows the near state by default so the Pages demo has a banner to look at', async () => {
    const result = await firstValueFrom(repositoryFor('account-smb-admin').getOrganizationUsage());

    expect(result).toEqual({
      status: 'ready',
      data: { month: '2026-10', usedTokens: 864_300, limitTokens: 1_000_000, state: 'near' },
    });
  });

  it('previews the normal and exceeded states with ?demoScenario=usage-normal / usage-exceeded', async () => {
    const normal = await firstValueFrom(repositoryFor('account-smb-admin', 'usage-normal').getOrganizationUsage());
    const exceeded = await firstValueFrom(repositoryFor('account-smb-admin', 'usage-exceeded').getOrganizationUsage());

    expect(normal.status === 'ready' && normal.data.state).toBe('normal');
    expect(exceeded.status === 'ready' && exceeded.data.state).toBe('exceeded');
    expect(exceeded.status === 'ready' && exceeded.data.usedTokens).toBeGreaterThan(1_000_000);
  });

  it('denies accounts without manage-publishing (the same view the API 403 becomes)', async () => {
    for (const account of ['account-internal-employee', 'account-external-customer'] as const) {
      const result = await firstValueFrom(repositoryFor(account).getOrganizationUsage());
      expect(result.status).toBe('permission-denied');
    }
  });
});
