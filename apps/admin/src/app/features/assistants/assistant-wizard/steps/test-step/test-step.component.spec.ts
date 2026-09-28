import { TestBed } from '@angular/core/testing';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { TestStepComponent } from './test-step.component';

async function render() {
  TestBed.configureTestingModule({
    imports: [TestStepComponent],
    providers: [AssistantDraftStore, ...provideWizardTesting().providers],
  });
  const store = TestBed.inject(AssistantDraftStore);
  const fixture = TestBed.createComponent(TestStepComponent);
  fixture.detectChanges();
  // 草稿是非同步讀取的（issue #81）：讀到之後再編輯，否則會被讀取結果蓋掉。
  await fixture.whenStable();
  store.applyTemplate('answer-customer-questions');
  store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store };
}

function suggestion(page: HTMLElement, text: string): HTMLButtonElement | undefined {
  return Array.from(page.querySelectorAll<HTMLButtonElement>('.trial-question')).find((candidate) =>
    candidate.textContent?.includes(text),
  );
}

async function ask(fixture: { whenStable(): Promise<unknown> }, page: HTMLElement, text: string): Promise<void> {
  suggestion(page, text)?.click();
  await fixture.whenStable();
}

async function askFreeText(
  fixture: { detectChanges(): void; whenStable(): Promise<unknown> },
  page: HTMLElement,
  text: string,
): Promise<void> {
  const input = page.querySelector<HTMLInputElement>('.ask-input');
  if (input === null) throw new Error('expected the free-text input to exist');
  input.value = text;
  input.dispatchEvent(new Event('input'));
  // 讓 Angular 先把 `[value]` 綁定的「上次渲染值」同步成剛剛打的字，否則之後若又清空輸入框，
  // ivy 會拿「清空後的值」跟「還停在初始空字串的快取值」比較、誤判成沒變化而跳過 DOM 寫入。
  fixture.detectChanges();
  page.querySelector<HTMLFormElement>('.ask-form')?.dispatchEvent(new Event('submit', { cancelable: true }));
  await fixture.whenStable();
}

describe('TestStepComponent (issue #82: trial answers share ChatReplyView)', () => {
  it('previews a cited company-data answer with the retrieved passages and threshold', async () => {
    const { fixture, page, store } = await render();

    await ask(fixture, page, '退貨');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.getAttribute('data-kind')).toBe('company-data');
    expect(answer.textContent).toContain('根據你的資料');
    expect(answer.textContent).toContain('退換貨政策');
    expect(answer.textContent).toContain('門檻');
    expect(store.draft().hasTrialAnswer).toBe(true);
  });

  it('shows the configured refusal as no-result when strict mode has no matching data', async () => {
    const { fixture, page, store } = await render();

    await ask(fixture, page, '皮革');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.getAttribute('data-kind')).toBe('no-result');
    expect(answer.textContent).toContain('查無資料');
    expect(answer.textContent).toContain(store.draft().rules.refusalMessage);
  });

  it('shows the general-knowledge notice when the scope allows it', async () => {
    const { fixture, page, store } = await render();
    store.updateRules({ knowledgeScope: 'allow-general-knowledge' });
    fixture.detectChanges();

    await ask(fixture, page, '皮革');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.getAttribute('data-kind')).toBe('general-knowledge');
    expect(answer.textContent).toContain('一般知識補充');
  });

  it('accepts a freely typed question, not only the fixed suggestions', async () => {
    const { fixture, page } = await render();

    await askFreeText(fixture, page, '收到商品後幾天內可以申請退貨？');
    fixture.detectChanges();

    const answer = page.querySelector('.trial-answer') as HTMLElement;
    expect(answer.textContent).toContain('收到商品後幾天內可以申請退貨？');
    expect(answer.getAttribute('data-kind')).toBe('company-data');
  });

  describe('clearing the free-text input (issue #114)', () => {
    it('keeps the typed question after a 422 (validation-failed) rejection', async () => {
      const { fixture, page, store } = await render();
      const tooLong = '超過上限的問題'.repeat(400); // 2800 字，超過 2000 字上限。

      await askFreeText(fixture, page, tooLong);
      fixture.detectChanges();

      expect(store.trialError()).toContain('2000');
      const input = page.querySelector<HTMLInputElement>('.ask-input');
      expect(input?.value).toBe(tooLong);
    });

    it('keeps the typed question after a 503 (network/unavailable) failure', async () => {
      const { fixture, page, store } = await render();
      vi.spyOn(store, 'runTrial').mockResolvedValue(null);

      await askFreeText(fixture, page, '對話模型暫時無法使用時的問題');
      fixture.detectChanges();

      const input = page.querySelector<HTMLInputElement>('.ask-input');
      expect(input?.value).toBe('對話模型暫時無法使用時的問題');
    });

    it('clears the typed question once a reply is received', async () => {
      const { fixture, page } = await render();

      await askFreeText(fixture, page, '收到商品後幾天內可以申請退貨？');
      fixture.detectChanges();

      const input = page.querySelector<HTMLInputElement>('.ask-input');
      expect(input?.value).toBe('');
    });
  });
});
