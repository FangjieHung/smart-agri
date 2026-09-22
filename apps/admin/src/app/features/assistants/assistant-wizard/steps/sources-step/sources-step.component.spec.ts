import { TestBed } from '@angular/core/testing';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { SourcesStepComponent } from './sources-step.component';

function render() {
  TestBed.configureTestingModule({
    imports: [SourcesStepComponent],
    providers: [AssistantDraftStore, ...provideWizardTesting().providers],
  });
  const fixture = TestBed.createComponent(SourcesStepComponent);
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store: TestBed.inject(AssistantDraftStore) };
}

function row(page: HTMLElement, name: string): HTMLElement {
  const match = Array.from(page.querySelectorAll<HTMLElement>('.source-row')).find((item) =>
    item.textContent?.includes(name),
  );
  if (!match) throw new Error(`missing row ${name}`);
  return match;
}

describe('SourcesStepComponent', () => {
  it('shows knowledge bases and databases in one list with type, permission, status and update time', () => {
    const { page } = render();

    const orders = row(page, '訂單資料庫');
    expect(orders.textContent).toContain('資料庫');
    expect(orders.textContent).toContain('唯讀');
    expect(orders.textContent).toContain('可使用');
    expect(orders.textContent).toContain('更新');
    expect(row(page, '商品使用指南').textContent).toContain('知識庫');
    expect(page.querySelectorAll('.source-row').length).toBe(5);
  });

  it('connects two knowledge bases and two databases and summarises the mix', () => {
    const { fixture, page, store } = render();

    for (const name of ['商品使用指南', '退換貨政策', '訂單資料庫', '客戶資料庫']) {
      (row(page, name).querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
    }

    expect(store.draft().sources).toHaveLength(4);
    expect(row(page, '訂單資料庫').querySelector('button')?.getAttribute('aria-pressed')).toBe('true');
    expect(page.querySelector('.source-summary')?.textContent).toContain('2 個知識庫、2 個資料庫');
  });

  it('filters the mixed list by type and search text', () => {
    const { fixture, page } = render();

    (page.querySelector('input[name="source-type"][value="database"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(page.querySelectorAll('.source-row').length).toBe(2);

    const search = page.querySelector('#source-search') as HTMLInputElement;
    search.value = '訂單';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(page.querySelectorAll('.source-row').length).toBe(1);
  });
});
