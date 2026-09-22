import { TestBed } from '@angular/core/testing';
import type {
  KnowledgeShareTargetView,
  KnowledgeSharingView,
} from '../../../../core/domain/knowledge-base.model';
import { SharingPanelComponent } from './sharing-panel.component';

const targets: readonly KnowledgeShareTargetView[] = [
  { id: 'account-internal-employee', displayName: '安心商行客服同仁' },
  { id: 'account-external-customer', displayName: '外部客戶' },
];

function render(sharing: KnowledgeSharingView) {
  TestBed.configureTestingModule({ imports: [SharingPanelComponent] });
  const fixture = TestBed.createComponent(SharingPanelComponent);
  fixture.componentRef.setInput('sharing', sharing);
  fixture.componentRef.setInput('targets', targets);
  fixture.detectChanges();
  const saved: KnowledgeSharingView[] = [];
  fixture.componentInstance.save.subscribe((value) => saved.push(value));
  return { fixture, host: fixture.nativeElement as HTMLElement, saved };
}

function choose(host: HTMLElement, value: string) {
  const radio = host.querySelector<HTMLInputElement>(`input[type="radio"][value="${value}"]`);
  if (!radio) throw new Error(`missing radio ${value}`);
  radio.click();
}

function submit(host: HTMLElement) {
  (host.querySelector('button[type="submit"]') as HTMLButtonElement).click();
}

describe('SharingPanelComponent', () => {
  it('offers the three sharing scopes as a labelled radio group', () => {
    const { host } = render({ scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false });

    expect(host.querySelector('fieldset legend')?.textContent).toContain('分享範圍');
    const labels = Array.from(host.querySelectorAll('input[type="radio"]')).map(
      (input) => input.closest('label')?.textContent ?? '',
    );
    expect(labels.join('|')).toContain('只有我');
    expect(labels.join('|')).toContain('指定帳號／團隊');
    expect(labels.join('|')).toContain('公開分享');
    expect(host.querySelector<HTMLInputElement>('input[value="private"]')?.checked).toBe(true);
  });

  it('asks for at least one account before saving account-specific sharing', () => {
    const { fixture, host, saved } = render({
      scope: 'private',
      sharedWithAccountIds: [],
      allowOriginalDownload: false,
    });
    choose(host, 'specific-accounts');
    fixture.detectChanges();
    submit(host);
    fixture.detectChanges();

    expect(saved).toEqual([]);
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('至少選擇一個帳號');

    host.querySelector<HTMLInputElement>('input[type="checkbox"][value="account-internal-employee"]')?.click();
    fixture.detectChanges();
    submit(host);

    expect(saved).toEqual([
      {
        scope: 'specific-accounts',
        sharedWithAccountIds: ['account-internal-employee'],
        allowOriginalDownload: false,
      },
    ]);
  });

  it('explains that public sharing only allows connecting and lets the owner decide on downloads', () => {
    const { fixture, host, saved } = render({
      scope: 'private',
      sharedWithAccountIds: [],
      allowOriginalDownload: false,
    });
    choose(host, 'public');
    fixture.detectChanges();

    expect(host.textContent).toContain('只能連接使用，不能修改內容');
    host.querySelector<HTMLInputElement>('#allow-download')?.click();
    fixture.detectChanges();
    submit(host);

    expect(saved).toEqual([
      { scope: 'public', sharedWithAccountIds: [], allowOriginalDownload: true },
    ]);
  });

  it('does not save a change until the owner explicitly submits', () => {
    const { fixture, host, saved } = render({
      scope: 'private',
      sharedWithAccountIds: [],
      allowOriginalDownload: false,
    });
    choose(host, 'public');
    fixture.detectChanges();

    expect(saved).toEqual([]);
  });
});
