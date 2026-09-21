# 樣式契約（Style Contract）

> 純文件，**不參與編譯**。列出共用 `ui-` class 契約與可用 token，供開發者查閱。
> 設計依據見 `docs/superpowers/specs/2026-07-15-theme-pack-architecture-design.md` §3。

元件與 template **只能**使用下列 `--mat-sys-*` 與 `--app-*` token；
禁止寫死色碼或直接引用底層色板變數（`--primary-500` 等）——`npm run lint:theme` 會擋。

---

## Class 契約（`ui-*` / `is-*`）

骨架 HTML 使用這組固定 class，每個質地包（`src/styles/texture/*`）負責實作它們。

| Class                           | 說明                                                                              |
| ------------------------------- | --------------------------------------------------------------------------------- |
| `.ui-card`                      | 白底卡片：hairline 邊、近隱形陰影、大圓角                                         |
| `.ui-card--inverse`             | 深色 hero 卡：無邊框無陰影（原 `.v-card-dark`）                                   |
| `.ui-card--clickable`           | 游標提示可點擊                                                                    |
| `.ui-nav-pill`                  | 導覽膠囊；啟用態疊加 `.is-active`（原 `.v-nav-pill + .active`）                   |
| `.ui-text-display`              | 大數字（StatCard 的 hero number，原 `.v-stat-number`）                            |
| `.ui-text-title`                | 頁面大標（原 `.v-page-title`）                                                    |
| `.ui-text-subtitle`             | 卡片與表單的小節標題（字級、字重由質地包提供）                                    |
| `.ui-text-caption`              | 卡片內小節標／quiet label（原 `.v-card-label`）                                   |
| `.ui-chip` + `.ui-chip--{tone}` | 狀態徽章 + tone 變體（tone：`positive`/`info`/`neutral`/`warning`/`danger`）      |
| `.ui-btn`                       | 按鈕（未來擴充：`--sm`/`--md`/`--lg` × `--solid`/`--outline`/`--ghost`/`--text`） |
| `.ui-table`                     | 表格（未來擴充）                                                                  |
| `.ui-field-group`               | 相連欄位群組：中間圓角歸零、接縫補 hairline divider（僅 `appearance="fill"`）     |

暫時狀態一律用 `is-` 前綴：`.is-active`、`.is-disabled`、`.is-loading`、`.is-selected`…

---

## 可用 token

### Material 3 系統 token（`--mat-sys-*`）

顏色值由**配色包**（`color-themes/*`）提供；造型值由**質地包**（`texture/*/_tokens.scss`）提供。

| 類別 | Token                                                                                                            |
| ---- | ---------------------------------------------------------------------------------------------------------------- |
| 表面 | `--mat-sys-background`、`--mat-sys-surface`、`--mat-sys-surface-container-low/high`、`--mat-sys-inverse-surface` |
| 文字 | `--mat-sys-on-surface`、`--mat-sys-on-surface-variant`、`--mat-sys-on-primary`、`--mat-sys-inverse-on-surface`   |
| 主色 | `--mat-sys-primary`                                                                                              |
| 邊線 | `--mat-sys-outline`、`--mat-sys-outline-variant`                                                                 |
| 狀態 | `--mat-sys-error`、`--mat-sys-error-container`、`--mat-sys-on-error-container`                                   |
| 圓角 | `--mat-sys-corner-small` / `-medium` / `-large` / `-extra-large` / `-full`                                       |
| 陰影 | `--mat-sys-level1` / `-level2` / `-level3` / `-level4`                                                           |

### 專案自訂 token（`--app-*`，M3 缺口的必要擴充）

| 用途         | Token                                                      |
| ------------ | ---------------------------------------------------------- |
| 狀態 tone    | `--app-{positive,info,neutral,warning,danger}-{bg,fg,dot}` |
| 圖表／時間軸 | `--app-viz-1` … `--app-viz-4`                              |
| 深色側欄     | `--app-shell-bg`、`--app-shell-on`、`--app-shell-on-muted` |
| 日曆範圍底色 | `--app-calendar-range-fill`                                |

### 字體（質地包提供，中英分軌）

`--font-zh`（中文固定 Noto）、`--font-en`（英文依質地）、`--font-display`、`--font-body`、`--font-mono`

### 間距

不使用 CSS 變數——一律用 Tailwind 間距工具（`p-4`、`gap-3`…）。
