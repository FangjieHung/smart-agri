import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  KNOWLEDGE_UPLOAD_ACCEPT,
  KNOWLEDGE_UPLOAD_MAX_FILE_BYTES,
  offersUploadAsNewVersion,
  precheckKnowledgeUpload,
  type KnowledgeBaseId,
  type KnowledgeDocumentId,
  type KnowledgeDocumentView,
  type KnowledgeUploadRejectionReason,
} from '../../../../core/domain/knowledge-base.model';
import type { UploadKnowledgeDocumentEvent } from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';

/** 並行上傳數上限（issue #46）：其餘檔案先排隊，等有空位再開始。 */
export const MAX_CONCURRENT_UPLOADS = 3;

type UploadItemStatus = 'queued' | 'uploading' | 'succeeded' | 'rejected';

interface UploadItem {
  readonly localId: string;
  readonly file: File;
  readonly status: UploadItemStatus;
  readonly percent: number;
  readonly reason?: KnowledgeUploadRejectionReason;
  readonly message?: string;
  readonly existingDocumentName?: string;
  /** 選了「改為上傳新版本」之後，這次要上傳到哪一份既有文件。 */
  readonly targetDocumentId?: KnowledgeDocumentId;
}

let nextLocalId = 0;

/**
 * 知識庫詳情頁的「上傳文件」（issue #46，M2 Slice 12）：可一次選取或拖放多個檔案，
 * 先在前端做副檔名與大小的預檢，再以固定並行數逐檔上傳；每個檔案獨立顯示進度與結果，
 * 失敗的可以單獨重試，不影響其他檔案。只在 `detail.summary.viewerCanManage` 時顯示
 * （由呼叫端決定，這裡不重複檢查）。
 */
@Component({
  selector: 'app-knowledge-upload-panel',
  templateUrl: './upload-panel.component.html',
  styleUrl: './upload-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UploadPanelComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);

  readonly knowledgeBaseId = input.required<KnowledgeBaseId>();
  /** 目前知識庫的文件清單，用來把「檔名重複」對應回既有文件的 id（改為上傳新版本用）。 */
  readonly documents = input<readonly KnowledgeDocumentView[]>([]);
  /** 任一檔案上傳成功時送出一次，呼叫端用它觸發 `reload()`；進度改由輪詢接手。 */
  readonly uploaded = output<void>();

  protected readonly accept = KNOWLEDGE_UPLOAD_ACCEPT;
  protected readonly maxFileMegabytes = Math.round(KNOWLEDGE_UPLOAD_MAX_FILE_BYTES / (1024 * 1024));
  protected readonly items = signal<readonly UploadItem[]>([]);

  protected readonly summary = computed(() => {
    const list = this.items();
    return {
      total: list.length,
      succeeded: list.filter((item) => item.status === 'succeeded').length,
      rejected: list.filter((item) => item.status === 'rejected').length,
      pending: list.filter((item) => item.status === 'queued' || item.status === 'uploading').length,
    };
  });

  protected onFilesSelected(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.enqueue(target.files);
    target.value = '';
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.enqueue(event.dataTransfer?.files ?? null);
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
  }

  protected canRetryAsNewVersion(item: UploadItem): boolean {
    return (
      item.status === 'rejected' &&
      item.reason !== undefined &&
      offersUploadAsNewVersion(item.reason) &&
      this.matchingDocument(item.file.name) !== undefined
    );
  }

  protected retry(item: UploadItem): void {
    if (item.status === 'uploading') return;
    this.updateItem(item.localId, {
      status: 'queued',
      percent: 0,
      reason: undefined,
      message: undefined,
      existingDocumentName: undefined,
    });
    this.pump();
  }

  protected retryAsNewVersion(item: UploadItem): void {
    if (item.status === 'uploading') return;
    const target = this.matchingDocument(item.file.name);
    if (target === undefined) return;
    this.updateItem(item.localId, {
      status: 'queued',
      percent: 0,
      reason: undefined,
      message: undefined,
      existingDocumentName: undefined,
      targetDocumentId: target.id,
    });
    this.pump();
  }

  protected dismiss(item: UploadItem): void {
    this.items.update((current) => current.filter((entry) => entry.localId !== item.localId));
  }

  private matchingDocument(fileName: string): KnowledgeDocumentView | undefined {
    return this.documents().find((document) => document.name === fileName);
  }

  private enqueue(files: FileList | null): void {
    if (files === null || files.length === 0) return;
    const added: UploadItem[] = Array.from(files).map((file) => {
      const precheck = precheckKnowledgeUpload(file);
      nextLocalId += 1;
      return {
        localId: `upload-${nextLocalId}`,
        file,
        status: precheck === null ? 'queued' : 'rejected',
        percent: 0,
        reason: precheck?.reason,
        message: precheck?.message,
      };
    });
    this.items.update((current) => [...current, ...added]);
    this.pump();
  }

  /**
   * 只要有空位就從排隊中的檔案開始上傳，維持並行數上限。mock 的請求是同步完成的
   * （訂閱當下就送出結果），`startUpload` 可能在這個迴圈跑到一半時就遞迴呼叫 `pump()`
   * 並把後面排隊的檔案都處理完——所以這裡每一輪都重新讀一次最新的 `items()`，
   * 不能像 `forEach` 那樣先取一份清單快照，否則會對已經處理過的項目重複送出請求。
   */
  private pump(): void {
    for (;;) {
      const runningCount = this.items().filter((item) => item.status === 'uploading').length;
      if (runningCount >= MAX_CONCURRENT_UPLOADS) return;
      const next = this.items().find((item) => item.status === 'queued');
      if (next === undefined) return;
      this.startUpload(next);
    }
  }

  private startUpload(item: UploadItem): void {
    this.updateItem(item.localId, { status: 'uploading', percent: 0 });
    const request$ =
      item.targetDocumentId === undefined
        ? this.repository.uploadKnowledgeDocument(this.knowledgeBaseId(), item.file)
        : this.repository.uploadKnowledgeDocumentVersion(this.knowledgeBaseId(), item.targetDocumentId, item.file);
    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (event) => this.handleEvent(item.localId, event),
      error: () => {
        this.updateItem(item.localId, {
          status: 'rejected',
          reason: undefined,
          message: '這個檔案沒有上傳成功，請稍後再試。',
        });
        this.pump();
      },
    });
  }

  private handleEvent(localId: string, event: UploadKnowledgeDocumentEvent): void {
    if (event.status === 'progress') {
      this.updateItem(localId, { percent: event.percent });
      return;
    }
    if (event.status === 'ready' || event.status === 'partial-failure') {
      this.updateItem(localId, { status: 'succeeded', percent: 100 });
      this.uploaded.emit();
      this.pump();
      return;
    }
    if (event.status === 'rejected') {
      this.updateItem(localId, {
        status: 'rejected',
        reason: event.reason,
        message: event.message,
        existingDocumentName: event.existingDocumentName,
      });
      this.pump();
      return;
    }
    // loading／partial-failure（Demo 情境切換器）／permission-denied：一律當作這個檔案沒有上傳成功。
    this.updateItem(localId, {
      status: 'rejected',
      reason: undefined,
      message: event.status === 'permission-denied' ? event.message : '目前無法上傳，請稍後再試。',
    });
    this.pump();
  }

  private updateItem(localId: string, patch: Partial<UploadItem>): void {
    this.items.update((current) =>
      current.map((item) => (item.localId === localId ? { ...item, ...patch } : item)),
    );
  }
}
