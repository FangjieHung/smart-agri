import { TestBed } from '@angular/core/testing';
import { firstValueFrom, Subject } from 'rxjs';
import type { KnowledgeDocumentView } from '../../../../core/domain/knowledge-base.model';
import type { UploadKnowledgeDocumentEvent } from '../../../../core/repositories/demo-repository';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { MAX_CONCURRENT_UPLOADS, UploadPanelComponent } from './upload-panel.component';

function file(name: string, size = 10, type = 'application/pdf'): File {
  return new File([new Uint8Array(size)], name, { type });
}

interface Call {
  readonly knowledgeBaseId: string;
  readonly file: File;
  readonly documentId?: string;
}

/** 假 repository：每次呼叫回傳一個可控制的 Subject，測試逐一送出進度與結果事件。 */
class FakeRepository {
  readonly calls: Call[] = [];
  private readonly subjects: Subject<UploadKnowledgeDocumentEvent>[] = [];

  uploadKnowledgeDocument(knowledgeBaseId: string, uploadedFile: File) {
    this.calls.push({ knowledgeBaseId, file: uploadedFile });
    const subject = new Subject<UploadKnowledgeDocumentEvent>();
    this.subjects.push(subject);
    return subject.asObservable();
  }

  uploadKnowledgeDocumentVersion(knowledgeBaseId: string, documentId: string, uploadedFile: File) {
    this.calls.push({ knowledgeBaseId, file: uploadedFile, documentId });
    const subject = new Subject<UploadKnowledgeDocumentEvent>();
    this.subjects.push(subject);
    return subject.asObservable();
  }

  /** 依檔名找到「最後一次」針對這個檔名的呼叫送出的 subject（同名檔案重試會有多個）。 */
  private subjectFor(fileName: string): Subject<UploadKnowledgeDocumentEvent> {
    const index = [...this.calls].map((call) => call.file.name).lastIndexOf(fileName);
    const subject = this.subjects[index];
    if (subject === undefined) throw new Error(`no pending upload for ${fileName}`);
    return subject;
  }

  progress(fileName: string, percent: number): void {
    this.subjectFor(fileName).next({ status: 'progress', percent });
  }

  resolve(fileName: string, event: UploadKnowledgeDocumentEvent): void {
    const subject = this.subjectFor(fileName);
    subject.next(event);
    subject.complete();
  }

  callCountFor(fileName: string): number {
    return this.calls.filter((call) => call.file.name === fileName).length;
  }
}

function render(documents: readonly KnowledgeDocumentView[] = []) {
  const repository = new FakeRepository();
  TestBed.configureTestingModule({
    imports: [UploadPanelComponent],
    providers: [{ provide: DEMO_REPOSITORY, useValue: repository }],
  });
  const fixture = TestBed.createComponent(UploadPanelComponent);
  fixture.componentRef.setInput('knowledgeBaseId', 'kb-1');
  fixture.componentRef.setInput('documents', documents);
  fixture.detectChanges();
  return { fixture, repository };
}

function selectFiles(fixture: ReturnType<typeof render>['fixture'], files: readonly File[]): void {
  const input = (fixture.nativeElement as HTMLElement).querySelector('input[type="file"]') as HTMLInputElement;
  Object.defineProperty(input, 'files', { value: files, configurable: true });
  input.dispatchEvent(new Event('change'));
  fixture.detectChanges();
}

function rowFor(fixture: ReturnType<typeof render>['fixture'], fileName: string): HTMLElement {
  const row = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.upload-item')).find((item) =>
    item.textContent?.includes(fileName),
  );
  if (row === undefined) throw new Error(`no row for ${fileName}`);
  return row as HTMLElement;
}

function buttonNamed(host: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.includes(label));
}

const BASE_DOCUMENT: KnowledgeDocumentView = {
  id: 'document-existing',
  kind: 'document',
  name: '既有文件.pdf',
  status: 'ready',
  issue: null,
  updatedAt: '2026-09-18T08:00:00.000Z',
  latestVersionId: 'document-existing:v1',
};

describe('UploadPanelComponent', () => {
  it('rejects an unsupported file at the front end without calling the repository', () => {
    const { fixture, repository } = render();
    selectFiles(fixture, [file('病毒.exe', 10, 'application/octet-stream')]);

    expect(repository.calls).toHaveLength(0);
    expect(rowFor(fixture, '病毒.exe').textContent).toContain('只支援');
  });

  it('rejects a file larger than the limit at the front end without calling the repository', () => {
    const { fixture, repository } = render();
    selectFiles(fixture, [file('太大.pdf', 21 * 1024 * 1024)]);

    expect(repository.calls).toHaveLength(0);
    expect(rowFor(fixture, '太大.pdf').textContent).toContain('MB 的上限');
  });

  it('caps concurrent uploads and starts the next one only after a slot frees up', () => {
    const { fixture, repository } = render();
    const files = [1, 2, 3, 4, 5].map((n) => file(`檔案-${n}.pdf`));
    selectFiles(fixture, files);

    expect(repository.calls).toHaveLength(MAX_CONCURRENT_UPLOADS);
    expect(rowFor(fixture, '檔案-4.pdf').textContent).toContain('等待上傳');
    expect(rowFor(fixture, '檔案-5.pdf').textContent).toContain('等待上傳');

    repository.resolve('檔案-1.pdf', { status: 'ready', data: { ...BASE_DOCUMENT, id: 'd1', name: '檔案-1.pdf' } });
    fixture.detectChanges();

    expect(repository.calls).toHaveLength(MAX_CONCURRENT_UPLOADS + 1);
    expect(rowFor(fixture, '檔案-4.pdf').textContent).toContain('上傳中');
  });

  it('shows upload progress while a file is uploading', () => {
    const { fixture, repository } = render();
    selectFiles(fixture, [file('進度中.pdf')]);

    repository.progress('進度中.pdf', 42);
    fixture.detectChanges();

    expect(rowFor(fixture, '進度中.pdf').textContent).toContain('42%');
  });

  it(
    '5 個檔案裡 1 個回 415、1 個回 422 duplicate-content，其餘成功；只重試失敗的那 1 個，' +
      '另外 3 個不會被重複送出（issue #46 驗收條件）',
    () => {
      const { fixture, repository } = render();
      const names = ['正常一.pdf', '偽裝檔案.pdf', '正常二.pdf', '重複內容.pdf', '正常三.pdf'];
      selectFiles(fixture, names.map((name) => file(name)));

      // 並行上限 3：前三個先開始。
      repository.resolve('正常一.pdf', { status: 'ready', data: { ...BASE_DOCUMENT, id: 'd1', name: '正常一.pdf' } });
      repository.resolve('偽裝檔案.pdf', {
        status: 'rejected',
        reason: 'file-content-mismatch',
        message: '檔案內容不是有效的 PDF，可能已損毀，或只是改了副檔名。',
      });
      repository.resolve('正常二.pdf', { status: 'ready', data: { ...BASE_DOCUMENT, id: 'd2', name: '正常二.pdf' } });
      fixture.detectChanges();

      // 有空位了，剩下兩個開始上傳。
      repository.resolve('重複內容.pdf', {
        status: 'rejected',
        reason: 'duplicate-content',
        message: '這份檔案的內容與「既有文件.pdf」完全相同，不需要重複上傳。',
        existingDocumentName: '既有文件.pdf',
      });
      repository.resolve('正常三.pdf', { status: 'ready', data: { ...BASE_DOCUMENT, id: 'd3', name: '正常三.pdf' } });
      fixture.detectChanges();

      expect(repository.calls).toHaveLength(5);
      expect(rowFor(fixture, '正常一.pdf').textContent).toContain('上傳成功');
      expect(rowFor(fixture, '正常二.pdf').textContent).toContain('上傳成功');
      expect(rowFor(fixture, '正常三.pdf').textContent).toContain('上傳成功');
      expect(rowFor(fixture, '偽裝檔案.pdf').textContent).toContain('檔案內容不是有效的 PDF');
      expect(rowFor(fixture, '重複內容.pdf').textContent).toContain('既有文件.pdf');
      expect((fixture.nativeElement as HTMLElement).querySelector('.upload-summary')?.textContent).toContain(
        '成功 3 檔、失敗 2 檔',
      );

      // 只重試失敗的那一個。
      const retryButton = buttonNamed(rowFor(fixture, '偽裝檔案.pdf'), '重新上傳') as HTMLButtonElement;
      retryButton.click();
      fixture.detectChanges();

      expect(repository.callCountFor('偽裝檔案.pdf')).toBe(2);
      expect(repository.callCountFor('正常一.pdf')).toBe(1);
      expect(repository.callCountFor('正常二.pdf')).toBe(1);
      expect(repository.callCountFor('正常三.pdf')).toBe(1);
      expect(repository.callCountFor('重複內容.pdf')).toBe(1);
    },
  );

  it('offers uploading as a new version only for a duplicate-name rejection, and calls the version endpoint', () => {
    const { fixture, repository } = render([BASE_DOCUMENT]);
    selectFiles(fixture, [file('既有文件.pdf')]);

    repository.resolve('既有文件.pdf', {
      status: 'rejected',
      reason: 'duplicate-name',
      message: '這個知識庫已經有名為「既有文件.pdf」的文件。要更新它的內容，請改用「上傳新版本」。',
    });
    fixture.detectChanges();

    const row = rowFor(fixture, '既有文件.pdf');
    const newVersionButton = buttonNamed(row, '改為上傳新版本') as HTMLButtonElement;
    expect(newVersionButton).toBeDefined();
    newVersionButton.click();
    fixture.detectChanges();

    expect(repository.calls.at(-1)).toMatchObject({ documentId: 'document-existing' });
  });

  it('does not offer uploading as a new version for other rejection reasons', () => {
    const { fixture, repository } = render([BASE_DOCUMENT]);
    selectFiles(fixture, [file('偽裝.pdf')]);

    repository.resolve('偽裝.pdf', {
      status: 'rejected',
      reason: 'file-content-mismatch',
      message: '檔案內容不是有效的 PDF，可能已損毀，或只是改了副檔名。',
    });
    fixture.detectChanges();

    expect(buttonNamed(rowFor(fixture, '偽裝.pdf'), '改為上傳新版本')).toBeUndefined();
  });

  it('emits uploaded once a file succeeds, so the page can reload', () => {
    const { fixture, repository } = render();
    const emitted: void[] = [];
    fixture.componentInstance.uploaded.subscribe(() => emitted.push(undefined));
    selectFiles(fixture, [file('成功.pdf')]);

    repository.resolve('成功.pdf', { status: 'ready', data: { ...BASE_DOCUMENT, id: 'd1', name: '成功.pdf' } });
    fixture.detectChanges();

    expect(emitted).toHaveLength(1);
  });

  it(
    'does not double-upload files against a repository that resolves synchronously (regression: ' +
      'the mock resolves each upload immediately, which can otherwise cascade past the concurrency cap)',
    async () => {
      const repository = new MockDemoRepository(DEMO_SEED, {
        storage: createMemoryStorage(),
        viewer: () => 'account-smb-admin',
      });
      TestBed.configureTestingModule({
        imports: [UploadPanelComponent],
        providers: [{ provide: DEMO_REPOSITORY, useValue: repository }],
      });
      const fixture = TestBed.createComponent(UploadPanelComponent);
      fixture.componentRef.setInput('knowledgeBaseId', 'knowledge-product-guide');
      fixture.detectChanges();

      const names = ['同步一.pdf', '同步二.pdf', '同步三.pdf', '同步四.pdf'];
      selectFiles(fixture, names.map((name, index) => file(name, 100 + index)));
      fixture.detectChanges();

      for (const name of names) {
        expect(rowFor(fixture, name).textContent).toContain('上傳成功');
      }
      const detail = await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-product-guide'));
      if (detail.status !== 'ready') throw new Error('expected ready');
      for (const name of names) {
        expect(detail.data.documents.filter((document) => document.name === name)).toHaveLength(1);
      }
    },
  );

  it('lets a failed upload be dismissed from the list', () => {
    const { fixture, repository } = render();
    selectFiles(fixture, [file('失敗.pdf')]);
    repository.resolve('失敗.pdf', { status: 'rejected', reason: 'file-too-large', message: '太大了。' });
    fixture.detectChanges();

    const dismissButton = buttonNamed(rowFor(fixture, '失敗.pdf'), '移除') as HTMLButtonElement;
    dismissButton.click();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('失敗.pdf');
  });
});
