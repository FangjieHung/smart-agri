import { CASE_ACTIONS, OPEN_CASE_STATUSES, type CaseAction, type CaseStatus } from './case.model';

/** 呼叫者與案件的關係：建立者、目前的案件負責人、目前承辦組的成員、管理者（可同時成立）。 */
export interface CaseActor {
  readonly isCreator: boolean;
  readonly isOwner: boolean;
  readonly isGroupMember: boolean;
  readonly isManager: boolean;
}

/**
 * `allowed`：可以做；`wrong-status`：這個狀態沒有人能做（`409 case-changed`）；
 * `not-yours`：這個狀態可以做，但不是你（`403 case-action`）。
 */
export type CaseActionCheck = 'allowed' | 'wrong-status' | 'not-yours';

const STATUSES: Readonly<Record<CaseAction, readonly CaseStatus[]>> = {
  accept: ['pending'],
  'request-info': ['in-progress'],
  resume: ['awaiting-info'],
  complete: ['in-progress', 'awaiting-info'],
  cancel: OPEN_CASE_STATUSES,
  transfer: OPEN_CASE_STATUSES,
  'set-due': OPEN_CASE_STATUSES,
  comment: OPEN_CASE_STATUSES,
};

/**
 * 動作表（M7 計畫第 3 節 D、決定 I、J；issue #249）：與後端 `CaseActionRules` 相同，只給 Demo 模式的
 * mock 使用；API 模式由後端的 `allowedActions` 決定畫面顯示哪些動作。
 */
export function checkCaseAction(action: CaseAction, status: CaseStatus, actor: CaseActor): CaseActionCheck {
  if (!STATUSES[action].includes(status)) return 'wrong-status';
  const accepted = status !== 'pending';
  const allowed: Readonly<Record<CaseAction, boolean>> = {
    accept: actor.isGroupMember,
    'request-info': actor.isOwner,
    resume: actor.isOwner,
    complete: actor.isOwner,
    'set-due': actor.isOwner,
    cancel: actor.isManager || (accepted ? actor.isOwner : actor.isCreator),
    transfer: actor.isManager || (accepted && actor.isOwner),
    comment: actor.isCreator || actor.isOwner,
  };
  return allowed[action] ? 'allowed' : 'not-yours';
}

/** 現在能做的動作（已結案的案件是空的，只剩「另開新案」）。 */
export function allowedCaseActions(status: CaseStatus, actor: CaseActor): CaseAction[] {
  return CASE_ACTIONS.filter((action) => checkCaseAction(action, status, actor) === 'allowed');
}

/** 取消要填原因，除了受理前的建立者。 */
export function cancelReasonRequired(status: CaseStatus, actor: CaseActor): boolean {
  return !(status === 'pending' && actor.isCreator);
}

/** 建立者在待補件時補充，自動回到處理中；案件負責人補充不改狀態。 */
export function commentResumes(status: CaseStatus, actor: CaseActor): boolean {
  return status === 'awaiting-info' && actor.isCreator && !actor.isOwner;
}
