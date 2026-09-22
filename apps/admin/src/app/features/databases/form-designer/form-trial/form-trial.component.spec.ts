import { TestBed } from '@angular/core/testing';
import type { DatabaseFieldView, DatabaseTrialAnswers } from '../../../../core/domain/database.model';
import type { PreviewDatabaseEntryResult } from '../../../../core/repositories/demo-repository';
import { FormTrialComponent } from './form-trial.component';

const fields: readonly DatabaseFieldView[] = [
  { id: 'field-name', label: '姓名', type: 'text', required: true, options: [], scale: null, unit: '' },
  { id: 'field-spend', label: '消費金額', type: 'number', required: false, options: [], scale: null, unit: '元' },
  { id: 'field-date', label: '回訪日期', type: 'date', required: false, options: [], scale: null, unit: '' },
  { id: 'field-level', label: '會員等級', type: 'single-choice', required: false, options: ['一般', '金卡'], scale: null, unit: '' },
  { id: 'field-tags', label: '關注商品', type: 'multiple-choice', required: false, options: ['保養品', '配件'], scale: null, unit: '' },
  {
    id: 'field-score',
    label: '滿意度',
    type: 'scale',
    required: true,
    options: [],
    scale: { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' },
    unit: '',
  },
];

function render(result: PreviewDatabaseEntryResult | null = null) {
  TestBed.configureTestingModule({ imports: [FormTrialComponent] });
  const fixture = TestBed.createComponent(FormTrialComponent);
  fixture.componentRef.setInput('fields', fields);
  fixture.componentRef.setInput('result', result);
  fixture.detectChanges();
  const submitted: DatabaseTrialAnswers[] = [];
  fixture.componentInstance.trial.subscribe((value) => submitted.push(value));
  return { fixture, host: fixture.nativeElement as HTMLElement, submitted };
}

describe('FormTrialComponent', () => {
  it('renders a labelled control for each of the six field types', () => {
    const { host } = render();

    expect(host.querySelector('label[for="trial-field-name"]')?.textContent).toContain('姓名');
    expect(host.querySelector<HTMLInputElement>('#trial-field-name')?.required).toBe(true);
    expect(host.querySelector('#trial-field-spend')?.getAttribute('type')).toBe('number');
    expect(host.querySelector('#trial-field-date')?.getAttribute('type')).toBe('date');
    expect(host.querySelectorAll('input[type="radio"][name="trial-field-level"]')).toHaveLength(2);
    expect(host.querySelectorAll('input[type="checkbox"][name="trial-field-tags"]')).toHaveLength(2);
    expect(host.querySelectorAll('input[type="radio"][name="trial-field-score"]')).toHaveLength(5);
    expect(host.textContent).toContain('很不滿意');
    expect(host.textContent).toContain('不會儲存');
  });

  it('emits the typed answers when the trial is submitted', () => {
    const { host, submitted } = render();
    const name = host.querySelector<HTMLInputElement>('#trial-field-name');
    if (name) {
      name.value = '王小姐';
      name.dispatchEvent(new Event('input'));
    }
    host.querySelector<HTMLInputElement>('input[name="trial-field-tags"][value="配件"]')?.click();
    host.querySelector<HTMLInputElement>('input[name="trial-field-tags"][value="保養品"]')?.click();
    host.querySelector<HTMLInputElement>('input[name="trial-field-score"][value="4"]')?.click();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();

    expect(submitted[0]).toMatchObject({
      'field-name': '王小姐',
      'field-tags': ['保養品', '配件'],
      'field-score': '4',
    });
  });

  it('shows validation errors from the repository next to each field', () => {
    const { host } = render({
      status: 'validation-failed',
      message: '試填內容還有需要修正的地方。',
      errors: [{ fieldId: 'field-name', message: '「姓名」為必填。' }],
    });

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('需要修正');
    expect(host.querySelector('#trial-field-name')?.getAttribute('aria-invalid')).toBe('true');
    expect(host.textContent).toContain('「姓名」為必填。');
  });

  it('previews the entry and says nothing was saved', () => {
    const { host } = render({
      status: 'ready',
      data: { saved: false, entries: [{ fieldId: 'field-score', label: '滿意度', display: '4 / 5' }] },
    });
    const preview = host.querySelector('.trial-preview');

    expect(preview?.getAttribute('role')).toBe('status');
    expect(preview?.textContent).toContain('試填結果');
    expect(preview?.textContent).toContain('4 / 5');
    expect(preview?.textContent).toContain('不會儲存');
  });
});
