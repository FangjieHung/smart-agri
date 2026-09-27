import { TestBed } from '@angular/core/testing';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { RulesStepComponent } from './rules-step.component';

async function render() {
  TestBed.configureTestingModule({
    imports: [RulesStepComponent],
    providers: [AssistantDraftStore, ...provideWizardTesting().providers],
  });
  const store = TestBed.inject(AssistantDraftStore);
  store.toggleSource({ id: 'database-orders', type: 'database' });
  const fixture = TestBed.createComponent(RulesStepComponent);
  fixture.detectChanges();
  // `writableDatabases` 依賴 `listConnectableSources()`（非同步契約，issue #49）；
  // 即使 mock 是同步 Observable，仍要等一輪穩定才能讀到 `ready` 的結果。
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store };
}

describe('RulesStepComponent', () => {
  it('defaults to strict answering with citations and private conversation history', async () => {
    const { page } = await render();

    expect((page.querySelector('#scope-strict') as HTMLInputElement).checked).toBe(true);
    expect((page.querySelector('#show-citations') as HTMLInputElement).checked).toBe(true);
    expect((page.querySelector('#keep-conversations') as HTMLInputElement).checked).toBe(true);
    expect((page.querySelector('#refusal-message') as HTMLTextAreaElement).value).not.toBe('');
  });

  it('asks for a collection purpose only after a connected database is chosen for writing', async () => {
    const { fixture, page, store } = await render();

    expect(page.querySelector('#data-write-purpose')).toBeNull();

    const select = page.querySelector('#data-write-database') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
      '不寫入資料庫',
      '訂單資料庫',
    ]);
    select.value = 'database-orders';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(store.draft().rules.dataWriteDatabaseId).toBe('database-orders');
    expect(page.querySelector('#data-write-purpose')).not.toBeNull();
  });

  it('lets the owner choose general knowledge and a periodic report in plain language', async () => {
    const { fixture, page, store } = await render();

    (page.querySelector('#scope-general') as HTMLInputElement).click();
    const report = page.querySelector('#periodic-report') as HTMLSelectElement;
    report.value = 'weekly';
    report.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(store.draft().rules).toMatchObject({
      knowledgeScope: 'allow-general-knowledge',
      periodicReport: 'weekly',
    });
  });
});
