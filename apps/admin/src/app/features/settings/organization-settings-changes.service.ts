import { Injectable, signal } from '@angular/core';

/** 某個區塊剛存好一項組織設定；`by` 是那個區塊（自己存的不必重新讀取）。 */
export interface OrganizationSettingsSaved {
  readonly by: object;
}

/**
 * 系統設定頁各區塊之間的「剛存好」通知（issue #243），由 `SettingsPageComponent` 提供。
 *
 * 後端的對話模型與保存期限共用同一個 `Organizations.SettingsRevision`：在同一頁先改保存期限、
 * 再換對話模型時，對話模型區塊手上的 revision 已經過時，照送會得到「已在其他分頁或由其他管理者
 * 更新過」的 `409`，但其實是自己剛改的。所以一個區塊存好後在這裡宣告，其他區塊重新讀取，拿到
 * 新的 revision 與最新的內容（不直接沿用別人的 revision：那會蓋掉其他管理者剛做、這個區塊還沒
 * 看到的變更）。單獨使用某個區塊（例如元件測試）時沒有這個服務，區塊照常運作。
 */
@Injectable()
export class OrganizationSettingsChanges {
  private readonly lastSaved = signal<OrganizationSettingsSaved | null>(null);

  readonly saved = this.lastSaved.asReadonly();

  announce(by: object): void {
    this.lastSaved.set({ by });
  }
}
