import { TestBed } from '@angular/core/testing';
import { ApiSessionService } from '../../../../../core/session/api-session.service';
import { AssistantDraftStore } from '../../assistant-draft.store';
import { provideWizardTesting } from '../../assistant-wizard.testing';
import { PurposeStepComponent } from './purpose-step.component';

async function render(options: { readonly apiMode?: boolean } = {}) {
  TestBed.configureTestingModule({
    imports: [PurposeStepComponent],
    providers: [
      AssistantDraftStore,
      ...provideWizardTesting().providers,
      ...(options.apiMode ? [{ provide: ApiSessionService, useValue: { apiMode: true } }] : []),
    ],
  });
  const fixture = TestBed.createComponent(PurposeStepComponent);
  fixture.detectChanges();
  // 草稿是非同步讀取的（issue #81）：讀到之後再操作，否則編輯會被讀取結果蓋掉。
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement, store: TestBed.inject(AssistantDraftStore) };
}

describe('PurposeStepComponent', () => {
  it('prefills the form from the 回答客戶問題 template', async () => {
    const { fixture, page } = await render();

    const template = Array.from(page.querySelectorAll<HTMLInputElement>('input[name="assistant-template"]'))
      .find((input) => input.closest('label')?.textContent?.includes('回答客戶問題'));
    template?.click();
    fixture.detectChanges();

    expect((page.querySelector('#assistant-name') as HTMLInputElement).value).toBe('客戶問答助理');
    expect((page.querySelector('#assistant-purpose') as HTMLTextAreaElement).value).not.toBe('');
  });

  it('maps the internal and external checkboxes to one audience value', async () => {
    const { fixture, page, store } = await render();

    (page.querySelector('#audience-internal') as HTMLInputElement).click();
    (page.querySelector('#audience-external') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(store.draft().audience).toBe('members-and-external-customers');
  });

  it('offers the external-customer audience in API mode too, without the old 後續版本開放 note (#224)', async () => {
    const { fixture, page, store } = await render({ apiMode: true });

    expect(page.textContent).not.toContain('將於後續版本開放');
    (page.querySelector('#audience-external') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(store.draft().audience).toBe('authorized-external-customers');
  });

  it('keeps advanced role instructions collapsed by default', async () => {
    const { page } = await render();

    const details = page.querySelector('details.advanced') as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(details.querySelector('summary')?.textContent).toContain('進階角色指令');
  });
});
