# ADR｜程式碼與 API 用語跟畫面一致：organization，不用 company

**狀態：** 已確認（2026-09-25）

## 決策

- 正式 API 規格的回覆類型使用 `organization-data`，不用前端 mock 目前的 `company-data`；其他 API 名稱同樣以 organization 命名。
- 前端程式碼中的 `company-*` 識別名稱（例如 `companyAssistants`、`company-assistants-title`）在該功能區由 mock 換成真實 API 時一併改名，不另外單獨重構。
- 交接文件（`docs/handoff/`）描述的是現行 mock 合約，暫時保留 `company-data`；正式 API 以 OpenAPI 產生的規格為準。

## 理由

畫面用語已統一為「組織」（見 `docs/glossary.md`）。後端尚未開始，現在改名幾乎沒有成本；上線後再改就是破壞性的 API 變更。程式碼與畫面同名，可避免日後把「公司」與「組織」誤當成兩個概念。
