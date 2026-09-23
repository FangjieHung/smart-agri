import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  viewChild,
  type ElementRef,
} from '@angular/core';
import type { AssistantTone } from '../../../../core/domain/assistant-draft.model';
import type { AssistantAudience } from '../../../../core/domain/assistant.model';

/** 名稱、用途、語氣、使用對象與角色說明；建立精靈與建立後的概覽頁籤共用。 */
export interface AssistantProfileValue {
  readonly name: string;
  readonly purpose: string;
  readonly tone: AssistantTone;
  readonly roleInstructions: string;
  /** 建立精靈允許「還沒選」；建立後的助理一定有值。 */
  readonly audience: AssistantAudience | null;
}

export type AssistantProfileChange = Partial<AssistantProfileValue>;

export interface AssistantProfileErrors {
  readonly name?: string | null;
  readonly purpose?: string | null;
  readonly audience?: string | null;
}

interface AudienceFlags {
  readonly internal: boolean;
  readonly external: boolean;
}

const TONES: readonly { readonly value: AssistantTone; readonly label: string }[] = [
  { value: 'friendly', label: '親切自然' },
  { value: 'professional', label: '專業正式' },
  { value: 'concise', label: '簡短直接' },
];

export function toAudience(flags: AudienceFlags): AssistantAudience | null {
  if (flags.internal && flags.external) return 'members-and-external-customers';
  if (flags.internal) return 'account-members';
  if (flags.external) return 'authorized-external-customers';
  return null;
}

export function toAudienceFlags(audience: AssistantAudience | null): AudienceFlags {
  return {
    internal:
      audience === 'account-members' || audience === 'members-and-external-customers',
    external:
      audience === 'authorized-external-customers' ||
      audience === 'members-and-external-customers',
  };
}

@Component({
  selector: 'app-assistant-profile-form',
  templateUrl: './assistant-profile-form.component.html',
  styleUrl: '../assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantProfileFormComponent {
  readonly value = input.required<AssistantProfileValue>();
  readonly errors = input<AssistantProfileErrors>({});
  /**
   * 使用對象在建立後仍可清成「都不選」嗎？建立精靈可以（尚未送出），
   * 已存在的助理不行——沒有使用對象的助理沒有人能開啟。
   */
  readonly audienceRequired = input(false);
  readonly changed = output<AssistantProfileChange>();
  /** 被擋下的變更；呼叫端負責把訊息顯示在 live region 裡。 */
  readonly refused = output<string>();

  protected readonly tones = TONES;
  protected readonly flags = computed(() => toAudienceFlags(this.value().audience));

  // 勾選狀態直接讀兩個 checkbox：input signal 要等下一輪變更偵測才更新，
  // 連按兩個勾選框時會用到上一輪的舊值。
  private readonly internalBox =
    viewChild.required<ElementRef<HTMLInputElement>>('internalBox');
  private readonly externalBox =
    viewChild.required<ElementRef<HTMLInputElement>>('externalBox');

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected emitTone(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.changed.emit({
      tone: TONES.find((tone) => tone.value === value)?.value ?? 'friendly',
    });
  }

  protected toggleAudience(event: Event): void {
    const checkbox = event.target as HTMLInputElement;
    const audience = toAudience({
      internal: this.internalBox().nativeElement.checked,
      external: this.externalBox().nativeElement.checked,
    });
    if (audience === null && this.audienceRequired()) {
      // 沒有使用對象的助理沒有人能開啟，所以把勾選還原並說明原因。
      checkbox.checked = true;
      this.refused.emit('助理至少要保留一種使用對象，否則沒有人能開啟它。');
      return;
    }

    this.changed.emit({ audience });
  }
}
