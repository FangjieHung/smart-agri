import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import type { AssistantIssueScope, AssistantIssueStatus, AssistantIssueView, UpdateAssistantIssueRequest } from '../../core/domain/assistant-issue.model';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';

@Component({
  selector: 'app-issues-page',
  imports: [DatePipe, RouterLink, PageHeaderComponent, StatePanelComponent],
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

  protected statusLabel(status: AssistantIssueStatus): string {
    return status === 'open' ? '待處理' : status === 'in-progress' ? '處理中' : '已解決';
  }

  protected sourceLabel(source: AssistantIssueView['source']): string {
    return source === 'handoff' ? '成員轉交' : '測試未通過';
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

  private resetForm(clearMessage = true): void {
    this.statusChoice.set('');
    this.assigneeChoice.set('');
    this.dueChoice.set('');
    this.clearDue.set(false);
    this.note.set('');
    if (clearMessage) this.actionMessage.set('');
  }
}
