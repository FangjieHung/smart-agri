# ADR｜客戶自行部署以 Docker Compose 交付

**狀態：** 已確認（2026-09-25）

## 決策

第一版以 Docker 映像檔加一份 docker compose 設定交付：後端、PostgreSQL（含 pgvector），模型服務（vLLM）為可選。Kubernetes Helm chart 等有需求再補。

第一個組織與管理者以一次性的 `setup` 指令建立（`docker compose run --rm api setup`）：指令產生只顯示一次的一次性密碼，管理者首次登入須改密碼；資料庫已有任何組織時拒絕執行。我方 SaaS 日後以同一工具的子指令新增組織。

## 理由

中小企業與農會的 IT 能力有限，docker compose 一個指令即可啟動；Kubernetes 對多數目標客戶過重。

## 考慮過的選項

- **Helm chart**：適合已有 Kubernetes 的大型客戶，暫緩。
- **直接安裝在主機上**：各作業系統環境差異大，安裝與升級難以標準化。
- **首次開啟網頁的設定精靈**：伺服器在設定完成前若可從外部連線，任何人都能搶先成為管理者。
- **以環境變數預先提供管理者密碼**：密碼會長期留在客戶機器的設定檔中。
