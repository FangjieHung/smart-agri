import { DOCUMENT, DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import type {
  AssistantTestCaseImportEntry,
  AssistantTestCaseInput,
  AssistantTestCaseView,
  AssistantTestRunView,
} from '../../../../../core/domain/assistant-acceptance.model';
import {
  pollWhile,
  repositoryResource,
} from '../../../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../../../core/repositories/tokens';

const RUN_POLL_INTERVAL_MS = 3000;

type LoadState = 'loading' | 'error' | 'permission-denied' | 'ready';

@Component({
  selector: 'app-assistant-acceptance-tab',
  imports: [DatePipe, RouterLink],
  templateUrl: './assistant-acceptance-tab.component.html',
  styleUrl: './assistant-acceptance-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantAcceptanceTabComponent {
  readonly assistantId = input.required<string>();
  readonly acceptanceStatus = input<string>('not-accepted');
  readonly runCompleted = output<void>();

  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly initiatedRunId = signal<string | null>(null);
  private readonly localAcceptanceStatus = signal<string | null>(null);

  protected readonly message = signal('');
  protected readonly busy = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly question = signal('');
  protected readonly category = signal<AssistantTestCaseView['category']>('common');
  protected readonly expectedKind = signal<AssistantTestCaseView['expectedKind']>('company-data');
  protected readonly expectedDocumentIds = signal('');
  protected readonly selectedRunId = signal<string | null>(null);

  private readonly caseResource = repositoryResource({
    params: () => this.assistantId() || undefined,
    stream: (id) => this.repository.listAssistantTestCases(id),
  });
  private readonly runResource = repositoryResource({
    params: () => this.assistantId() || undefined,
    stream: (id) =>
      pollWhile(
        () => this.repository.listAssistantTestRuns(id),
        (view) =>
          (view.status === 'ready' || view.status === 'partial-failure') &&
          view.data.some((run) => run.status === 'queued' || run.status === 'running'),
        { intervalMs: RUN_POLL_INTERVAL_MS, document: this.document },
      ),
  });
  private readonly selectedRunResource = repositoryResource({
    params: () => {
      const runId = this.selectedRunId();
      return runId ? { assistantId: this.assistantId(), runId } : undefined;
    },
    stream: ({ assistantId, runId }) => this.repository.getAssistantTestRun(assistantId, runId),
  });

  protected readonly cases = computed<readonly AssistantTestCaseView[]>(() => {
    const view = this.caseResource.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : [];
  });
  protected readonly runs = computed<readonly AssistantTestRunView[]>(() => {
    const view = this.runResource.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : [];
  });
  protected readonly selectedRun = computed(() => {
    const view = this.selectedRunResource.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : null;
  });
  protected readonly selectedRunError = computed(
    () => this.selectedRunResource.view().status === 'error',
  );
  protected readonly state = computed<LoadState>(() => {
    const cases = this.caseResource.view();
    const runs = this.runResource.view();
    if (cases.status === 'permission-denied' || runs.status === 'permission-denied')
      return 'permission-denied';
    if (cases.status === 'error' || runs.status === 'error') return 'error';
    return cases.status === 'loading' || runs.status === 'loading' ? 'loading' : 'ready';
  });
  protected readonly permissionMessage = computed(() => {
    const cases = this.caseResource.view();
    const runs = this.runResource.view();
    return cases.status === 'permission-denied'
      ? cases.message
      : runs.status === 'permission-denied'
        ? runs.message
        : '';
  });
  protected readonly shownAcceptanceStatus = computed(
    () => this.localAcceptanceStatus() ?? this.acceptanceStatus(),
  );

  constructor() {
    effect(() => {
      const id = this.initiatedRunId();
      if (!id) return;
      const run = this.runs().find((item) => item.id === id);
      if (run && (run.status === 'completed' || run.status === 'failed')) {
        this.localAcceptanceStatus.set(
          run.status === 'failed' || run.failedCount > 0 ? 'failed' : 'passed',
        );
        this.initiatedRunId.set(null);
        this.runCompleted.emit();
        this.selectedRunResource.reload();
      }
    });
  }

  protected reload(): void {
    this.message.set('');
    this.caseResource.reload();
    this.runResource.reload();
    if (this.selectedRunId()) this.selectedRunResource.reload();
  }

  protected statusLabel(status: string): string {
    return (
      (
        {
          'not-accepted': '尚未驗收',
          passed: '通過',
          failed: '未通過',
          outdated: '已過期',
        } as Record<string, string>
      )[status] ?? '尚未驗收'
    );
  }

  protected runStatusLabel(status: AssistantTestRunView['status']): string {
    return { queued: '排隊中', running: '執行中', completed: '已完成', failed: '執行失敗' }[status];
  }

  protected answerKindLabel(kind: AssistantTestCaseView['expectedKind']): string {
    return {
      'company-data': '公司資料',
      'general-knowledge': '一般知識',
      'no-result': '查無結果',
    }[kind];
  }

  protected hasActiveRun(): boolean {
    return this.runs().some((run) => run.status === 'queued' || run.status === 'running');
  }

  protected canRun(): boolean {
    return (
      this.state() === 'ready' && this.cases().length > 0 && !this.busy() && !this.hasActiveRun()
    );
  }

  protected saveCase(): void {
    if (this.busy() || !this.question().trim()) return;
    const payload: AssistantTestCaseInput = {
      question: this.question().trim(),
      category: this.category(),
      expectedKind: this.expectedKind(),
      expectedDocumentIds: this.expectedDocumentIds()
        .split(/[\s,]+/)
        .map((id) => id.trim())
        .filter(Boolean),
      followUpOfId: null,
    };
    const editingId = this.editingId();
    const request = editingId
      ? this.repository.updateAssistantTestCase(this.assistantId(), editingId, payload)
      : this.repository.createAssistantTestCase(this.assistantId(), payload);
    this.busy.set(true);
    this.message.set('');
    request
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (view) => {
          if (view.status === 'ready') {
            this.clearForm();
            this.reload();
          } else if (view.status === 'permission-denied') {
            this.message.set(view.message);
          }
        },
        error: () => this.message.set('儲存題目失敗，請檢查欄位後再試。'),
      });
  }

  protected editCase(item: AssistantTestCaseView): void {
    this.editingId.set(item.id);
    this.question.set(item.question);
    this.category.set(item.category);
    this.expectedKind.set(item.expectedKind);
    this.expectedDocumentIds.set(item.expectedDocumentIds.join(', '));
  }

  protected cancelEdit(): void {
    this.clearForm();
  }

  protected removeCase(item: AssistantTestCaseView): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set('');
    this.repository
      .deleteAssistantTestCase(this.assistantId(), item.id)
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (view) => {
          if (view.status === 'ready') this.reload();
          else if (view.status === 'permission-denied') this.message.set(view.message);
        },
        error: () => this.message.set('刪除題目失敗，請稍後再試。'),
      });
  }

  protected runAll(): void {
    if (!this.canRun()) return;
    this.busy.set(true);
    this.message.set('');
    this.repository
      .createAssistantTestRun(this.assistantId())
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (view) => {
          if (view.status === 'ready') {
            this.initiatedRunId.set(view.data.id);
            this.selectedRunId.set(view.data.id);
            this.runResource.reload();
          } else if (view.status === 'permission-denied') {
            this.message.set(view.message);
          }
        },
        error: () => this.message.set('目前無法排入重跑，請稍後再試。'),
      });
  }

  protected openRun(run: AssistantTestRunView): void {
    this.selectedRunId.set(run.id);
  }

  protected async importFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file || this.busy()) return;
    try {
      const parsed: unknown = JSON.parse(await file.text());
      const questions = Array.isArray(parsed)
        ? parsed
        : typeof parsed === 'object' && parsed !== null && 'questions' in parsed
          ? parsed.questions
          : null;
      if (!Array.isArray(questions) || !questions.every(isImportEntry))
        throw new Error('invalid questions');
      const entries: AssistantTestCaseImportEntry[] = questions.map((entry) => ({
        id: entry.id ?? null,
        question: entry.question,
        category: entry.category ?? null,
        expectedKind: entry.expectedKind,
        expectedCitedDocuments: entry.expectedCitedDocuments ?? null,
        followUpOf: entry.followUpOf ?? null,
      }));
      this.busy.set(true);
      this.message.set('');
      this.repository
        .importAssistantTestCases(this.assistantId(), entries)
        .pipe(
          finalize(() => this.busy.set(false)),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe({
          next: (view) => {
            if (view.status === 'ready') this.reload();
            else if (view.status === 'permission-denied') this.message.set(view.message);
          },
          error: () => this.message.set('匯入失敗，請確認 JSON 題庫格式。'),
        });
    } catch {
      this.message.set('無法讀取檔案；請使用 questions.json 格式。');
    } finally {
      input.value = '';
    }
  }

  protected exportCases(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set('');
    this.repository
      .exportAssistantTestCases(this.assistantId())
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (view) => {
          if (view.status === 'permission-denied') {
            this.message.set(view.message);
            return;
          }
          if (view.status !== 'ready') return;
          const blob = new Blob([JSON.stringify({ questions: view.data }, null, 2)], {
            type: 'application/json',
          });
          const url = URL.createObjectURL(blob);
          const anchor = this.document.createElement('a');
          anchor.href = url;
          anchor.download = 'questions.json';
          anchor.click();
          setTimeout(() => URL.revokeObjectURL(url), 0);
        },
        error: () => this.message.set('匯出失敗，請稍後再試。'),
      });
  }

  protected onQuestion(value: string): void {
    this.question.set(value);
  }
  protected onCategory(value: string): void {
    if (value === 'common' || value === 'exception' || value === 'should-refuse')
      this.category.set(value);
  }
  protected onExpectedKind(value: string): void {
    if (value === 'company-data' || value === 'general-knowledge' || value === 'no-result')
      this.expectedKind.set(value);
  }
  protected onDocumentIds(value: string): void {
    this.expectedDocumentIds.set(value);
  }

  private clearForm(): void {
    this.editingId.set(null);
    this.question.set('');
    this.category.set('common');
    this.expectedKind.set('company-data');
    this.expectedDocumentIds.set('');
  }
}

function isImportEntry(
  value: unknown,
): value is Partial<AssistantTestCaseImportEntry> & { question: string; expectedKind: string } {
  if (typeof value !== 'object' || value === null) return false;
  const entry = value as Record<string, unknown>;
  return (
    (entry['id'] == null || typeof entry['id'] === 'string') &&
    typeof entry['question'] === 'string' &&
    (entry['category'] == null || typeof entry['category'] === 'string') &&
    typeof entry['expectedKind'] === 'string' &&
    (entry['expectedCitedDocuments'] == null ||
      (Array.isArray(entry['expectedCitedDocuments']) &&
        entry['expectedCitedDocuments'].every((id) => typeof id === 'string'))) &&
    (entry['followUpOf'] == null || typeof entry['followUpOf'] === 'string')
  );
}
