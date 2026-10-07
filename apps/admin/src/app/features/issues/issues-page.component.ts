import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import {
  issueResolutionKindLabel,
  type AssistantIssueEventView,
  type AssistantIssueOpenedCaseView,
  type AssistantIssueScope,
  type AssistantIssueStatus,
  type AssistantIssueView,
  type UpdateAssistantIssueRequest,
} from '../../core/domain/assistant-issue.model';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';
import { OpenCaseDialogComponent, type OpenCaseConflict } from './open-case-dialog/open-case-dialog.component';

@Component({
  selector: 'app-issues-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent, OpenCaseDialogComponent],
  templateUrl: './issues-page.component.html',
  styleUrl: './issues-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IssuesPageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly route = inject(ActivatedRoute);

  protected readonly scope = signal<AssistantIssueScope>('all');
  protected readonly status = signal<AssistantIssueStatus | ''>('');
  protected readonly selectedId = signal(this.route.snapshot.queryParamMap.get('issue'));
  protected readonly saving = signal(false);
  protected readonly actionMessage = signal('');
  protected readonly statusChoice = signal<AssistantIssueStatus | ''>('');
  protected readonly assigneeChoice = signal('');
  protected readonly dueChoice = signal('');
  protected readonly clearDue = signal(false);
  protected readonly note = signal('');
  /** 「另開案件」對話框是否開著（issue #252）；`caseNotice` 是結果訊息，`noticeCaseId` 是可以打開的案件。 */
  protected readonly openingCase = signal(false);
  protected readonly caseNotice = signal('');
  protected readonly noticeCaseId = signal<string | null>(null);
  protected readonly resolutionKindLabel = issueResolutionKindLabel;

  protected readonly listResource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, scope: this.scope(), status: this.status() } : undefined;
    },
    stream: (params) => this.issues.list({ scope: params.scope, status: params.status || undefined }),
  });
  protected readonly listView = this.listResource.view;

  protected readonly detailResource = repositoryResource({
    params: () => this.selectedId() ?? undefined,
    stream: (id) => this.issues.get(id),
  });
  protected readonly detailView = this.detailResource.view;

  private readonly teamResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.getTeam(),
  });
  protected readonly eligibleAssignees = computed(() => {
    const view = this.teamResource.view();
    return view.status === 'ready' || view.status === 'partial-failure'
      ? view.data.members.filter((member) => member.permissions.includes('handle-assistant-issues'))
      : [];
  });

  protected select(issue: AssistantIssueView): void {
    this.selectedId.set(issue.id);
    this.resetForm();
  }

  protected startOpenCase(): void {
    this.caseNotice.set('');
    this.noticeCaseId.set(null);
    this.openingCase.set(true);
  }

  /** 對話框關閉後把焦點還給「另開案件」按鈕（按鈕還在時）或詳情標題。 */
  protected closeOpenCase(): void {
    this.openingCase.set(false);
    this.restoreFocus();
  }

  protected caseOpened(result: AssistantIssueOpenedCaseView): void {
    this.openingCase.set(false);
    this.caseNotice.set('已另開案件，這個處理事項以「非助理問題」結案。');
    this.noticeCaseId.set(result.issue.linkedCase?.canOpen ? result.caseId : null);
    this.listResource.reload();
    this.detailResource.reload();
    this.restoreFocus();
  }

  /** `409`：已另開過（帶既有案件）或剛被別人改過；重新整理詳情。 */
  protected caseConflicted(conflict: OpenCaseConflict): void {
    this.openingCase.set(false);
    this.caseNotice.set(conflict.message);
    this.noticeCaseId.set(conflict.caseId ?? null);
    this.listResource.reload();
    this.detailResource.reload();
    this.restoreFocus();
  }

  protected statusLabel(status: AssistantIssueStatus): string {
    return status === 'open' ? '待處理' : status === 'in-progress' ? '處理中' : '已解決';
  }

  protected sourceLabel(source: AssistantIssueView['source']): string {
    return source === 'handoff' ? '成員轉交' : '測試未通過';
  }

  protected failureReasonLabel(reason: NonNullable<AssistantIssueView['testFailureReason']>): string {
    return reason === 'kind-mismatch' ? '回答類型與預期不符' : '缺少預期引用文件';
  }

  protected eventActionLabel(action: AssistantIssueEventView['action']): string {
    switch (action) {
      case 'created': return '建立事項';
      case 'assigned': return '變更負責人';
      case 'status-changed': return '更新狀態';
      case 'commented': return '新增處理紀錄';
      case 'due-date-changed': return '變更到期日';
      case 'case-opened': return '另開案件（非助理問題）';
    }
  }

  protected isUnverifiedHandoff(issue: AssistantIssueView): boolean {
    return issue.source === 'handoff' && 'handoffUnverified' in issue && issue.handoffUnverified === true;
  }

  protected save(): void {
    const id = this.selectedId();
    if (!id || this.saving()) return;
    const request: UpdateAssistantIssueRequest = {};
    if (this.statusChoice()) request.status = this.statusChoice();
    if (this.assigneeChoice() === '__unassign__') request.unassign = true;
    else if (this.assigneeChoice()) request.assigneeAccountId = this.assigneeChoice();
    if (this.clearDue()) request.clearDueAt = true;
    else if (this.dueChoice()) request.dueAt = new Date(this.dueChoice()).toISOString();
    if (this.note().trim()) request.note = this.note().trim();
    if (Object.keys(request).length === 0) {
      this.actionMessage.set('請先選擇要變更的內容或填寫處理紀錄。');
      return;
    }
    this.saving.set(true);
    this.actionMessage.set('');
    this.issues.update(id, request).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: (result) => {
        if (result.status === 'ready') {
          this.actionMessage.set('處理事項已更新。');
          this.resetForm(false);
          this.listResource.reload();
          this.detailResource.reload();
        } else {
          this.actionMessage.set(result.message);
          if (result.reason === 'issue-changed') this.detailResource.reload();
        }
      },
      error: () => this.actionMessage.set('目前無法更新處理事項，請稍後再試。'),
    });
  }

  private restoreFocus(): void {
    queueMicrotask(() => {
      const target = document.querySelector<HTMLElement>('[data-issue-open-case]') ?? document.querySelector<HTMLElement>('#issue-detail-title');
      target?.focus();
    });
  }

  private resetForm(clearMessage = true): void {
    if (clearMessage) {
      this.caseNotice.set('');
      this.noticeCaseId.set(null);
    }
    this.statusChoice.set('');
    this.assigneeChoice.set('');
    this.dueChoice.set('');
    this.clearDue.set(false);
    this.note.set('');
    if (clearMessage) this.actionMessage.set('');
  }
}
