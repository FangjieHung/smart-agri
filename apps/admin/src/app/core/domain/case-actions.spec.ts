import { describe, expect, it } from 'vitest';
import { allowedCaseActions, cancelReasonRequired, checkCaseAction, commentResumes, type CaseActor } from './case-actions';
import { CASE_ACTIONS, type CaseAction, type CaseStatus } from './case.model';

const creator: CaseActor = { isCreator: true, isOwner: false, isGroupMember: false, isManager: false };
const owner: CaseActor = { isCreator: false, isOwner: true, isGroupMember: true, isManager: false };
const member: CaseActor = { isCreator: false, isOwner: false, isGroupMember: true, isManager: false };
const manager: CaseActor = { isCreator: false, isOwner: false, isGroupMember: false, isManager: true };

/** 與後端 `CaseActionRulesTests` 相同的動作表（issue #249）；只給 Demo 模式的 mock 用。 */
describe('case action table', () => {
  it.each<[CaseAction, CaseStatus, CaseActor, string]>([
    ['accept', 'pending', member, 'allowed'],
    ['accept', 'pending', creator, 'not-yours'],
    ['accept', 'in-progress', member, 'wrong-status'],
    ['request-info', 'in-progress', owner, 'allowed'],
    ['request-info', 'in-progress', member, 'not-yours'],
    ['resume', 'awaiting-info', owner, 'allowed'],
    ['resume', 'awaiting-info', manager, 'not-yours'],
    ['complete', 'awaiting-info', owner, 'allowed'],
    ['complete', 'in-progress', manager, 'not-yours'],
    ['complete', 'pending', member, 'wrong-status'],
    ['cancel', 'pending', creator, 'allowed'],
    ['cancel', 'pending', member, 'not-yours'],
    ['cancel', 'in-progress', creator, 'not-yours'],
    ['cancel', 'in-progress', manager, 'allowed'],
    ['transfer', 'pending', manager, 'allowed'],
    ['transfer', 'pending', member, 'not-yours'],
    ['transfer', 'in-progress', owner, 'allowed'],
    ['set-due', 'in-progress', owner, 'allowed'],
    ['set-due', 'in-progress', manager, 'not-yours'],
    ['comment', 'pending', creator, 'allowed'],
    ['comment', 'in-progress', member, 'not-yours'],
  ])('%s on %s', (action, status, actor, expected) => {
    expect(checkCaseAction(action, status, actor)).toBe(expected);
  });

  it('admits nothing on a closed case and lists what each person may do now', () => {
    const everyone: CaseActor = { isCreator: true, isOwner: true, isGroupMember: true, isManager: true };
    for (const status of ['completed', 'cancelled'] as const) {
      expect(CASE_ACTIONS.every((action) => checkCaseAction(action, status, everyone) === 'wrong-status')).toBe(true);
      expect(allowedCaseActions(status, everyone)).toEqual([]);
    }
    expect(allowedCaseActions('pending', member)).toEqual(['accept']);
    expect(allowedCaseActions('pending', creator)).toEqual(['cancel', 'comment']);
    expect(allowedCaseActions('in-progress', owner)).toEqual(['request-info', 'complete', 'cancel', 'transfer', 'set-due', 'comment']);
  });

  it('lets only the creator cancel without a reason before acceptance, and only the creator\'s answer resumes', () => {
    expect(cancelReasonRequired('pending', creator)).toBe(false);
    expect(cancelReasonRequired('pending', manager)).toBe(true);
    expect(cancelReasonRequired('in-progress', owner)).toBe(true);
    expect(commentResumes('awaiting-info', creator)).toBe(true);
    expect(commentResumes('awaiting-info', owner)).toBe(false);
    expect(commentResumes('in-progress', creator)).toBe(false);
  });
});
