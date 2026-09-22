import { TestBed } from '@angular/core/testing';
import type { DatabaseFieldError, DatabaseFieldView } from '../../../core/domain/database.model';
import { FormDesignerComponent } from './form-designer.component';

const fields: readonly DatabaseFieldView[] = [
  { id: 'field-order-number', label: '訂單編號', type: 'text', required: true, options: [], scale: null, unit: '' },
  { id: 'field-issue-type', label: '問題類型', type: 'single-choice', required: true, options: ['配送延遲', '商品瑕疵'], scale: null, unit: '' },
];

function render(errors: readonly DatabaseFieldError[] = []) {
  TestBed.configureTestingModule({ imports: [FormDesignerComponent] });
  const fixture = TestBed.createComponent(FormDesignerComponent);
  fixture.componentRef.setInput('fields', fields);
  fixture.componentRef.setInput('errors', errors);
  fixture.detectChanges();
  const saved: (readonly DatabaseFieldView[])[] = [];
  fixture.componentInstance.save.subscribe((value) => saved.push(value));
  const host = fixture.nativeElement as HTMLElement;
  return { fixture, host, saved };
}

function type(input: HTMLInputElement | HTMLTextAreaElement | null, value: string) {
  if (!input) throw new Error('missing input');
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function select(element: HTMLSelectElement | null, value: string) {
  if (!element) throw new Error('missing select');
  element.value = value;
  element.dispatchEvent(new Event('change'));
}

function button(host: HTMLElement, text: string, scope: ParentNode = host): HTMLButtonElement {
  const found = Array.from(scope.querySelectorAll('button')).find((b) => b.textContent?.includes(text));
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

describe('FormDesignerComponent', () => {
  it('edits each field with a labelled name, one of six types and a required toggle', () => {
    const { host } = render();
    const editors = host.querySelectorAll('.field-editor');

    expect(editors).toHaveLength(2);
    const label = host.querySelector<HTMLInputElement>('#field-label-field-order-number');
    expect(host.querySelector('label[for="field-label-field-order-number"]')?.textContent).toContain('欄位名稱');
    expect(label?.value).toBe('訂單編號');
    const options = Array.from(host.querySelectorAll('#field-type-field-order-number option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['文字', '數字', '日期', '單選', '多選', '量尺']);
    expect(host.querySelector<HTMLInputElement>('#field-required-field-order-number')?.checked).toBe(true);
    expect(host.textContent).not.toMatch(/條件|公式/);
  });

  it('emits the edited, reordered fields only when saved', () => {
    const { fixture, host, saved } = render();

    type(host.querySelector('#field-label-field-order-number'), '訂單號碼');
    host.querySelector<HTMLInputElement>('#field-required-field-order-number')?.click();
    button(host, '下移', host.querySelector('.field-editor') as HTMLElement).click();
    fixture.detectChanges();
    expect(saved).toHaveLength(0);

    button(host, '儲存表單').click();
    expect(saved[0].map((field) => field.label)).toEqual(['問題類型', '訂單號碼']);
    expect(saved[0][1].required).toBe(false);
  });

  it('adds a field, switches its type and edits type-specific settings', () => {
    const { fixture, host, saved } = render();

    button(host, '新增欄位').click();
    fixture.detectChanges();
    const editors = host.querySelectorAll<HTMLElement>('.field-editor');
    expect(editors).toHaveLength(3);
    const added = editors[2];
    const id = added.getAttribute('data-field-id');
    type(added.querySelector(`#field-label-${id}`), '滿意度');
    select(added.querySelector(`#field-type-${id}`), 'scale');
    fixture.detectChanges();
    type(added.querySelector(`#field-scale-max-${id}`), '10');
    select(host.querySelector('#field-type-field-order-number'), 'number');
    fixture.detectChanges();
    type(host.querySelector('#field-unit-field-order-number'), '件');
    type(host.querySelector('#field-options-field-issue-type'), '配送延遲\n商品瑕疵\n退換貨');
    fixture.detectChanges();

    button(host, '儲存表單').click();
    const [order, issue, scale] = saved[0];
    expect(order).toMatchObject({ type: 'number', unit: '件' });
    expect(issue.options).toEqual(['配送延遲', '商品瑕疵', '退換貨']);
    expect(scale).toMatchObject({ label: '滿意度', type: 'scale', scale: { min: 1, max: 10 } });
  });

  it('removes a field', () => {
    const { fixture, host, saved } = render();

    button(host, '刪除', host.querySelector('.field-editor') as HTMLElement).click();
    fixture.detectChanges();
    button(host, '儲存表單').click();

    expect(saved[0].map((field) => field.id)).toEqual(['field-issue-type']);
  });

  it('shows repository validation errors next to the field they belong to', () => {
    const { host } = render([
      { fieldId: 'field-order-number', message: '請填寫欄位名稱。' },
      { fieldId: null, message: '表單至少需要一個欄位。' },
    ]);
    const input = host.querySelector('#field-label-field-order-number');
    const errorId = input?.getAttribute('aria-describedby') ?? '';

    expect(input?.getAttribute('aria-invalid')).toBe('true');
    expect(host.querySelector(`#${errorId}`)?.textContent).toContain('請填寫欄位名稱');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('表單至少需要一個欄位');
  });
});
