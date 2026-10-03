import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import type { ChatFormView } from '../../../core/domain/conversation.model';
import type {
  DatabaseFieldError,
  DatabaseRecordEntryView,
  DatabaseSubmissionFormView,
  DatabaseSubmissionReceiptView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';
import { fmtDateTime } from '../../../core/date-utils';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { ConsentConfirmationComponent } from '../../assistant-use/consent-confirmation/consent-confirmation.component';
import { InlineFormComponent } from '../../assistant-use/inline-form/inline-form.component';
import { RECORD_SOURCE_LABELS } from '../database-labels';

type Step = 'fill' | 'confirm' | 'receipt';

/** 這一次填寫的提交編號（冪等鍵）：同一次填寫的每一次重送都沿用，換一份新的填寫才重新產生。 */
function newSubmissionKey(): string {
  return crypto.randomUUID();
}

/**
 * 表單連結的填寫頁（issue #145）：先填寫 → 伺服器檢查（不建立紀錄）→ 看清楚接收單位、用途、
 * 實際可查看者與敏感資料提示後明確同意 → 送出並取得回執。
 *
 * 可恢復的狀態：欄位錯誤標在欄位上；表單在載入後改版時提示重新載入（這次沒有送出）；送出時連線
 * 中斷或伺服器錯誤保留答案，以同一個提交編號再送一次，伺服器保證不會建立第二筆。回執的網址帶
 * `?receipt=<id>`，重新整理仍看得到（只有送出者本人拿得到）。
 *
 * 私人對話內容不經過這裡：送出的只有這份表單的答案。助理在對話中請求表單（#148）走另一個入口，
 * 共用伺服器端同一個提交服務。
 */
@Component({
  selector: 'app-database-submission-page',
  imports: [PageHeaderComponent, StatePanelComponent, InlineFormComponent, ConsentConfirmationComponent],
  templateUrl: './database-submission-page.component.html',
  styleUrl: './database-submission-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseSubmissionPageComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

  protected readonly databaseId = computed(() => this.params().get('databaseId') ?? '');
  private readonly receiptId = computed(() => this.query().get('receipt'));

  private readonly form = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId && this.receiptId() === null ? { accountId, databaseId: this.databaseId() } : undefined;
    },
    stream: ({ databaseId }) => this.repository.getDatabaseSubmissionForm(databaseId),
  });
  protected readonly view = this.form.view;

  private readonly savedReceipt = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      const receiptId = this.receiptId();
      return accountId && receiptId !== null ? { accountId, receiptId } : undefined;
    },
    stream: ({ receiptId }) => this.repository.getDatabaseSubmissionReceipt(receiptId),
  });

  protected readonly step = signal<Step>('fill');
  protected readonly answers = signal<DatabaseTrialAnswers>({});
  protected readonly fieldErrors = signal<readonly DatabaseFieldError[]>([]);
  protected readonly reviewEntries = signal<readonly DatabaseRecordEntryView[]>([]);
  /** 這一次送出（或回執頁讀到）的回執。 */
  protected readonly receipt = signal<DatabaseSubmissionReceiptView | null>(null);
  protected readonly busy = signal(false);
  /** 確認同意畫面上的錯誤（未同意、送出失敗可再試）。 */
  protected readonly consentError = signal('');
  /** 填寫畫面上方的說明（檢查失敗可再試）。 */
  protected readonly fillNotice = signal('');
  /** 表單已改版：這次沒有送出，必須重新載入最新表單。 */
  protected readonly conflictMessage = signal('');
  private submissionKey = newSubmissionKey();

  /** 回執頁（重新整理或從網址開啟）。 */
  protected readonly receiptView = computed<RepositoryView<DatabaseSubmissionReceiptView> | { readonly status: 'error' } | null>(() => {
    const receiptId = this.receiptId();
    if (receiptId === null) return null;
    const local = this.receipt();
    return local !== null && local.id === receiptId ? { status: 'ready', data: local } : this.savedReceipt.view();
  });

  /** 填寫與確認元件沿用對話中表單的形狀。 */
  protected readonly chatForm = computed<ChatFormView | null>(() => {
    const result = this.view();
    if (result.status !== 'ready' && result.status !== 'partial-failure') return null;
    const data = result.data;
    return {
      id: data.databaseId,
      title: data.databaseName,
      formVersion: data.formVersion,
      fields: data.fields,
      consent: {
        recipient: data.recipient,
        purpose: data.purpose,
        viewers: data.viewers,
        sensitiveNotice: data.sensitiveNotice,
        withdrawalNotice: data.withdrawalNotice,
      },
    };
  });

  constructor() {
    // 換一份資料庫（或換身分）就是一份新的填寫。
    effect(() => {
      this.databaseId();
      this.session.activeAccountId();
      untracked(() => this.startOver());
    });
    // 離開回執（網址不再帶 receipt）就是一份新的填寫：新的提交編號，不會被當成上一份的重送。
    effect(() => {
      if (this.receiptId() === null && untracked(() => this.step()) === 'receipt') untracked(() => this.startOver());
    });
  }

  protected submittedAt(value: string): string {
    return fmtDateTime(value);
  }

  protected sourceLabel(receipt: DatabaseSubmissionReceiptView): string {
    return RECORD_SOURCE_LABELS[receipt.source];
  }

  protected reload(): void {
    this.startOver();
    this.form.reload();
  }

  protected review(answers: DatabaseTrialAnswers): void {
    const form = this.loadedForm();
    if (form === null || this.busy()) return;
    this.answers.set(answers);
    this.fillNotice.set('');
    this.busy.set(true);
    this.repository
      .reviewDatabaseSubmission(form.databaseId, form.formVersion, answers)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          switch (result.status) {
            case 'ready':
            case 'partial-failure':
              this.fieldErrors.set([]);
              this.reviewEntries.set(result.data.entries);
              this.consentError.set('');
              this.step.set('confirm');
              return;
            case 'validation-failed':
              this.fieldErrors.set(result.errors);
              return;
            case 'conflict':
              this.conflictMessage.set(result.message);
              return;
            case 'permission-denied':
              this.fillNotice.set(result.message);
              return;
            case 'loading':
              return;
          }
        },
        error: () => {
          this.busy.set(false);
          this.fillNotice.set('目前無法檢查你填寫的內容，資料尚未送出。你填的內容還在，請稍後再按一次「下一步」。');
        },
      });
  }

  protected backToForm(): void {
    this.consentError.set('');
    this.step.set('fill');
  }

  protected submit(): void {
    const form = this.loadedForm();
    if (form === null || this.busy()) return;
    this.busy.set(true);
    this.consentError.set('');
    this.repository
      .submitDatabaseEntry(form.databaseId, {
        submissionId: this.submissionKey,
        formVersion: form.formVersion,
        consent: true,
        answers: this.answers(),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          switch (result.status) {
            case 'ready':
            case 'partial-failure':
              this.showReceipt(result.data);
              return;
            case 'validation-failed':
              if (result.errors.some((error) => error.fieldId !== null)) {
                this.fieldErrors.set(result.errors);
                this.step.set('fill');
              } else {
                this.consentError.set(result.message);
              }
              return;
            case 'conflict':
              // 表單改版或編號已被另一份內容使用：這次沒有送出。換新的編號，請重新載入再填。
              this.submissionKey = newSubmissionKey();
              this.conflictMessage.set(result.message);
              this.step.set('fill');
              return;
            case 'permission-denied':
              this.consentError.set(result.message);
              return;
            case 'loading':
              return;
          }
        },
        error: () => {
          this.busy.set(false);
          // 同一個提交編號再送一次：就算上一次其實已經寫入，伺服器也只會回同一張回執。
          this.consentError.set('送出時發生問題，無法確認是否已送出。請再按一次「同意並送出」，不會重複建立紀錄。');
        },
      });
  }

  protected fillAnother(): void {
    this.startOver();
    void this.router.navigate([], { relativeTo: this.route, queryParams: { receipt: null }, replaceUrl: true });
  }

  protected cancel(): void {
    void this.router.navigateByUrl('/app/home');
  }

  private showReceipt(receipt: DatabaseSubmissionReceiptView): void {
    this.receipt.set(receipt);
    this.step.set('receipt');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { receipt: receipt.id },
      replaceUrl: true,
    });
  }

  private startOver(): void {
    this.submissionKey = newSubmissionKey();
    this.step.set('fill');
    this.answers.set({});
    this.fieldErrors.set([]);
    this.reviewEntries.set([]);
    this.receipt.set(null);
    this.consentError.set('');
    this.fillNotice.set('');
    this.conflictMessage.set('');
  }

  private loadedForm(): DatabaseSubmissionFormView | null {
    const result = this.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  }
}
