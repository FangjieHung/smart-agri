# ADR｜測試策略與禁用套件

**狀態：** 已確認（2026-09-25）

## 決策

**測試**
- 單元與整合測試使用 xUnit（Apache-2.0）；斷言使用 Shouldly 或 AwesomeAssertions。
- 資料庫相關測試以 Testcontainers（MIT）啟動真正的 PostgreSQL，不使用記憶體假資料庫。
- 一般測試以假的模型用戶端（實作 `IChatClient`）回傳固定答案，確保結果穩定。
- 另備評測題庫，定期以真實模型執行，檢查回答品質與「查無結果」判斷是否退步。

**禁用套件**（已改為商用授權，不得引入）
- MediatR
- AutoMapper
- FluentAssertions（v8 以後）
- Hangfire（LGPL／付費，見背景工作 ADR）

## 理由

這幾個套件在 .NET 專案中極常見，容易被順手加入；改為商用授權後違反「寬鬆授權、可閉源散布」的原則（見後端技術棧 ADR）。記憶體假資料庫無法驗證 pgvector 與交易行為，因此資料庫測試一律用真實 PostgreSQL。

## 影響

- 新增任何第三方套件前需確認授權與維護團隊，並可在 CI 加入授權檢查。
