# ADR｜後端技術棧：.NET + Microsoft Agent Framework + Microsoft.Extensions.AI／VectorData

**狀態：** 已確認（2026-09-25）

## 決策

- 後端全部使用 .NET（ASP.NET Core + EF Core），不另設 Python 服務。
- LLM 呼叫一律經過 `Microsoft.Extensions.AI` 的 `IChatClient`／`IEmbeddingGenerator`；向量檢索一律經過 `Microsoft.Extensions.VectorData` 的抽象。這兩層是穩定邊界，業務程式碼不直接依賴任何供應商 SDK。
- Microsoft Agent Framework 只用於編排層（助理執行、工具呼叫、工作流程、對外協定 AG-UI／MCP），包在自己的應用服務後面；套件版本鎖定，升級前逐版閱讀 changelog。
- 核心只使用正式版套件。仍為 preview 的元件（例如官方 pgvector、SQL Server 向量 connector）不直接進核心，改以自行實作的 VectorData 介面或其他正式版套件替代，待官方轉為正式版再替換。
- 所有第三方依賴限用寬鬆授權（MIT、Apache-2.0、BSD 等），且維護團隊不得為中國團隊。

## 理由

需求是可長期維護、授權允許商用與客製、非中國團隊。截至 2026-09-25：Agent Framework 為 MIT、2026-04 發布 1.0 正式版；Semantic Kernel 與 AutoGen 已進入維護模式，官方指引新專案改用 Agent Framework；`Microsoft.Extensions.AI`（10.10.0）與 `VectorData.Abstractions`（9.7.0+）皆為正式版。前端已是 Angular／TypeScript，後端單一 .NET 可把長期維運的語言數量壓到兩種。

Agent Framework 發布後仍約每週一個小版本，且 .NET 端尚無自動化 breaking-change 檢查，因此只把它放在可替換的編排層。

## 考慮過的選項

- **Semantic Kernel 單獨使用**：已進入維護模式，不再加新功能。
- **Python（LangGraph／LlamaIndex／Pydantic AI）放在 .NET gateway 後面**：授權與團隊皆符合，但多一套執行環境與部署面，違背長期維護目標；除非文件解析等需求非 Python 生態不可，否則不採用。
- **Dify**：修改版 Apache-2.0，未經授權不得用於多租戶；公司註冊於美國但團隊主要在中國。
- **RAGFlow、FastGPT**：中國團隊。
- **Kernel Memory**：已封存的研究專案。

## 影響

- 每次升級 Agent Framework 需有整合測試覆蓋編排層。
- 自行實作的向量存取需能以設定切換成官方 connector，不得讓業務程式碼知道差異。
