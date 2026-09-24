# ADR｜客戶自行部署以 Docker Compose 交付

**狀態：** 已確認（2026-09-25）

## 決策

第一版以 Docker 映像檔加一份 docker compose 設定交付：後端、PostgreSQL（含 pgvector），模型服務（vLLM）為可選。Kubernetes Helm chart 等有需求再補。

## 理由

中小企業與農會的 IT 能力有限，docker compose 一個指令即可啟動；Kubernetes 對多數目標客戶過重。

## 考慮過的選項

- **Helm chart**：適合已有 Kubernetes 的大型客戶，暫緩。
- **直接安裝在主機上**：各作業系統環境差異大，安裝與升級難以標準化。
