import { Injectable, signal } from '@angular/core';
import type { AccountId } from '../domain/account.model';
import type { AssistantId } from '../domain/assistant.model';
import { DEMO_SECURITY_NOTICE } from '../repositories/demo-repository';

export type DemoAssistantTab =
  'overview' | 'data-sources' | 'test' | 'publishing';

export interface DemoViewState {
  readonly assistantId: AssistantId | null;
  readonly assistantTab: DemoAssistantTab | null;
  readonly returnPath: string | null;
}

const EMPTY_VIEW_STATE: DemoViewState = Object.freeze({
  assistantId: null,
  assistantTab: null,
  returnPath: null,
});

@Injectable({ providedIn: 'root' })
export class DemoSessionService {
  readonly securityNotice = DEMO_SECURITY_NOTICE;

  private readonly activeAccountIdState = signal<AccountId | null>(null);
  private readonly viewStateValue = signal<DemoViewState>(EMPTY_VIEW_STATE);

  readonly activeAccountId = this.activeAccountIdState.asReadonly();
  readonly viewState = this.viewStateValue.asReadonly();

  switchAccount(accountId: AccountId): void {
    this.activeAccountIdState.set(accountId);
    this.viewStateValue.set(EMPTY_VIEW_STATE);
  }

  updateViewState(viewState: Partial<DemoViewState>): void {
    this.viewStateValue.update((current) =>
      Object.freeze({ ...current, ...viewState }),
    );
  }

  clearSession(): void {
    this.activeAccountIdState.set(null);
    this.viewStateValue.set(EMPTY_VIEW_STATE);
  }
}
