import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import {
  ASSISTANT_WIZARD_STEPS,
  type AssistantWizardStep,
} from '../../../core/domain/assistant-draft.model';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { AssistantDraftStore } from './assistant-draft.store';
import { PurposeStepComponent } from './steps/purpose-step/purpose-step.component';
import { RulesStepComponent } from './steps/rules-step/rules-step.component';
import { SourcesStepComponent } from './steps/sources-step/sources-step.component';
import { TestStepComponent } from './steps/test-step/test-step.component';

interface WizardStepMeta {
  readonly id: AssistantWizardStep;
  readonly label: string;
  readonly title: string;
  readonly description: string;
}

const STEP_META: readonly WizardStepMeta[] = [
  { id: 'purpose', label: '用途', title: '選擇用途', description: '先說明助理要幫忙完成什麼工作，以及誰會使用它。' },
  { id: 'sources', label: '資料來源', title: '連接資料來源', description: '在同一份清單中加入知識庫與資料庫，可以自由混合。' },
  { id: 'rules', label: '回答規則', title: '設定回答與記錄規則', description: '用白話選項決定助理怎麼回答、記錄哪些資訊。' },
  { id: 'test', label: '試問確認', title: '試問確認', description: '用範例問題確認回答方式，沒問題就可以建立助理。' },
];

function toStep(value: string | null): AssistantWizardStep | null {
  return ASSISTANT_WIZARD_STEPS.find((step) => step === value) ?? null;
}

@Component({
  selector: 'app-assistant-wizard-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    PurposeStepComponent,
    SourcesStepComponent,
    RulesStepComponent,
    TestStepComponent,
  ],
  providers: [AssistantDraftStore],
  templateUrl: './assistant-wizard-page.component.html',
  styleUrl: './assistant-wizard-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantWizardPageComponent {
  protected readonly store = inject(AssistantDraftStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly steps = STEP_META;
  protected readonly draftId = this.route.snapshot.paramMap.get('draftId');
  private readonly requestedStep = toSignal(
    this.route.paramMap.pipe(map((params) => toStep(params.get('step')))),
    { initialValue: toStep(this.route.snapshot.paramMap.get('step')) },
  );
  protected readonly currentStep = computed<AssistantWizardStep>(
    () => this.requestedStep() ?? 'purpose',
  );
  protected readonly currentMeta = computed(
    () => STEP_META.find((step) => step.id === this.currentStep()) ?? STEP_META[0],
  );
  protected readonly stepNumber = computed(
    () => ASSISTANT_WIZARD_STEPS.indexOf(this.currentStep()) + 1,
  );

  constructor() {
    this.route.paramMap
      .pipe(
        map((params) => toStep(params.get('step'))),
        takeUntilDestroyed(),
      )
      .subscribe((step) => this.syncStep(step));
  }

  protected isComplete(step: AssistantWizardStep): boolean {
    return (
      ASSISTANT_WIZARD_STEPS.indexOf(step) <
      ASSISTANT_WIZARD_STEPS.indexOf(this.store.firstIncompleteStep())
    );
  }

  protected next(): void {
    const step = this.currentStep();
    if (!this.store.tryAdvance(step)) {
      this.focusFirstError();
      return;
    }

    const next = ASSISTANT_WIZARD_STEPS[ASSISTANT_WIZARD_STEPS.indexOf(step) + 1];
    if (next !== undefined) void this.goTo(next);
  }

  protected back(): void {
    const previous = ASSISTANT_WIZARD_STEPS[ASSISTANT_WIZARD_STEPS.indexOf(this.currentStep()) - 1];
    if (previous !== undefined) void this.goTo(previous);
  }

  protected create(): void {
    const assistantId = this.store.create();
    if (assistantId === null) {
      this.focusFirstError();
      return;
    }

    void this.router.navigate(['/app/assistants', assistantId, 'overview'], {
      state: { assistantCreated: true },
    });
  }

  /** 步驟跟著網址走；未完成前面步驟時，直接連到後面會被帶回第一個未完成步驟。 */
  private syncStep(step: AssistantWizardStep | null): void {
    if (!this.store.canManage()) return;

    if (step === null || !this.store.canVisit(step)) {
      const fallback = step === null ? this.store.draft().currentStep : this.store.firstIncompleteStep();
      void this.goTo(this.store.canVisit(fallback) ? fallback : this.store.firstIncompleteStep(), true);
      return;
    }

    this.store.setCurrentStep(step);
  }

  private goTo(step: AssistantWizardStep, replaceUrl = false): Promise<boolean> {
    return this.router.navigate(this.draftId
      ? ['/app/assistants/drafts', this.draftId, step]
      : ['/app/assistants/new', step], { replaceUrl });
  }

  private focusFirstError(): void {
    afterNextRender(
      () => {
        const target = this.host.nativeElement.querySelector<HTMLElement>(
          '[aria-invalid="true"], .error[role="alert"]',
        );
        if (target === null) return;
        if (!target.matches('input, textarea, select')) target.setAttribute('tabindex', '-1');
        target.focus();
      },
      { injector: this.injector },
    );
  }
}
