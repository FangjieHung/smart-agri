# ADR｜PostgreSQL + pgvector 作為唯一資料庫，向量存取自行實作

**狀態：** 已確認（2026-09-25）

## 決策

- 業務資料與向量都存在同一個 PostgreSQL，向量使用 pgvector 擴充。
- 向量存取實作 `Microsoft.Extensions.VectorData` 的抽象，底層使用正式版的 `Pgvector.EntityFrameworkCore`（MIT），**不使用**官方仍為 preview 的 pgvector connector。官方 connector 轉為正式版後，可在不改業務程式碼的前提下替換。
- 文件段落與其向量在同一個交易內寫入與刪除。

## 理由

客戶自行部署時只需維運一個資料庫、沒有授權費；段落與向量同交易，刪除文件不會留下孤兒向量。以中小企業知識庫的資料量，pgvector 效能足夠。

## 考慮過的選項

- **PostgreSQL + Qdrant**：Qdrant 的官方 connector 較成熟，但多一個服務要部署、備份與監控，且兩邊資料一致性要自己處理。
- **SQL Server 2025**：向量 connector 仍為 preview，且客戶自行部署需購買授權。

## 影響

- 資料量或查詢量成長到 pgvector 不足時，再評估把向量搬到專用向量庫；因為走 VectorData 抽象，搬移限於基礎設施層。
