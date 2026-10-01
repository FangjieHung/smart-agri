import { firstValueFrom } from 'rxjs';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';

function ready<T>(view: { status: string; data?: T }): T {
  if (view.status !== 'ready' || view.data === undefined)
    throw new Error(`expected ready, got ${view.status}`);
  return view.data;
}

describe('MockDemoRepository acceptance', () => {
  it('moves a run through queued, running, and completed with its case snapshot', async () => {
    const storage = createMemoryStorage();
    const repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      viewer: () => 'account-smb-admin',
    });
    const common = ready(
      await firstValueFrom(
        repository.createAssistantTestCase(ASSISTANT, {
          question: '退貨期限是多久？',
          category: 'common',
          expectedKind: 'company-data',
          expectedDocumentIds: [],
          followUpOfId: null,
        }),
      ),
    );
    ready(
      await firstValueFrom(
        repository.createAssistantTestCase(ASSISTANT, {
          question: '例外題',
          category: 'exception',
          expectedKind: 'company-data',
          expectedDocumentIds: [],
          followUpOfId: null,
        }),
      ),
    );

    const queued = ready(await firstValueFrom(repository.createAssistantTestRun(ASSISTANT)));
    expect(queued.status).toBe('queued');
    expect(ready(await firstValueFrom(repository.listAssistantTestRuns(ASSISTANT)))[0].status).toBe(
      'running',
    );
    const completed = ready(await firstValueFrom(repository.listAssistantTestRuns(ASSISTANT)))[0];
    expect(completed).toMatchObject({ status: 'completed', passedCount: 1, failedCount: 1 });
    const detail = ready(
      await firstValueFrom(repository.getAssistantTestRun(ASSISTANT, queued.id)),
    );
    expect(detail.results).toHaveLength(2);
    expect(detail.results[0]).toMatchObject({ testCaseId: common.id, passed: true });
    expect(detail.results[1]).toMatchObject({ passed: false, failureReason: 'kind-mismatch' });
    const configurations = ready(await firstValueFrom(repository.listAssistantConfigurations()));
    expect(configurations.find((assistant) => assistant.id === ASSISTANT)?.acceptanceStatus).toBe(
      'failed',
    );
    const reloaded = new MockDemoRepository(DEMO_SEED, {
      storage,
      viewer: () => 'account-smb-admin',
    });
    expect(ready(await firstValueFrom(reloaded.listAssistantTestCases(ASSISTANT)))).toHaveLength(2);
    expect(
      ready(await firstValueFrom(reloaded.getAssistantTestRun(ASSISTANT, queued.id))).results,
    ).toHaveLength(2);
    expect(
      ready(await firstValueFrom(reloaded.listAssistantConfigurations())).find(
        (assistant) => assistant.id === ASSISTANT,
      )?.acceptanceStatus,
    ).toBe('failed');
  });
});
