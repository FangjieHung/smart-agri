import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { firstValueFrom, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import type { KnowledgeDocumentView } from '../../../../core/domain/knowledge-base.model';
import type {
  ApproveKnowledgeVersionsResult,
  DisableKnowledgeDocumentResult,
} from '../../../../core/repositories/demo-repository';
import { provideKnowledgeTesting } from '../../knowledge.testing';
import { ReviewDialogComponent, type ReviewDialogData } from './review-dialog.component';

const DOCUMENT: KnowledgeDocumentView = {
  id: 'document-guide-specs',
  kind: 'document',
  name: '商品規格總表.pdf',
  status: 'ready',
  issue: null,
  updatedAt: '2026-09-18T07:40:00.000Z',
  latestVersionId: 'document-guide-specs:v1',
  latestVersionNumber: 1,
  latestVersionState: 'effective',
  effectiveVersionNumber: 1,
  disabled: false,
  inEffect: true,
};

function render(overrides: Partial<ReviewDialogData> = {}, testing = provideKnowledgeTesting('account-smb-admin')) {
  const dialogRef = { close: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      ...testing.providers,
      { provide: MatDialogRef, useValue: dialogRef },
      {
        provide: MAT_DIALOG_DATA,
        useValue: {
          knowledgeBaseId: 'knowledge-product-guide',
          document: DOCUMENT,
          canManage: true,
          ...overrides,
        } satisfies ReviewDialogData,
      },
    ],
  });
  const fixture = TestBed.createComponent(ReviewDialogComponent);
  fixture.detectChanges();
  return { fixture, dialogRef, repository: testing.repository };
}

/** 先透過真實的 mock 上傳一份新文件（一定是待確認狀態），再用它開對話框。 */
async function renderWithPendingDocument() {
  const testing = provideKnowledgeTesting('account-smb-admin');
  const file = new File([new Uint8Array(10)], '新版商品規格.pdf', { type: 'application/pdf' });
  const uploaded = await firstValueFrom(testing.repository.uploadKnowledgeDocument('knowledge-product-guide', file));
  if (uploaded.status !== 'ready') throw new Error(`expected ready, got ${uploaded.status}`);
  return render({ document: uploaded.data }, testing);
}

async function settle(fixture: ReturnType<typeof render>['fixture']): Promise<void> {
  await fixture.whenStable();
  fixture.detectChanges();
}

function buttonNamed(host: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.includes(label));
}

function setDisableReason(host: HTMLElement, value: string): void {
  const input = host.querySelector<HTMLInputElement>('#knowledge-disable-reason');
  if (!input) throw new Error('#knowledge-disable-reason not found');
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('ReviewDialogComponent', () => {
  it('loads and shows the version history and the effective status', async () => {
    const { fixture } = render();
    await settle(fixture);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('商品規格總表.pdf');
    expect(host.querySelector('[data-effect="in-effect"]')?.textContent).toContain('第 1 版');
    expect(host.querySelector('.version-list')?.textContent).toContain('第 1 版');
  });

  it('requires a reason before it will send an emergency disable request', async () => {
    const { fixture, repository } = render();
    await settle(fixture);
    const disableSpy = vi.spyOn(repository, 'disableKnowledgeDocument');
    const host = fixture.nativeElement as HTMLElement;

    buttonNamed(host, '緊急停用')?.click();
    fixture.detectChanges();

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('請說明緊急停用的原因。');
    expect(disableSpy).not.toHaveBeenCalled();
  });

  it('disables the document once a reason is given, and shows the reason afterwards', async () => {
    const { fixture } = render();
    await settle(fixture);
    const host = fixture.nativeElement as HTMLElement;

    setDisableReason(host, '疑似內容錯誤，暫停使用');
    fixture.detectChanges();

    buttonNamed(host, '緊急停用')?.click();
    await settle(fixture);

    expect(host.querySelector('[data-effect="disabled"]')).toBeTruthy();
    expect(host.textContent).toContain('疑似內容錯誤，暫停使用');
    expect(buttonNamed(host, '恢復使用')).toBeTruthy();
  });

  it('shows the API’s message when the disable request is refused (e.g. already disabled)', async () => {
    const { fixture, repository } = render();
    await settle(fixture);
    const refusal: DisableKnowledgeDocumentResult = {
      status: 'validation-failed',
      message: '這份文件已經停用了。',
    };
    vi.spyOn(repository, 'disableKnowledgeDocument').mockReturnValue(of(refusal));
    const host = fixture.nativeElement as HTMLElement;

    setDisableReason(host, '原因');
    fixture.detectChanges();
    buttonNamed(host, '緊急停用')?.click();
    await settle(fixture);

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('這份文件已經停用了。');
  });

  it('says so when the disable request cannot be sent at all', async () => {
    const { fixture, repository } = render();
    await settle(fixture);
    vi.spyOn(repository, 'disableKnowledgeDocument').mockReturnValue(throwError(() => new Error('offline')));
    const host = fixture.nativeElement as HTMLElement;

    setDisableReason(host, '原因');
    fixture.detectChanges();
    buttonNamed(host, '緊急停用')?.click();
    await settle(fixture);

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('目前無法停用');
  });

  it('shows a validation error and does not close when a version cannot be approved (issue #47 batch confirm 422)', async () => {
    const { fixture, repository } = await renderWithPendingDocument();
    await settle(fixture);
    const refusal: ApproveKnowledgeVersionsResult = {
      status: 'validation-failed',
      message: '這些版本剛剛有其他變更，或不是待確認的版本，請重新整理後再試一次。',
    };
    vi.spyOn(repository, 'approveKnowledgeVersions').mockReturnValue(of(refusal));
    const host = fixture.nativeElement as HTMLElement;

    buttonNamed(host, '確認生效')?.click();
    await settle(fixture);

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('這些版本剛剛有其他變更');
  });

  it('does not offer disable, enable or exclusion controls to a viewer without manage access', async () => {
    const { fixture } = render({ canManage: false });
    await settle(fixture);
    const host = fixture.nativeElement as HTMLElement;

    expect(buttonNamed(host, '緊急停用')).toBeUndefined();
    expect(buttonNamed(host, '恢復使用')).toBeUndefined();
    expect(host.querySelector('.chunk-row input[type="checkbox"]')?.hasAttribute('disabled')).toBe(true);
  });
});
