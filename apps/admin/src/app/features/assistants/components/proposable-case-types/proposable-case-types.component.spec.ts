import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { ProposableCaseTypesComponent } from './proposable-case-types.component';

/** mock 範例：啟用中的「設備故障報修」與停用的「倉儲盤點差異」（`CaseSettingsRepository`）。 */
const ACTIVE_TYPE = 'case-type-equipment-repair';
const INACTIVE_TYPE = 'case-type-stocktake';

@Component({
  imports: [ProposableCaseTypesComponent],
  template: `
    <app-proposable-case-types
      [selected]="selected()"
      [keepsConversations]="keepsConversations()"
      [hasFormTarget]="hasFormTarget()"
      [formTargetLink]="['/app/assistants', 'assistant-1', 'rules']"
    />
  `,
})
class HostComponent {
  readonly selected = signal<readonly string[]>([]);
  readonly keepsConversations = signal(true);
  readonly hasFormTarget = signal(false);
}

type Fixture = { detectChanges(): void; whenStable(): Promise<unknown> };

async function settle(fixture: Fixture): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function render(state: { selected: readonly string[]; keepsConversations?: boolean; hasFormTarget: boolean }) {
  const activeAccountId = signal<AccountId | null>('account-smb-admin');
  TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [provideRouter([]), { provide: DemoSessionService, useValue: { activeAccountId } }],
  });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.componentInstance.selected.set(state.selected);
  fixture.componentInstance.keepsConversations.set(state.keepsConversations ?? true);
  fixture.componentInstance.hasFormTarget.set(state.hasFormTarget);
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function warning(host: HTMLElement): HTMLElement | null {
  return host.querySelector<HTMLElement>('[data-missing-form]');
}

describe('ProposableCaseTypesComponent: missing form warning (issue #346)', () => {
  it('warns, with the consequence and a link to the rules tab, only when the assistant proposes cases and has no form', async () => {
    const { host } = await render({ selected: [ACTIVE_TYPE], hasFormTarget: false });

    // 類型清單已讀完，警示才有判斷的依據。
    expect(host.textContent).toContain('設備故障報修');
    const notice = warning(host);
    expect(notice).not.toBeNull();
    expect(notice?.getAttribute('role')).toBe('status');
    const text = notice?.textContent?.replace(/\s+/g, '') ?? '';
    expect(text).toContain('對話中沒有表單可以填');
    expect(text).toContain('作物病蟲害回報這類應該填表記錄的事，可能會被提議成案件');
    const link = notice?.querySelector<HTMLAnchorElement>('a');
    expect(link?.textContent?.trim()).toContain('到「回答與記錄」指定寫入的資料庫');
    expect(link?.getAttribute('href')).toBe('/app/assistants/assistant-1/rules');
  });

  it.each([
    { name: 'proposes cases and has a form', selected: [ACTIVE_TYPE], hasFormTarget: true },
    { name: 'proposes no cases and has no form', selected: [], hasFormTarget: false },
    { name: 'proposes no cases and has a form', selected: [], hasFormTarget: true },
  ])('stays hidden when the assistant $name', async ({ selected, hasFormTarget }) => {
    const { host } = await render({ selected, hasFormTarget });
    expect(host.textContent).toContain('設備故障報修');
    expect(warning(host)).toBeNull();
  });

  it('stays hidden when the listed types cannot be proposed: only an inactive type, or conversations are not kept', async () => {
    const inactiveOnly = await render({ selected: [INACTIVE_TYPE], hasFormTarget: false });
    expect(inactiveOnly.host.textContent).toContain('已停用的案件類型（不會提議）');
    expect(warning(inactiveOnly.host)).toBeNull();
    TestBed.resetTestingModule();

    const notKept = await render({ selected: [ACTIVE_TYPE], keepsConversations: false, hasFormTarget: false });
    expect(notKept.host.textContent).toContain('設備故障報修');
    expect(warning(notKept.host)).toBeNull();
  });

  it('appears and disappears as the settings change', async () => {
    const { fixture, host } = await render({ selected: [], hasFormTarget: false });
    expect(warning(host)).toBeNull();

    fixture.componentInstance.selected.set([ACTIVE_TYPE]);
    await settle(fixture);
    expect(warning(host)).not.toBeNull();

    fixture.componentInstance.hasFormTarget.set(true);
    await settle(fixture);
    expect(warning(host)).toBeNull();
  });
});
