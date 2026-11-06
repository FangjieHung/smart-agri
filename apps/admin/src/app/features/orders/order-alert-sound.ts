import { DOCUMENT } from '@angular/common';
import { inject, InjectionToken } from '@angular/core';

/** 記住「開啟提示音」的選擇（只是這台裝置的偏好，不是共用的狀態）。 */
export const ORDER_SOUND_STORAGE_KEY = 'smartagri:ordering-sound';

/**
 * 新訂單的提示音（issue #399）：用 Web Audio 產生兩聲短嗶，不需要音檔。瀏覽器只在使用者對頁面有過互動（點擊、
 * 按鍵）之後才允許播放，所以 `unlock()` 要在使用者手勢的處理程式裡呼叫；被擋或瀏覽器不支援時回傳 `false`。
 */
export interface OrderAlertSound {
  /** 在使用者手勢裡呼叫：建立（或恢復）音訊環境。可以播放時回傳 `true`。 */
  unlock(): Promise<boolean>;
  /** 播放一次提示音；還沒解鎖或被擋時什麼都不做並回傳 `false`。 */
  play(): boolean;
  close(): void;
}

type AudioContextConstructor = new () => AudioContext;

/** Safari 舊版只有 `webkitAudioContext`。 */
type AudioWindow = { readonly AudioContext?: AudioContextConstructor; readonly webkitAudioContext?: AudioContextConstructor };

export class WebAudioOrderAlertSound implements OrderAlertSound {
  private context: AudioContext | null = null;

  constructor(private readonly view: Window | null) {}

  async unlock(): Promise<boolean> {
    const audio = this.view as AudioWindow | null;
    const create = audio?.AudioContext ?? audio?.webkitAudioContext;
    if (!create) return false;
    try {
      const context = (this.context ??= new create());
      if (context.state !== 'running') await context.resume();
      return context.state === 'running';
    } catch {
      return false;
    }
  }

  play(): boolean {
    const context = this.context;
    if (!context || context.state !== 'running') return false;
    try {
      const start = context.currentTime + 0.02;
      // 兩聲上揚的短嗶（880 Hz、1320 Hz），每聲 0.16 秒，淡入淡出避免爆音。
      [[880, 0], [1320, 0.22]].forEach(([frequency, offset]) => {
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.type = 'sine';
        oscillator.frequency.value = frequency;
        const at = start + offset;
        gain.gain.setValueAtTime(0.0001, at);
        gain.gain.exponentialRampToValueAtTime(0.3, at + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + 0.16);
        oscillator.connect(gain).connect(context.destination);
        oscillator.start(at);
        oscillator.stop(at + 0.18);
      });
      return true;
    } catch {
      return false;
    }
  }

  close(): void {
    void this.context?.close().catch(() => undefined);
    this.context = null;
  }
}

/** 每個「點餐」頁各自一個（頁面銷毀時關閉）；測試以假物件替換。 */
export const ORDER_ALERT_SOUND = new InjectionToken<() => OrderAlertSound>('ORDER_ALERT_SOUND', {
  providedIn: 'root',
  factory: () => {
    const view = inject(DOCUMENT).defaultView;
    return () => new WebAudioOrderAlertSound(view);
  },
});

/** localStorage 可能被封鎖（無痕模式、隱私設定），讀寫失敗都當成沒有記住。 */
export function rememberedSound(view: Window | null): boolean {
  try {
    return view?.localStorage.getItem(ORDER_SOUND_STORAGE_KEY) === 'on';
  } catch {
    return false;
  }
}

export function rememberSound(view: Window | null, on: boolean): void {
  try {
    if (on) view?.localStorage.setItem(ORDER_SOUND_STORAGE_KEY, 'on');
    else view?.localStorage.removeItem(ORDER_SOUND_STORAGE_KEY);
  } catch {
    // 記不住只影響下次打開時要再按一次。
  }
}
