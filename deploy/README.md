# 部署、官網嵌入與 LINE 發布指南（營運人員）

這份文件寫給**部署與維運系統的人**：把助理放上客戶官網（M5a「官網嵌入」）或客戶的 LINE 官方帳號（M5b，第 10 節）之前，伺服器端要準備什麼、
`deploy/.env` 要填什麼、哪些東西要備份、出問題時看哪裡。第一次安裝（建立第一個組織與管理者）、
各設定的完整行為與錯誤碼，請看英文的 [`apps/api/README.md`](../apps/api/README.md)；本文只照操作順序串起來，
並在每一節標出該去哪一節看細節。對話模型清單（讓組織選模型）與對話保存期限的部署注意事項在第 11、12 節，備份與還原在第 14 節，
升級到新版本與回滾在第 15 節，轉真人的 Email 通知（SMTP）在第 16 節，店家共用的「統籌助理」LINE 帳號、綁定碼與店家設定在第 17 節。

## 1. 這個部署長什麼樣子

`deploy/docker-compose.yml` 啟動兩個服務：`postgres`（pgvector 資料庫）與 `api`。**一個網址（`PUBLIC_BASE_URL`）
同時提供管理介面、API、LINE 與官網嵌入**，`api` 容器提供：

- 管理介面（`apps/admin`，已打包在映像檔裡）：`/`、`/login`、`/app/...` 等不屬於下列路徑的網址都回管理介面；
- 管理介面用的 API（`/api/*`、`/connect/*`、`/.well-known/*`）；
- 訪客用的對話視窗（`/use/{助理 id}`、`/widget/*`）與載入器（`/embed.js`），都不需要登入；
- 訪客 API（`/api/v1/public/*`）與 LINE 的 webhook（`/api/v1/line/webhook/{助理 id}`）；
- 健康檢查（`/health/live`、`/health/ready`）。compose 用 `/health/live` 做 api 容器的健康檢查，`docker compose ps` 會顯示
  `(healthy)`（第 15.1 節）。

容器只講**純 HTTP**（容器內 8080，對外映射到 `API_PORT`，預設 8080），**不處理 TLS**。對外一定要有一層
反向代理（nginx、雲端負載平衡器、CDN）負責 HTTPS。客戶官網是 HTTPS，瀏覽器不會在 HTTPS 頁面裡載入 HTTP 的
`<script>` 或 iframe，所以**沒有 HTTPS 的公開網址就不能做官網嵌入**。

> 管理介面和 API 同一個來源，所以登入（`/connect/*`、`SameSite=Strict` 的登入 cookie）不需要額外設定：
> `ADMIN_SPA_ORIGIN` 留空時，`migrate` 會以 `PUBLIC_BASE_URL` 的來源登錄 `{來源}/auth/callback` 與 `{來源}/login`。
> `PUBLIC_BASE_URL` 不能帶路徑（管理介面掛在網址的根目錄）。
>
> **選項：另外部署管理介面。** 也可以把 `nx build admin --configuration=production-api` 的產出放到別的網址，
> 前面由同一個反向代理把 `/api`、`/connect`、`/.well-known` 轉到 api（管理介面只用相對路徑呼叫 API，必須同源）。
> 這時把該網址填進 `ADMIN_SPA_ORIGIN`（它會取代上面的預設值），並可在 compose 的覆寫檔把 `Admin__RootPath` 設為空字串，
> 讓 api 容器不再提供管理介面（見 `apps/api/README.md`「Serving the admin」）。

## 2. 事前準備

| 項目 | 說明 |
| --- | --- |
| 公開的 HTTPS 網址 | 管理者與訪客的瀏覽器、LINE 的伺服器都連得到的網址，例如 `https://assistant.example.org`（不帶路徑），指向反向代理，再轉到 `api` 容器的 8080。憑證用公開 CA 簽發（Let's Encrypt 等）；自簽憑證的網址，客戶官網的訪客瀏覽器會拒絕載入。 |
| 三個 `.pfx` 憑證 | `deploy/certs/signing.pfx`、`encryption.pfx`、`dataprotection.pfx`。這三個是**API 自己用的金鑰憑證，不是網站的 TLS 憑證**，自簽即可，產生方式見下一節與 `apps/api/README.md`「Sign-in and tokens」。 |
| 對話模型與嵌入模型 | `CHAT_*` 與 `EMBEDDING_*` 都要設好（見 `deploy/.env.example`）；要讓組織選第二個對話模型時再填 `CHAT_MODELS_0_*`（第 11 節）。沒有對話模型時，訪客看到的是無法回覆；建議使用會回傳 token 用量的供應商，否則每月用量上限算不到（見第 6 節）。 |
| 第一個組織與管理者 | 已經跑過 `setup`（`apps/api/README.md`「First install: `setup`」）。 |

### 產生三個 `.pfx`

在 repo 根目錄執行（`openssl` 會要求輸入匯出密碼，這個密碼就是 `.env` 裡對應的 `*_CERTIFICATE_PASSWORD`）：

```sh
mkdir -p deploy/certs
openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes -subj "/CN=smartagri-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout signing.key -out signing.crt
openssl pkcs12 -export -inkey signing.key -in signing.crt -out deploy/certs/signing.pfx

openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes -subj "/CN=smartagri-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout encryption.key -out encryption.crt
openssl pkcs12 -export -inkey encryption.key -in encryption.crt -out deploy/certs/encryption.pfx

openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes -subj "/CN=smartagri-dataprotection" \
  -addext "keyUsage=critical,keyEncipherment" -keyout dataprotection.key -out dataprotection.crt
openssl pkcs12 -export -inkey dataprotection.key -in dataprotection.crt -out deploy/certs/dataprotection.pfx

# api 容器用非 root 使用者（uid 1654）執行，openssl 產生的檔案只有你自己讀得到（600），要放寬：
chmod 644 deploy/certs/*.pfx
```

- **一定要 `chmod 644`**：否則 api 因為讀不到憑證而拒絕啟動。密碼在 `.env`，檔案本身可讀不影響安全。
- `deploy/certs/` 與 `deploy/.env` 都在 `.gitignore` 裡，不要 commit；`.key`／`.crt` 中間檔用完就刪或另外保管。
- `dataprotection.pfx` 與 Data Protection 金鑰環綁在一起，**一旦開始使用就不要換**（見第 4 節）。

## 3. `deploy/.env`

```sh
cp deploy/.env.example deploy/.env
```

官網嵌入相關的變數（其餘如資料庫、模型見檔內註解）：

| 變數 | 填什麼 | 沒填會怎樣 |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | 資料庫密碼（必填） | compose 拒絕啟動 |
| `AUTH_SIGNING_CERTIFICATE_PASSWORD`、`AUTH_ENCRYPTION_CERTIFICATE_PASSWORD` | 兩個 token 憑證的密碼 | 憑證打不開，api 拒絕啟動 |
| `DATA_PROTECTION_CERTIFICATE_PASSWORD` | `deploy/certs/dataprotection.pfx` 的密碼 | api 拒絕啟動（Production） |
| `PUBLIC_BASE_URL` | 這個部署唯一的公開網址，例如 `https://assistant.example.org`（不帶路徑）：管理介面、嵌入碼、LINE webhook 都用它 | api 照常啟動，但沒有嵌入碼、**官網頻道無法發布**（`422 public-base-url`）；沒填 `ADMIN_SPA_ORIGIN` 時也**無法登入管理介面** |
| `ADMIN_SPA_ORIGIN` | 選填。只有管理介面另外部署時才填它的來源，例如 `https://admin.example.org` | 用 `PUBLIC_BASE_URL` 的來源（api 容器提供的管理介面） |
| `TRUSTED_PROXIES` | 反向代理的 IP 或網段，逗號分隔，例如 `172.18.0.0/16,10.0.0.5` | 忽略所有 `X-Forwarded-*`，見第 5 節 |
| `DEFAULT_MONTHLY_TOKEN_LIMIT` | 每個組織每月可用的對話模型 token 數（輸入＋輸出）；空白 = 內建的 2,000,000；`0` = 沒有自己上限的組織一律暫停對外回覆 | 用 2,000,000 |
| `SMTP_HOST`、`SMTP_PORT`、`SMTP_USERNAME`、`SMTP_PASSWORD`、`SMTP_FROM`、`SMTP_FROM_NAME` | 選填。寄「轉真人」Email 與線上訂位通知顧客的 Email（第 17.12 節）用的寄信伺服器，見第 16 節 | 不寄 Email；站內的轉真人通知照常，訂位頁不問 Email、由店家來電 |
| `COORDINATOR_LINE_CHANNEL_SECRET`、`COORDINATOR_LINE_CHANNEL_ACCESS_TOKEN` | 選填。平台共用的「統籌助理」LINE 官方帳號的 Channel secret 與 Channel access token，見第 17 節；**兩個一起填** | 不啟用統籌助理（它的 webhook 回 `404`）；只填一個時 api 拒絕啟動 |
| `COORDINATOR_FACEBOOK_APP_ID`、`COORDINATOR_FACEBOOK_APP_SECRET`、`COORDINATOR_GOOGLE_CLIENT_ID`、`COORDINATOR_GOOGLE_CLIENT_SECRET` | 選填。平台的 Meta 應用程式與 Google OAuth 用戶端，讓店家老闆用授權連結授權 FB 粉專、Google 商家，見第 17.9 節；**每一組兩個一起填** | 那個服務不能產生授權連結（FB、Google 的動作維持提醒）；同一組只填一個時 api 拒絕啟動 |
| `COORDINATOR_FACEBOOK_CONFIG_ID` | 選填。「Facebook 登入（企業版）」的設定（configuration）編號，只能是數字，見第 17.10 節 | 授權畫面直接要求 FB 貼文需要的三個權限；只填這個、沒填上一列的 FB 那一組時 api 拒絕啟動 |

**頻率限制**沒有放進 `.env`：預設值（每個 IP 每分鐘開 10 個工作階段、每個訪客每分鐘 6 題／每小時 60 題、
每個 IP 每分鐘 20 題、每個助理每分鐘 120 題且同時最多 10 個回覆）適用多數網站，完整清單見
`apps/api/README.md`「Website visitors」。要調整時，另寫一個 compose 檔疊在上面（不要改 repo 內的 compose）：

```yaml
# deploy/docker-compose.limits.yml（自己建立，不要 commit）
services:
  api:
    environment:
      PublicChannels__RateLimits__RunsPerIpPerMinute: "40"
      PublicChannels__RateLimits__MaxConcurrentRunsPerAssistant: "20"
```

```sh
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.limits.yml --env-file deploy/.env up -d
```

> 指令中一旦用 `-f` 指定檔案，compose 就不會自動套用 `docker-compose.override.yml`，所以每次（含 `run`、`logs`、
> `down`）都要把兩個 `-f` 帶齊。

**檢索門檻**也沒有放進 `.env`，預設值是為 `text-embedding-3-small` 校正的：`Retrieval__MinScore`（0.406，段落分數達到才算相關）
與 `Retrieval__CandidateMinScore`（0.35，由上線前評估 P5 決定，`docs/evals/2026-10-07-304-final-evaluation.md`）。沒有段落達到 `MinScore`、但有段落達到
`CandidateMinScore` 時，只用組織資料的助理仍會把這些候選段落交給模型，模型判斷不足以回答就照常拒答。要調整時寫在同一個
疊加的 compose 檔的 `environment` 裡；`CandidateMinScore` 必須介於 0 和 `MinScore` 之間，否則 api 拒絕啟動，設成空字串
（`Retrieval__CandidateMinScore: ""`）就停用候選段落。換嵌入模型時兩個都要重新設定（`apps/api/README.md`「Retrieval preview」）。

填完先檢查設定能不能展開（不會啟動任何東西；缺 `POSTGRES_PASSWORD` 會在這裡報錯）：

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env config > /dev/null && echo ok
```

啟動後驗證（`<網址>` 換成你的公開網址；前三個都應該回 `200`，最後一個應該印出 `1`：首頁就是管理介面）：

```sh
curl -s -o /dev/null -w '%{http_code}\n' <網址>/health/ready
curl -s -o /dev/null -w '%{http_code}\n' <網址>/embed.js
curl -s -o /dev/null -w '%{http_code}\n' <網址>/.well-known/openid-configuration
curl -s <網址>/login | grep -c '<app-root'
```

接著用瀏覽器開 `<網址>/`，以 `setup` 建立的管理者登入。`ADMIN_SPA_ORIGIN` 或 `PUBLIC_BASE_URL` 改過之後要重新跑一次
`migrate`（每次啟動容器都會自動跑），登入時才不會出現 redirect URI 不符的錯誤。

## 4. Data Protection 金鑰環：存在哪裡、備份什麼、遺失會怎樣

API 用 ASP.NET Core Data Protection 保護三樣東西：管理者的登入 cookie、OpenIddict 的授權碼與 token、
**訪客憑證**（12 小時），以及之後 LINE 的 Channel Secret／Access Token 等機敏設定（M5b 起）。這些都靠同一組
「金鑰檔」解開，所以金鑰檔必須放在資料庫之外，並且容器重建後還在。

- **位置**：compose 的具名 volume `smartagri-dataprotection-keys`（實際名稱 `smartagri_smartagri-dataprotection-keys`），
  掛在容器的 `/app/keys`。金鑰檔本身用 `deploy/certs/dataprotection.pfx` 加密。
- **備份**：把「資料庫」、「金鑰 volume」、「`dataprotection.pfx`（連同密碼）」**一起備份、盡量取同一個時間點**。
  資料庫裡的機敏設定是密文，只有這組金鑰打得開。`deploy/backup.sh` 一次把這些（加上另外兩個憑證與 `deploy/.env`）
  備份成一個檔案，排程、複製到別處與還原的步驟見**第 14 節**。

- **金鑰每 90 天自動輪替**，舊金鑰留在同一個資料夾、繼續解開舊資料，**不要刪除裡面的任何檔案**。
- **遺失的後果**（金鑰檔或憑證任一不見，資料就無法復原；換一張新憑證也打不開舊金鑰）：
  - 所有已存的機敏設定要重新輸入；
  - 所有人的登入狀態失效（重新登入）；
  - 所有訪客憑證失效（訪客重新開啟對話視窗即可，視窗會自動建立新工作階段）；
  - 知識庫、助理、對話統計等其他資料不受影響。
- 用 bind mount 取代具名 volume 時，該資料夾要讓 uid 1654 可寫入，否則 api 會指出路徑並拒絕啟動。

完整說明：`apps/api/README.md`「Data Protection key ring」。

## 5. 反向代理

api 容器只收純 HTTP，**TLS 在反向代理結束**。代理要做兩件事：把請求轉到 `api` 的 8080，並帶上
`X-Forwarded-For`（訪客真正的 IP）、`X-Forwarded-Proto`（`https`）、`Host`。管理介面也由 api 提供，所以整個網址
（`location /`）都轉給 api 即可，不需要為管理介面另寫規則。

nginx 範例（`server_name`、憑證路徑換成自己的；已用 `nginx -t` 檢查語法）：

```nginx
server {
    listen 443 ssl;
    server_name assistant.example.org;

    ssl_certificate     /etc/ssl/assistant/fullchain.pem;
    ssl_certificate_key /etc/ssl/assistant/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        # 回答是 Server-Sent Events（串流），允許長一點的回覆。
        proxy_read_timeout 120s;
    }
}
```

回答串流的回應已帶 `X-Accel-Buffering: no`，nginx 不需要另外關閉 buffering；其他代理若會緩衝串流，
訪客會看到回答「整段一起出現」而不是逐字出現，請關閉該路徑的 response buffering。

### 為什麼一定要設 `TRUSTED_PROXIES`

**預設完全不信任 `X-Forwarded-*`**（任何人都能偽造這些標頭）。後果在代理後面有兩個：

1. **頻率限制把所有訪客當成同一個 IP。** 訪客 API 依 IP 限流；沒設 `TRUSTED_PROXIES` 時，每個請求的來源都是代理本身，
   於是全世界的訪客共用一個「每個 IP 每分鐘 20 題」，很快大家一起收到 `429`。
2. **api 看到的是 `http` 而不是 `https`。** 載入器與嵌入碼的網址、訪客 API 的同源檢查、以及 `/connect/*`
   （Production 強制 HTTPS）都依請求的 scheme 與 host 判斷，TLS 在代理結束時，沒套用 `X-Forwarded-Proto`
   會讓登入與同源檢查出錯。

`TRUSTED_PROXIES` 只有列在裡面的位址送來的請求才會套用轉送標頭；偽造的標頭不會被採信（從右邊往左讀，遇到第一個
不在清單內的位址就停）。**填代理「連到 api 時的來源位址」**：

- 代理與 api 在同一台主機、用 `127.0.0.1:8080` 連：經 Docker 發佈的連接埠進來時，來源通常是 compose 網路的閘道，
  所以填 compose 網路的網段。取得方式（`smartagri_default` 是 `docker compose up` 建立的網路；指令格式可用
  `docker network ls` 看到的任何網路驗證）：

  ```sh
  docker network inspect smartagri_default --format '{{(index .IPAM.Config 0).Subnet}}'
  ```

  輸出例如 `172.18.0.0/16`，填進 `TRUSTED_PROXIES`。
- 代理在別台主機：填那台的 IP。代理前面還有 CDN 時，兩層都列（CDN 的網段＋代理）。
- 填對與否的判斷：Production 下 api 收到 `X-Forwarded-For` 但 `TRUSTED_PROXIES` 沒設，會在 log 記一次警告
  （`docker compose -f deploy/docker-compose.yml logs api | grep TrustedProxies`）；設好之後，用兩個不同網路的
  裝置連線，訪客 API 的 `429` 不應該互相牽連。

完整說明：`apps/api/README.md`「Behind a reverse proxy」。

## 6. 每月 token 上限與 `set-token-limit`

每個組織每月有一筆對話模型的 token 預算（輸入＋輸出，含組織內部的對話、驗收重跑等；不含嵌入模型）。用完時，
**只有對外的官網與 LINE 回覆暫停**（訪客看到「目前暫停服務」），組織內部照常使用；下個月（以 `STATISTICS_TIME_ZONE`
的月份為準）自動恢復，或調高上限後最多 30 秒內恢復。用到 80% 時，admin 的「發布管道」頁與助理的「發布」頁會提示。
沒有設定畫面，由營運人員用指令調整。**供應商沒有回傳用量的呼叫算 0，不會被計入**，所以請使用會回傳用量的供應商。

容器的 entrypoint 是 `dotnet SmartAgri.Api.dll`（會先 `migrate`，再執行你給的子指令），所以用 `run --rm`
起一個一次性容器執行（在 repo 根目錄；`<組織代碼>` 是登入時輸入的那個，例如 `anxin`）：

```sh
# 設為每月 5,000,000 tokens
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  set-token-limit --organization anxin --tokens 5000000

# 改回部署預設值（DEFAULT_MONTHLY_TOKEN_LIMIT）
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  set-token-limit --organization anxin --tokens default

# 查看說明
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api set-token-limit --help
```

結束代碼：`0` 完成、`1` 找不到組織（什麼都沒寫入）、`2` 參數錯誤（`--tokens` 只能是 `0`、正整數或 `default`）。
`--tokens 0` = 本月不允許任何對外回覆（緊急關閉用）。成功時會印出「前一個值 → 新值」，例如「預設值（PublicChannels:DefaultMonthlyTokenLimit） → 5,000,000」。
有用 `docker-compose.limits.yml` 的話，這些指令一樣要帶兩個 `-f`。

完整說明：`apps/api/README.md`「Monthly token limit」。

### 6.1 多家店共用一個部署

同一個部署可以服務多家店（每家店是一個組織，資料互相隔離）。第一家店用 `setup` 建立；**第二家以後**用 `add-organization`
（在 repo 根目錄，一次性容器；有用 `docker-compose.limits.yml` 的話一樣要帶兩個 `-f`）：

1. 新增店家與它的管理者（互動式會詢問；非互動式三個旗標都必填，顯示名稱省略時等於帳號名稱）：
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api add-organization

   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T api add-organization \
     --organization-name "豐收商行" --organization-code harvest \
     --admin-login boss --admin-display-name "李老闆"
   ```
   指令會印出**只顯示一次的一次性密碼**（不寫入檔案、日誌或遙測，遺失後無法再查），請當場交給那家店的管理者；
   他首次登入時要輸入**組織代碼**（多家店時每次登入都要，建立後不可修改），並先設定新密碼。
   資料庫還沒有任何組織時指令會拒絕並提示改用 `setup`；組織代碼已被使用時也會拒絕，且不寫入任何東西。
   結束代碼同 `setup`：`0` 完成、`1` 拒絕或失敗、`2` 參數錯誤。
2. 為這家店設定每月 token 上限（否則套用 `DEFAULT_MONTHLY_TOKEN_LIMIT`）：
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
     set-token-limit --organization harvest --tokens 3000000
   ```
3. 隨時用 `list-organizations` 查看目前有哪些店，以及每家店這個月用了多少 token、離上限多遠（不含任何帳號的個人資料）：
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api list-organizations
   ```
   輸出範例（實際以 Tab 分隔，這裡對齊顯示）：
   ```text
   組織代碼  名稱      建立時間(UTC)        帳號數  本月用量   上限                        使用比例
   harvest   豐收商行  2026-10-08 03:12:45  3       1,234,567  3,000,000                   41.1%
   green     綠野農坊  2026-10-09 07:40:02  1       1,700,000  未設上限（預設 2,000,000）  85.0%（接近上限）
   共 2 個組織。
   本月為 2026-10（Asia/Taipei）；用量為對話模型 token（輸入＋輸出），未設上限的組織套用部署預設值 2,000,000。
   ```
   - 「本月用量」與上限判斷用的是同一套算法：以 `STATISTICS_TIME_ZONE` 時區的當月計算，計入對話模型的輸入＋輸出 token，
     上個月的用量不算在內。
   - 沒用 `set-token-limit` 設過的店顯示「未設上限（預設 N）」：它仍受部署預設值 `DEFAULT_MONTHLY_TOKEN_LIMIT` 限制，
     使用比例也以預設值計算。
   - 使用比例達 80% 標示「接近上限」，達 100% 標示「已達上限」（這時網站與 LINE 的對外回覆會暫停到下個月或調高上限為止）。
4. **隱私權政策**：由你代管多家店時，你和各店的角色不同，請把 `privacy-policy-template.md` 的第 1 節與第 5 節改成
   「店家（資料控制者）／代管業者（處理者）」的寫法，並由各店各自發布；範本已附上可選用的代管者文字。

### 6.2 店家管理者忘記密碼：`reset-password`

不必改資料庫：用 `reset-password` 為**指定組織的指定帳號**產生新的一次性密碼（在 repo 根目錄，一次性容器；有用
`docker-compose.limits.yml` 的話一樣要帶兩個 `-f`）：

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  reset-password --organization harvest --login boss
```

- 新密碼**只在這裡顯示一次**（不寫入檔案、日誌或遙測），請當場交給帳號本人；他登入後必須先設定新密碼。遺失了就再執行一次。
- 原本的密碼立即失效，鎖定（連錯 5 次）與失敗次數一併清除。
- 這個帳號原有的登入工作階段會失效：已登入的瀏覽器無法再取得新的存取權杖（換發用的 refresh token 也一併撤銷）；
  手上已有的存取權杖在到期前（最多 30 分鐘）只能查看自己的帳號資料，其他功能都會被要求先改密碼。
- 只影響這個組織的這個帳號；其他店的同名帳號、同一家店的其他帳號都不受影響。
- 組織代碼或登入名稱不存在時以結束代碼 `1` 離開並說明是哪一個找不到，不做任何變更；參數錯誤為 `2`；完成為 `0`。
  說明：`run --rm api reset-password --help`。

## 7. 助理擁有者的發布流程

只有**助理擁有者**且帶有「管理發布」權限的帳號能發布（admin：助理 →「發布」頁 →「官網嵌入」）。

1. **驗收必須通過。** 助理的驗收狀態（驗收題組頁）現在就是「通過」才能按「發布官網嵌入」；「過期」要等重跑完成。
2. **設定允許網域。** 至少一個，最多 5 個，**只填主機名稱**：小寫、不含 `https://`、路徑、連接埠與萬用字元。
   `www.example.com` 與 `example.com` 是兩個網域，要分別加入。這份清單就是 `frame-ancestors` 的來源：
   不在清單上的網站，瀏覽器不會顯示對話視窗。儲存之後，發布使用的是**已儲存**的設定。
3. **只能用擁有者自己的知識庫。** 助理若連接了別人分享的知識庫，發布會被拒絕並列出那些知識庫，
   到「資料來源」頁解除連接後再發布（對外回答不能使用別人只同意「組織內分享」的資料）。
4. **把聯絡方式寫進「找不到資料時的回覆」。** 訪客端沒有轉人工按鈕；資料裡找不到答案時，訪客看到的就是這段文字
   （助理設定 →「回答與記錄」）。請寫上客服電話、LINE 或 Email。
5. **按「發布官網嵌入」並確認**，複製「嵌入碼」，貼到允許網域的網頁（`</body>` 前）：

   ```html
   <script src="https://assistant.example.org/embed.js" data-assistant="<助理 id>" async></script>
   ```

   嵌入碼的網址來自 `PUBLIC_BASE_URL`；畫面上寫「伺服器尚未設定對外網址」時，請系統管理者補上並重啟 api。
   顯示位置選「左下角」時嵌入碼會多 `data-position="left"`，品牌色不是預設的森林綠時會多 `data-brand="ocean"` 之類的屬性：
   官網上的啟動按鈕只從網頁讀這兩個設定，所以**之後改了位置或品牌色，要重新複製嵌入碼貼到網站上**，按鈕才會跟著變
   （對話視窗裡的顏色、名稱與歡迎語則是即時的，不必重貼）。舊的嵌入碼沒有這些屬性，會維持右下角、森林綠。
6. **看「安裝偵測」。** 網頁第一次載入對話視窗時，該網域的「最後偵測到」時間就會更新。這是被動偵測（伺服器不會去連客戶網站），
   僅供參考，不是安全判斷。
7. **之後的狀態會自動判斷**：驗收「未通過」或「尚未驗收」、連接了別人的知識庫、組織本月用量用完，對外回覆都會自動暫停，
   訪客看到「目前暫停服務」（不揭露原因），擁有者在發布頁看到原因與處理連結；原因消除後自動恢復，不必重新發布。
   擁有者也可以手動「暫停服務」／「恢復服務」或「取消發布」。

## 8. 瀏覽器限制擋不住腳本

**請不要把「允許網域」當成安全防線。** 下列機制都只約束**瀏覽器**：

- 允許網域 → `Content-Security-Policy: frame-ancestors`：只有瀏覽器會照著不在清單上的網站不顯示 iframe；
- 訪客 API 的同源檢查（有 `Origin` 標頭、且不是 API 自己的來源就 `403`），以及不開放 CORS。

任何人都可以**不用瀏覽器**，用腳本（`curl`、Python）直接呼叫訪客 API：腳本不送 `Origin`、也不理會 CSP。
真正的保護是**伺服端的限制，它們對腳本與瀏覽器一視同仁**：

1. **頻率限制**（每個 IP、訪客、助理，第 3 節）——前提是 `TRUSTED_PROXIES` 設對（第 5 節），否則看到的 IP 是錯的；
2. **每月 token 上限**（第 6 節）——限制模型費用的最後防線，用完對外回覆就暫停；
3. 訪客只能問**知識庫問題**：不保存對話、沒有表單、沒有資料庫查詢、沒有轉人工。

所以：不要把不想公開的內容放進對外助理的知識庫；`DEFAULT_MONTHLY_TOKEN_LIMIT` 與頻率限制要依預算設定，
而不是預設「反正只有允許的網站能用」。

## 9. 常見狀況

| 現象 | 先看 |
| --- | --- |
| 發布時 `422`，訊息提到對外網址 | `PUBLIC_BASE_URL` 沒填；填好後重啟 api。 |
| api 啟動就結束，log 說讀不到憑證 | `deploy/certs/*.pfx` 沒有 `chmod 644`、密碼不對，或缺少 `dataprotection.pfx`。 |
| 訪客大量收到 `429`、「問題太頻繁了」 | `TRUSTED_PROXIES` 沒設或不是代理的來源位址（第 5 節）。 |
| 客戶官網上看不到對話按鈕 | 該網站的網域不在允許清單（注意 `www.`）；或頻道未發布、已暫停。用瀏覽器開發者工具看 console 是否有 `frame-ancestors` 的 CSP 錯誤。 |
| 直接用瀏覽器開 `/use/<id>` 看到「請從官網開啟這個對話視窗」 | 正常：對話視窗只能從允許的網站嵌入；預覽請用 admin 的 `/app/chat/<助理 id>`。 |
| 訪客看到「目前暫停服務」 | 到發布頁看狀態：驗收未通過、連接了別人的知識庫、本月用量用完，或擁有者暫停。 |
| 官網的啟動按鈕顏色或位置沒有跟著發布頁的設定改變 | 網站上貼的還是舊的嵌入碼：到發布頁重新複製嵌入碼貼上（第 7 節第 5 步）。 |
| 客戶的工程師在開發者工具看到紅色的 `Agent execution failed: Error: HTTP 403: …` 或 `HTTP 429: …` | **預期中的訊息，不是故障**：訪客遇到暫停服務（`403`）或問題太頻繁（`429`）時，對話視窗使用的 `@ag-ui/client` 套件會自己印這一行，而且沒有正式的方式關掉；訪客畫面上看到的是「目前暫停服務」或「問題太頻繁了，請稍後再試」。確切內容：`Agent execution failed: Error: HTTP 403: {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"public-assistant","message":"這個對話視窗目前無法使用。"}`，以及 `Agent execution failed: Error: HTTP 429: {"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"reason":"rate-limited","message":"問題太頻繁了，請稍後再試。"}`。瀏覽器另外會為同一個請求記一行網路錯誤（例如 Chrome 的 `POST https://<api>/api/v1/public/assistants/<助理 id>/chat/runs 403 (Forbidden)` 或 `Failed to load resource: the server responded with a status of 403 ()`），這一行網頁無法關掉。只有訪客沒看到上述畫面時才需要追查（先看發布頁的狀態）。說明與原始碼位置見 `apps/widget/README.md`「主控台裡預期中的錯誤訊息」。 |
| `docker compose ps` 的 api 一直是 `Restarting` | api 一啟動就結束：看 `docker compose ... logs --tail 50 api`。最常見的是連不上資料庫（`.env` 的 `POSTGRES_PASSWORD` 改過，但資料庫是用舊密碼建立的）或讀不到憑證（上面那一列）。 |
| `docker compose ps` 的 api 是 `(unhealthy)` | api 還在跑但不回應 `/health/live`：先看 log，再 `docker compose ... restart api`；Docker 不會自己重啟它（第 15.1 節）。 |
| 重建容器後所有人被登出 | 金鑰 volume 沒有掛上或被刪（第 4 節）。 |
| LINE 使用者收到兩則回覆，或一則罐頭回覆加一則助理回答 | Official Account Manager 的「自動回應訊息」沒關（第 10.2 節）。 |
| LINE 的「測試連線」第三項（webhook-test）失敗 | `PUBLIC_BASE_URL` 不是 LINE 連得到的公開 HTTPS 網址，或反向代理沒轉發 `/api/v1/line/webhook/*`；網址變了要重按「測試連線」（第 10.1 節）。 |
| 群組裡 @ 官方帳號沒反應 | 要用 LINE 提及清單選到官方帳號，手打「@名稱」不算提及；也要確認官方帳號允許加入群組（第 10.2、10.5 節）。 |
| LINE 一對一的回答很慢才出現，admin 的「本月補送次數」增加 | 回答超過回覆期限（預設 50 秒）改用 push 補送，會消耗 LINE 訊息額度（第 10.3 節）。 |

### 9.1 冒煙測試：`deploy/smoke-test.sh`

升級前（第 15.2 節：`git pull` 之後、建置正式環境的映像檔之前）可以先在部署主機上確認新版的映像與 compose 檔能用。CI 的 `docker-smoke`
job 在每個 PR 上跑的就是同一支腳本（#341）。在 repo 根目錄執行：

```sh
deploy/smoke-test.sh
```

它會：用 repo 的 Dockerfile 建置正式映像 → 以 Production 啟動 `deploy/docker-compose.yml` → 等 `/health/ready` 回 200、
Docker 回報 api 為 `healthy`（compose 的健康檢查）→
確認 `/` 與 `/app/home` 是管理介面的 HTML、`/embed.js` 是 JavaScript → 跑一次 `setup`（只看結束碼，輸出裡的一次性密碼直接丟掉，
不會印出）→ `down -v` 清掉。全部通過時最後一行是 `smoke-test: PASS: …`，結束碼 `0`；任何一步失敗都會印
`smoke-test: FAIL: <原因>`（api 起不來時附上它最後的 log）並以非 0 結束，容器與 volume 一樣會清掉。

- **不碰正式環境**：不讀 `deploy/.env`、不用 `deploy/certs/`，用自己的 compose 專案名稱（`smartagri-smoke-<時間>-<pid>`）、
  只綁 `127.0.0.1:18341`。資料庫密碼、三個 `.pfx` 與它們的密碼都是當場產生的，放在 `deploy/.smoke-test.*/`（已加入
  `.gitignore`），結束時刪除。模型設成一個沒有人在聽的位址（`OpenAICompatible`，Production 不接受 `Fake`），所以不需要任何金鑰。
- **需要**：Docker（含 `docker compose`）、`curl`、`openssl`；第一次建置要幾分鐘；建好的映像結束時會刪掉，建置快取會留著，下次比較快。
- **可調整的環境變數**：`SMOKE_PORT`（預設 `18341`，被占用時換一個）、`SMOKE_TIMEOUT_SECONDS`（等 `/health/ready` 與 `healthy` 的秒數，預設 180）、
  `SMOKE_KEEP_IMAGE=1`（保留建好的映像 `smartagri-smoke-api:local`）、`SMOKE_IMAGE` 搭配 `SMOKE_SKIP_BUILD=1`（直接用已建好的映像，CI 用這個方式套用層快取）。

## 10. LINE 頻道（M5b）

LINE 頻道讓助理擁有者把通過驗收的助理接上**客戶自己的 LINE 官方帳號**：LINE 使用者在一對一聊天，或在群組裡
@ 官方帳號提問，得到附引用的回答。設定與發布都在 admin：助理 →「發布」頁 →「LINE」，由助理擁有者操作；
伺服器端與 LINE 後台的準備由營運人員與客戶一起做。LINE 頻道沿用官網嵌入的發布閘門（驗收要「通過」、知識庫
只能是擁有者自己的、`PUBLIC_BASE_URL` 要設定）、每月 token 上限（第 6 節）與暫停規則（第 7 節第 7 點）；
各設定的完整行為與錯誤碼見 `apps/api/README.md`「LINE channel」「LINE webhook」「LINE answers」三節。

### 10.1 準備官方帳號與 Messaging API channel

1. **建立官方帳號並啟用 Messaging API**：在 LINE Official Account Manager 建立官方帳號，於「設定 → Messaging API」
   啟用 Messaging API（選擇或建立 Provider）。這會在 LINE Developers Console 建出一個 Messaging API channel。
2. **在 LINE Developers Console 取得四項資料**：
   - **Channel ID**：「Basic settings」分頁，10 位數字；
   - **Channel secret**：同一分頁，32 位十六進位；
   - **Bot basic ID**：「Messaging API」分頁，`@` 開頭，就是 admin 的「官方帳號 ID」；
   - **Channel access token**：「Messaging API」分頁發行**長效型**（long-lived）token，至少 40 字元、
     不含空白。
3. **開啟 Use webhook**：同在「Messaging API」分頁，把「Use webhook」打開。
4. **在 admin 填入並按「測試連線」**：官方帳號 ID、Channel ID、Channel secret、Channel access token 由**擁有者自己在
   admin 輸入**（不經過營運人員，也不要貼進聊天或工單）。Secret 與 Token 以 Data Protection 加密保存（第 4 節），
   畫面只顯示「已設定・末四碼」，之後無法再讀出原文；要更換時重新輸入即可。
5. **Webhook 網址不用自己貼**：「測試連線」會依序做三項檢查——驗證 Token 且 Bot basic ID 與填寫的相符、把 LINE 的
   Webhook 網址設成 `{PUBLIC_BASE_URL}/api/v1/line/webhook/{助理 id}`、請 LINE 送一個有簽章的測試事件（同時驗證 Secret
   與網址連得到）。三項都通過、助理驗收也「通過」，才能按「啟用」。
6. **`PUBLIC_BASE_URL` 必須是公開的 HTTPS 網址**（第 2 節、第 5 節）：LINE 的伺服器要連得到它；沒設定時 admin 的
   LINE 頁看不到 Webhook 網址、也無法測試連線。**這個網址一旦改變（換網域、換通道網址），要重新按一次「測試連線」**，
   才會把新網址設定到 LINE。
7. 更換官方帳號 ID、Channel ID、Secret 或 Token 任何一項，連線測試結果會清空、已啟用的頻道退回草稿，要重新測試與啟用；
   只改歡迎訊息（≤ 120 字）則不影響。

> Webhook 的簽章以 Channel secret 驗證，簽章不符一律回 `401`、不揭露原因。LINE 不公布來源 IP，所以不要在防火牆用 IP 白名單
> 擋 `/api/v1/line/webhook/*`；它也不需要 `Origin`，不受訪客 API 的同源檢查。

### 10.2 Official Account Manager 的設定

在 LINE Official Account Manager 的「回應設定」與帳號設定裡：

| 設定 | 要怎麼設 | 為什麼 |
| --- | --- | --- |
| **Webhook** | 開啟 | 不開，LINE 不會把訊息送給系統。 |
| **自動回應訊息** | **關閉** | 開著時，LINE 自己的自動回應可能與助理的回答同時送出，使用者收到兩則。我們的實機驗收（`docs/evals/2026-10-07-235-line-acceptance.md`）在自動回應開著時，擁有者沒有看到多餘的自動回應；但是否重複取決於自動回應的內容與關鍵字設定，所以**仍然要關閉**，避免重複回覆。 |
| **加入群組或多人聊天室** | 要在群組使用就**允許** | 官方帳號不允許加入群組時，無法被邀進群組。 |
| **聊天**（回應設定） | 擁有者想自己看客戶訊息時才開 | 開了之後，擁有者可在 Official Account Manager 的聊天介面看到客戶訊息並手動回覆。助理回覆「找不到資料時的回覆」時，系統會在管理介面的「轉真人」通知助理的擁有者（有設定 SMTP 時也寄 Email，第 16 節），但**不會轉接真人、也不保存客戶的訊息**：「找不到資料時的回覆」若寫「店長會在這裡回覆您」，擁有者仍要到聊天介面回覆。 |

這些開關目前無法由系統檢查或設定，只能由客戶在 LINE 後台確認。

### 10.3 額度、補送與回覆期限

- **reply 免費**：系統先用事件的 reply token 回覆，不消耗 LINE 訊息額度。reply token 約一分鐘有效且只能用一次。
- **回覆期限**：從收到 webhook 起算，`PublicChannels:Line:ReplyDeadlineSeconds`（預設 **50** 秒，可設 1–60，超出範圍 api 拒絕啟動）
  內送出 reply。
- **逾時改用 push 補送（只限一對一）**：回答超過期限，或 LINE 拒絕 reply token 時，一對一聊天的回答改用 push 補送，
  並累計在頻道的「本月補送次數」（admin 的 LINE 頁可看）。push 依「收到的人數」計入 LINE 官方帳號的每月訊息額度
  （一對一 1 則）；額度用完時 LINE 回 `429`、不送出，系統記錄後放棄這一則，不重試。額度與方案以 LINE 當時的規定為準，
  請客戶在 Official Account Manager 確認方案的每月則數。
- **群組與聊天室逾時不補送**（決定 A）：push 到群組要依群組成員數計算額度（30 人的群組補送一次 = 30 則），所以逾時的群組回答直接丟棄，
  log 會記「missed its reply deadline in a group or room … and is not pushed」。群組裡沒收到回答時，使用者可再問一次。
- **用量**：LINE 的問答（`line-answer`）計入每月 token 上限（第 6 節）；超過時 LINE 與官網同時暫停，使用者收到「目前暫停服務」。
- **LINE 頻率限制**：比照第 3 節，不放進 `.env`，預設值通常夠用；要調整時，在同一個 `docker-compose.limits.yml` 加
  `PublicChannels__RateLimits__*`（也可一併設回覆期限）。LINE 的請求都來自 LINE 的伺服器，不能依 IP 分區，改依聊天對象計算：

  | 設定（`PublicChannels__RateLimits__…`） | 預設 | 意義 |
  | --- | --- | --- |
  | `LineQuestionsPerUserPerMinute` | 6 | 每個 LINE 使用者（一對一）每分鐘題數 |
  | `LineQuestionsPerUserPerHour` | 60 | 同上，每小時 |
  | `LineQuestionsPerGroupPerMinute` | 10 | 每個群組／聊天室每分鐘題數（全體成員合計） |
  | `LineQuestionsPerAssistantPerMinute` | 120 | 每個助理每分鐘題數（所有聊天合計） |
  | `LineMaxConcurrentQuestionsPerAssistant` | 10 | 每個助理同時產生的回答數 |
  | `LineWebhooksPerAssistantPerMinute` | 1000 | 每個助理的 Webhook 網址每分鐘接受的請求數，超過回 `429`（擋偽造請求） |
  | `LineMaxConcurrentWebhooksPerAssistant` | 10 | 每個助理同時處理的 Webhook 投遞數 |

  超過時，使用者會收到一次「問題太頻繁了，請稍後再試」，同一個時間窗內不再回覆也不呼叫模型。
  回覆期限是 `PublicChannels__Line__ReplyDeadlineSeconds`：

  ```yaml
  # deploy/docker-compose.limits.yml（自己建立，不要 commit）
  services:
    api:
      environment:
        PublicChannels__RateLimits__LineQuestionsPerUserPerMinute: "10"
        PublicChannels__Line__ReplyDeadlineSeconds: "50"
  ```

### 10.4 單一程序前提

LINE 的下列狀態都存在 **api 程序的記憶體**，沒有放進資料庫：

- 對話前文：每段對話（助理＋使用者／群組／聊天室）最近 20 則、閒置 30 分鐘清除；
- Webhook 事件去重（10 分鐘）與等待處理的事件佇列；
- 頻率限制的計數。

所以：**重啟 api 會清掉所有前文**（使用者的追問會當成新對話，處理中的事件最多少收到一則回答）；**只能跑一個 api 執行個體**，
多個執行個體會各自保存前文與計數，同一位使用者的追問可能落在沒有前文的那一個。

### 10.5 對使用者的行為

- **非文字訊息**：一對一聊天傳貼圖、照片、影片、語音、檔案或位置時，回擁有者在 admin 設定的「收到非文字訊息時的回覆」
  （LINE 設定頁；預設「目前只能回答文字問題。」，最多 500 字，換行與 emoji 原樣送出；#291）。不呼叫模型、不計入用量；群組與多人聊天室不回應。
  修改這段回覆不影響連線測試結果與啟用狀態。既有頻道升級後是預設文字。
- **群組只在被 @ 時回答**：要用 LINE 輸入框彈出的**提及清單**選到官方帳號才算提及；手打「@名稱」只是一般文字，系統不會當成提問
  （驗收時第一次手打就沒有回應，也沒有呼叫模型）。群組成員看得到彼此的提問與回答，前文也是整個群組共用。
- **歡迎訊息**：加好友或被邀進群組時送出（預設「您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。」，最多 120 字，可在 admin 修改）。
- **引用卡片**：助理開啟「顯示引用」時，回答後面會多一則 Flex 卡片（每份被引用的文件一張，最多 5 張）。想關閉，到助理設定 →「回答與記錄」
  關掉「在回答下方顯示引用來源」：LINE 就不再送卡片，回答文字裡的「（來源 1）」標示也一併移除。許多客服場景不需要引用卡片，
  建議上線前與擁有者確認。
- **暫停服務**：頻道暫停、驗收未通過、用量用完時，使用者收到「目前暫停服務」，不呼叫模型。
- **找不到資料**：回擁有者寫的「找不到資料時的回覆」（助理設定 →「回答與記錄」），請寫上聯絡方式（第 7 節第 4 點）。

### 10.6 隱私權政策提醒與系統保存的資料

- LINE 要求官方帳號有**隨時可查看的隱私權政策**。客戶必須準備，並**揭露使用者傳的訊息會送交模型供應商（例如 OpenAI）處理**。
  LINE 的使用者資料政策另規定：除內部使用者 id 外，保存「LINE 使用者資訊」（含訊息）超過 24 小時須告知使用者。
- **範本**：[`privacy-policy-template.md`](privacy-policy-template.md)（繁體中文）依下列系統行為寫成，涵蓋 LINE 官方帳號與官網聊天視窗。
  客戶要**依自己的狀況改寫（搜尋 `〔` 補齊）、自行發布**，它不是法律意見。發布後，把網址填進 LINE 官方帳號管理後台
  （LINE Official Account Manager 或 LINE Developers Console）裡官方帳號的隱私權政策欄位；實際的選單名稱以 LINE 當時的後台為準。
  官網的聊天視窗也要在客戶官網上放同一份政策的連結。
- 系統的做法（M5b 計畫 §2.1、§3 E）：
  - **資料庫不寫任何 LINE 對話內容、使用者 id、群組 id**；`ChatThreads`／`ChatMessages` 不變，模型呼叫記錄與回覆統計也不含 LINE 使用者
    （`AccountId` 為空）。前文只在記憶體、閒置 30 分鐘清除，因此不會觸發 24 小時告知義務。
  - **不呼叫 LINE 的 Profile API**，不取得使用者名稱或頭像。
  - 系統 log 只記請求的方法、路徑、狀態與 LINE 的 `x-line-request-id`，不記訊息、使用者 id、reply token 或 Token。
    LINE、官網訪客與員工聊天（含固定查詢、表單與案件建議的模型選擇）的路徑上，處理失敗時 log 只記**例外的類型**（及 HTTP 狀態碼），不記例外訊息：模型供應商的錯誤訊息可能引用被拒絕的請求內容。
  - 但訊息內容**會**經由系統送到客戶設定的模型供應商（對話與嵌入模型），這是隱私權政策要揭露的部分；
  - 此外 Official Account Manager 的「聊天」模式（第 10.2 節）由 LINE 自己保存聊天紀錄，不在系統控制範圍內。
- **線上訂位**（第 17.12 節）是例外：訂位頁**會**把顧客填的姓名、電話、Email、備註存進資料庫（代店家處理），訂位時間過後 90 天整筆刪除，
  log 不記這些資料。開放訂位的店，隱私權政策要保留範本的第 5.5 節。

### 10.7 `Line:ApiBaseUrl`

Messaging API 的基底網址（`Line__ApiBaseUrl`）**Production 不要設定**（預設就是官方的 `https://api.line.me`）。Production 若設成別的值，
api 會拒絕啟動，因為 Channel access token 會被送到那個位址。只有測試與 E2E 會把它指到假的 LINE 伺服器；compose 與 `.env.example`
都沒有這個變數，也不需要新增。

## 11. 對話模型清單（M6）

一個部署可以提供不只一個對話模型，讓每個組織的管理者在「系統設定 → 對話模型」自己選。細節（API、錯誤碼、啟動驗證）見
`apps/api/README.md`「Chat model」。

### 11.1 寫法：部署預設與額外的模型

- **`CHAT_*`（`Ai:Chat`）是部署預設。** 每個組織在管理者選別的模型之前都用它；設定頁選單的第一項就是它，標示「（部署預設）」。
  沒有填任何額外模型時，跟 M6 之前完全一樣：一個模型、設定頁只有一個選項，既有的 `.env` 不用改。
- **`CHAT_MODELS_0_*`（`Ai:Chat:Models:0`）是額外的模型。** compose 預留了一組，全部空白（或舊的 `.env` 根本沒有這些變數）時整組略過。
  只要 `CHAT_MODELS_0_PROVIDER` 有值，這一組就會被驗證、出現在設定頁；它有自己的金鑰、輸出上限、逾時與推理強度，與部署預設互不影響。
- 要第三個以上，照第 3 節「頻率限制」的作法另寫一個 compose 檔疊上去，用 `Ai__Chat__Models__1__Provider`、`Ai__Chat__Models__1__Model`…
  （每個欄位與下表相同，編號從 1 往上加，中間不要跳號）。

每個欄位的意思（兩組相同）：

| 部署預設 | 額外的模型 | 說明 |
| --- | --- | --- |
| `CHAT_PROVIDER` | `CHAT_MODELS_0_PROVIDER` | `OpenAI`、`AzureOpenAI` 或 `OpenAICompatible`；`Fake` 在 Production 會拒絕啟動。額外的模型空白 = 這組不存在。**有額外的模型時，部署預設不可空白**（拒絕啟動）。 |
| `CHAT_ENDPOINT` | `CHAT_MODELS_0_ENDPOINT` | `AzureOpenAI`、`OpenAICompatible` 必填；`OpenAI` 可空白。 |
| `CHAT_MODEL` | `CHAT_MODELS_0_MODEL` | 有供應商時必填，例如 `gpt-6-luna`（Azure 填部署名稱）。紀錄（`ModelInvocations`、題組紀錄、報表摘要）記的是這個名稱。 |
| `CHAT_API_KEY` | `CHAT_MODELS_0_API_KEY` | `OpenAI`、`AzureOpenAI` 必填。只放在 `deploy/.env`，不要 commit；啟動 log 與 API 都不會顯示金鑰。 |
| `CHAT_ID` | `CHAT_MODELS_0_ID` | 這個模型的 id，組織的選擇存的就是它。空白 = 模型名稱。只能用英數字、`-`、`_`、`.`，最多 64 字；**id 重複會拒絕啟動**。組織選了之後不要再改（見 11.4）。 |
| `CHAT_DISPLAY_NAME` | `CHAT_MODELS_0_DISPLAY_NAME` | 設定頁顯示的名稱，例如「gpt-5.6-terra（進階）」。空白 = 模型名稱。 |
| `CHAT_MAX_OUTPUT_TOKENS` | `CHAT_MODELS_0_MAX_OUTPUT_TOKENS` | 每次呼叫的輸出上限；空白 = 供應商預設。 |
| `CHAT_TIMEOUT_SECONDS` | `CHAT_MODELS_0_TIMEOUT_SECONDS` | 呼叫逾時秒數；空白 = 用戶端預設。 |
| `CHAT_REASONING_EFFORT` | `CHAT_MODELS_0_REASONING_EFFORT` | `None`、`Low`、`Medium`、`High`、`ExtraHigh`；空白 = 供應商預設。見 11.2。 |

範例（2026-10-07 實機驗收用的組合，金鑰換成你自己的）：

```sh
CHAT_PROVIDER=OpenAI
CHAT_MODEL=gpt-6-luna
CHAT_API_KEY=<OpenAI 金鑰>
CHAT_REASONING_EFFORT=None

CHAT_MODELS_0_PROVIDER=OpenAI
CHAT_MODELS_0_MODEL=gpt-5.6-terra
CHAT_MODELS_0_API_KEY=<OpenAI 金鑰>
CHAT_MODELS_0_ID=gpt-5.6-terra
CHAT_MODELS_0_DISPLAY_NAME=gpt-5.6-terra（進階）
CHAT_MODELS_0_REASONING_EFFORT=None
```

啟動後，api 的 log 會逐一列出每個模型（不含金鑰），可以用來確認第二組有沒有被讀到：

```text
Chat: model gpt-6-luna (deployment default): provider openai, model gpt-6-luna.
Chat: model gpt-5.6-terra: provider openai, model gpt-5.6-terra.
```

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs api | grep 'Chat: model'
```

### 11.2 推理強度：要用工具的模型一定要設 `None`

推理強度是**每個項目各自設定**的。系統有三種呼叫會把「工具」交給模型：數據庫查詢（固定查詢）、表單請求，以及案件提議。
`gpt-6-luna` 與 `gpt-5.6-terra` 在 Chat Completions 上**只有推理強度是 `None` 時才接受工具**；沒設或設成其他值，供應商回
HTTP 400（「Function tools with reasoning_effort are not supported … in /v1/chat/completions」），結果是：

- 數據庫查詢：使用者看到錯誤（`chat-unavailable`），統計問題答不出來；
- 表單請求與案件提議：退回關鍵字判斷，使用者沒有明講「填表」就不會出現表單；
- 一般回答、題組重跑、報表摘要不帶工具，照常可用，所以**只看一般問答會以為設定正確**。

所以這兩個模型都要設 `CHAT_REASONING_EFFORT=None`、`CHAT_MODELS_0_REASONING_EFFORT=None`。換成其他模型前，先用它問一題統計問題與一題
需要表單的問題，確認兩種工具呼叫都成功（2026-10-07 的驗收方法見
[`docs/evals/2026-10-07-245-model-switch-acceptance.md`](../docs/evals/2026-10-07-245-model-switch-acceptance.md)）。

### 11.3 組織換模型之後

- 管理者在設定頁換模型後，**下一次**呼叫就用新模型：對話、精靈試問、題組重跑、數據庫查詢、表單請求、案件提議、報表摘要
  （背景工作在執行時才決定模型），官網與 LINE 也一樣。已經在跑的那一次不受影響。
- 呼叫紀錄（`ModelInvocations`）、題組紀錄（`AssistantTestRuns.Model`）與報表的 AI 摘要（`SummaryModel`）都記實際用到的模型名稱。
- 驗收頁比對「最近一次題組重跑的模型」與「目前生效的模型」，不同時提示「上次測試使用模型 X，現在是 Y。建議重跑題組。」
- 每月 token 上限（第 6 節）是**所有對話模型合計**，不分模型。不同模型的價格不同，換到較貴的模型時，上限要自己重新估算。
- 評測指令（`eval-answers`、`eval-form-requests`、`eval-case-proposals`）一律用部署預設。

### 11.4 移除或更改模型的影響

- **移除一個額外的模型**（清空 `CHAT_MODELS_0_PROVIDER` 再重啟），或**改掉它的 id**：選了它的組織會**自動改用部署預設**，不會壞掉；
  設定頁顯示「原本選的模型已不再提供，目前使用部署預設 X」，直到管理者重新選擇。組織原本的選擇仍保存著：同一個 id 之後再加回來，
  這些組織就自動回到它。移除前，先通知選了它的組織。
- **換部署預設的模型**（改 `CHAT_MODEL`）：在設定頁選「部署預設」的組織跟著換。若 `CHAT_ID` 是空白，部署預設的 id 也會跟著變成新的模型名稱。
- **改一個項目的模型名稱、id 不變**：選了它的組織直接用新模型；驗收頁會因為模型名稱不同而提示重跑題組。
- 不管哪一種，換模型後都建議各助理重跑一次題組。

### 11.5 同一個模型名稱放兩個項目

可以，例如同一個模型設兩種推理強度，但兩個項目**一定要有不同的 id**（id 預設是模型名稱，重複會拒絕啟動），也請給不同的顯示名稱。限制：

- 驗收頁的提示與所有紀錄（`ModelInvocations.Model`、`AssistantTestRuns.Model`、報表 `SummaryModel`）都只記**模型名稱**。在這兩個項目之間切換，
  驗收頁**不會**提示重跑題組，事後也無法從紀錄分辨是哪一個項目回答的。
- 需要分辨時，請改用不同的模型（或 Azure 上不同名稱的部署），不要靠同名的兩個項目。

## 12. 對話保存期限與每日清理（M6）

每個組織的管理者在「系統設定 → 對話保存」選對話要保存多久：30、90、180、365 天，或永久（預設永久）。細節見 `apps/api/README.md`
「Conversation retention and the daily cleanup」。部署端沒有要填的變數，但營運人員要知道下面幾件事。

- **縮短有 7 天緩衝期。** 從永久改成有天數、或改成更短的天數時，新的期限在 7 天後才生效，期間不刪除任何東西，管理者也可以改回。
  延長（含改回永久）立即生效。
- **每日清理**：每個組織每天在 `STATISTICS_TIME_ZONE`（預設 `Asia/Taipei`）的 **03:00** 執行一次（時間固定，不能設定），由 api 容器內的背景工作執行，
  compose 不需要另外的服務。清理會刪除最後一則訊息早於「當地今天 00:00 減 N 天」的整串對話（含訊息與引用），以及同樣早於這個時間的回答紀錄
  （所有通道，含官網）。每批 1,000 串、各自一個交易。
- **不受影響**：模型呼叫紀錄、處理事項裡的問答副本、定期報表、數據庫紀錄與題組紀錄。
- **轉真人通知另有固定的期限**（第 16 節）：已處理超過 365 天的會在同一個 03:00 刪除，**每個組織都會**（保存期限是永久也一樣）；待處理的永遠不刪。
- **線上訂位也有固定的期限**（第 17.12 節）：訂位時間過後 90 天整筆刪除，同一個 03:00、每個組織都會，不論訂位的狀態。
  統籌助理的**推播紀錄**（只有店家、時間、用途與則數）保存 400 天，同一個 03:00 刪除。
- **第一次啟用時的離峰提醒**：一個用了很久的組織第一次縮短期限時，緩衝期過後的第一次清理可能一次刪掉大量對話，資料庫的 WAL 與磁碟用量會短暫升高。
  - 先確認磁碟有餘裕，並在緩衝期內做一次資料庫備份（第 4 節）。
  - 清理固定在 03:00，若客戶的離峰不是凌晨，請和管理者約好變更的時間點，讓第一次清理落在可以接受的時段。
  - 要手動提早執行（下方指令），也請選在離峰。
- 清理有刪除時會在組織活動紀錄寫一筆「刪除 N 串對話、M 筆回答紀錄」，設定頁的「上次變更」也看得到；每次執行都會記 OpenTelemetry 指標
  `smartagri.retention.cleanup.runs` 與 `smartagri.retention.cleanup.deleted`，指標長時間沒有增加就代表清理沒有在跑。
- **安全網**：清理工作重試用完失敗時，這個組織的每日清理就停了。重啟 api 會自動為「有天數卻沒有待執行清理」的組織補排一次。
- 立即執行一次某個組織的清理（不影響每天的排程）：

  ```sh
  docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api retention-cleanup --organization <組織代碼>
  ```

  這個指令也會刪除該組織已處理超過 365 天的轉真人通知，輸出最後一行寫刪了幾筆。
  結束碼 `0` 完成、`1` 組織不存在或清理失敗、`2` 參數錯誤。`--as-of`（模擬未來時間）只給開發與測試用，Production 會拒絕。
- **立即刪除既有對話**：管理者可以在「系統設定 → 對話保存」對單一助理立即刪除所有成員已保存的對話，不等保存期限；處理事項裡的問答副本與回答紀錄不受影響。
  這是管理介面的操作，不需要營運人員介入，但刪除後無法復原，資料庫備份是唯一的救回方式。

## 13. 哪些指令必須在真實部署上才能驗證

本文的 `openssl`、`docker compose config`、`set-token-limit`（含說明、未知組織、參數錯誤與實際設定）與 nginx 設定語法，
都已在本機實際執行過；`docker compose up` 與備份還原（第 14 節）也由 `deploy/smoke-test.sh`、`deploy/backup-drill.sh`
在本機的暫時環境實際跑過，升級與回滾（第 15 節）也由 `deploy/upgrade-drill.sh` 從前一版實際演練過。需要真實伺服器、公開網域與 TLS 才能驗證的是：對外網址的 `curl`、cron／systemd 排程與 rsync 到另一台電腦，
以及在真實官網上的嵌入驗收。

LINE 部分已在真實的 LINE 官方帳號上以 Cloudflare 通道與真實模型驗收過（一對一、群組、歡迎訊息、非文字訊息、暫停與恢復、逾時補送、
自動回應訊息未關閉時的行為），紀錄見 [`docs/evals/2026-10-07-235-line-acceptance.md`](../docs/evals/2026-10-07-235-line-acceptance.md)。
客戶自己的官方帳號、公開網域與方案額度仍需在交付時各自確認（第 10 節）。

對話模型清單（第 11 節）已在本機以兩個真實模型（部署預設 `gpt-6-luna`、額外的 `gpt-5.6-terra`，推理強度都是 `None`）實際切換驗收：
試問、題組重跑、表單請求、數據庫查詢與報表摘要都記到切換後的模型，兩個模型都完成了表單與數據庫查詢的工具呼叫，紀錄見
[`docs/evals/2026-10-07-245-model-switch-acceptance.md`](../docs/evals/2026-10-07-245-model-switch-acceptance.md)。compose 預留的
`CHAT_MODELS_0_*` 空白與填寫兩種情況由啟動測試（`ChatModelCatalogStartupTests`）解析 compose 檔驗證，`docker compose config` 也實際展開過；
填了之後在真實部署上 `up`、看設定頁出現兩個模型，仍需在交付時確認。

## 14. 備份與還原

這一節教你：每天自動備份、把備份放到這台電腦以外的地方、電腦壞掉或資料出問題時怎麼還原，以及多久練習一次。
所有指令都在 **repo 根目錄**（有 `deploy/` 資料夾的那一層，例如 `/home/<你的使用者名稱>/smart-agri`）執行。

### 14.1 備份了什麼？為什麼「金鑰」那麼重要？

`deploy/backup.sh` 每跑一次，就在 `deploy/backups/` 產生**一個**檔案，例如 `smartagri-backup-20261013-023000.tar`
（檔名是日期與時間）。裡面有：

| 內容 | 是什麼 | 沒有它會怎樣 |
| --- | --- | --- |
| 資料庫（`database.dump`） | 所有店家、帳號、助理、知識庫、對話、案件……全部的資料 | 一切都沒了 |
| Data Protection 金鑰（`dataprotection-keys.tar.gz`） | 打開「加密資料」的鑰匙（第 4 節）：LINE 的 Channel Secret 與 Access Token 就是加密後才存進資料庫 | 資料庫還原了，**加密的資料卻打不開**：每個 LINE 頻道都要重新輸入憑證，所有人要重新登入 |
| 三個憑證（`certs.tar.gz`） | `deploy/certs/` 的 `signing.pfx`、`encryption.pfx`、`dataprotection.pfx`；`dataprotection.pfx` 是用來鎖住上面那組金鑰的 | 換一張新的憑證也打不開舊金鑰，結果同上 |
| 設定檔（`env`） | `deploy/.env` 的複本：裡面有三個憑證的**密碼**、資料庫密碼、模型的 API 金鑰 | 沒有密碼，憑證檔就打不開，結果同上 |

> **一份備份檔＝這個部署的全部秘密。** 它和 `deploy/.env` 一樣重要：只放在只有你拿得到的地方（自己的外接硬碟、自己的另一台電腦），
> 不要放到公開分享的雲端資料夾，也不要寄給別人。腳本會把檔案設成只有你能讀（權限 600，資料夾 700）。

腳本**不會印出**任何密碼或金鑰；資料庫是在服務照常運作時備份的（不用停機）。

### 14.2 先手動備份一次

```sh
deploy/backup.sh
```

最後一行是 `backup: OK ...`，寫著檔案位置、大小、資料庫有幾個資料表、幾個金鑰檔、幾個憑證，以及目前保留幾份備份。
任何一步失敗都會印 `backup: FAIL: <原因>`，而且不會留下不完整的檔案。最常見的失敗是服務沒有在跑
（`the postgres container is not running`）：先 `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d` 再試。

**保留幾份**：預設保留最新的 14 份，更舊的會在備份成功後自動刪掉。要改就在前面加 `BACKUP_KEEP=30`；
要換存放位置就加 `BACKUP_DIR=/某個/資料夾`（14.4 節）。例如：

```sh
BACKUP_KEEP=30 BACKUP_DIR=/media/<你的使用者名稱>/外接硬碟/smartagri-backups deploy/backup.sh
```

> 有另外疊一個 compose 檔（第 3 節的 `docker-compose.limits.yml`）的話，在每個指令前面加
> `DEPLOY_COMPOSE_FILES="deploy/docker-compose.yml deploy/docker-compose.limits.yml"`，備份與還原才會用同一組設定。

### 14.3 每天自動備份

**方法一：cron（最簡單）。** 用平常登入主機、能執行 `docker` 的那個使用者執行 `crontab -e`，在最後加一行
（路徑換成你的 repo 位置；以下是每天凌晨 2:30，剛好在 03:00 的對話清理之前，見第 12 節）：

```cron
30 2 * * * /home/<你的使用者名稱>/smart-agri/deploy/backup.sh >> /home/<你的使用者名稱>/smartagri-backup.log 2>&1
```

存檔離開就生效。隔天早上看一下紀錄的最後一行是不是 `backup: OK`：

```sh
tail -n 1 ~/smartagri-backup.log
```

**方法二：systemd timer（電腦關機錯過時間，開機後會補做）。** 建立兩個檔案（要 `sudo`），`<你的使用者名稱>` 與路徑換成你的：

```ini
# /etc/systemd/system/smartagri-backup.service
[Unit]
Description=smart-agri backup
After=docker.service

[Service]
Type=oneshot
User=<你的使用者名稱>
ExecStart=/home/<你的使用者名稱>/smart-agri/deploy/backup.sh
```

```ini
# /etc/systemd/system/smartagri-backup.timer
[Unit]
Description=smart-agri backup every night

[Timer]
OnCalendar=*-*-* 02:30
Persistent=true

[Install]
WantedBy=timers.target
```

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now smartagri-backup.timer
systemctl list-timers smartagri-backup.timer      # 看下次什麼時候執行
journalctl -u smartagri-backup.service -n 5       # 看最近一次的結果（最後一行應該是 backup: OK）
```

兩種方法選一種就好，不要同時用。

### 14.4 把備份放到這台電腦以外的地方

備份只放在同一台電腦上，硬碟壞掉時會一起不見，所以**至少要有一份在別的地方**。選一種：

- **外接硬碟（插著不拔）**：直接把備份寫到外接硬碟，cron 那一行改成
  `BACKUP_DIR=/media/<你的使用者名稱>/<硬碟名稱>/smartagri-backups /home/.../deploy/backup.sh >> ...`。
  硬碟沒插上時備份會失敗（紀錄裡看得到 `FAIL`），不會默默寫到別處。
- **另一台電腦（透過 Tailscale）**：另一台電腦也登入同一個 Tailscale 帳號、開好 SSH，並讓主機能用 SSH 金鑰登入它
  （`ssh-copy-id <使用者>@<那台的 Tailscale 名稱>`）。然後 cron 那一行改成「備份成功才複製」：

  ```cron
  30 2 * * * /home/<你>/smart-agri/deploy/backup.sh >> /home/<你>/smartagri-backup.log 2>&1 && rsync -a /home/<你>/smart-agri/deploy/backups/ <使用者>@<那台的 Tailscale 名稱>:smartagri-backups/ >> /home/<你>/smartagri-backup.log 2>&1
  ```

  `rsync -a` 只會新增檔案、不會刪掉另一台上的舊備份；另一台的空間偶爾要自己清。

檢查另一個地方的備份檔能不能讀（列出 6 個項目就是完整的）：

```sh
tar -tf <備份檔>
```

### 14.5 還原

還原會把**資料庫整個換成備份當時的樣子**（備份之後新增的資料會不見），所以只在真的需要時做。還原腳本 `deploy/restore.sh` 會：
檢查備份檔沒有損壞 → 把缺少的 `deploy/.env` 與憑證放回去 → 停掉 api → 還原資料庫與金鑰 → 重新啟動 api → 確認 `/health/ready` 回 200。
最後一行是 `restore: OK ...`；任何一步失敗都會印 `restore: FAIL: <原因>`，而且在真正換掉資料庫之前失敗的話，原本的資料庫不會被動到。

**情況 A：同一台電腦，資料出問題（例如誤刪）**

1. 找出要用的備份：`ls deploy/backups/`（檔名就是時間）。
2. 執行（因為資料庫裡還有資料，要加 `--force` 表示「我確定要整個換掉」）：

   ```sh
   deploy/restore.sh --force deploy/backups/smartagri-backup-20261013-023000.tar
   ```

   沒加 `--force` 時，腳本會說 `already holds data` 並拒絕，什麼都不改。
3. 用瀏覽器登入管理介面確認資料。有人被要求重新登入是正常的。

**情況 B：換一台新電腦（舊電腦壞了）**

1. 新電腦照第 2 節裝好 Docker，把 repo 下載下來（`git clone`，版本和舊電腦一樣或更新）。
   **不要**自己產生新的憑證、也**不要**建立新的 `deploy/.env`：這些都在備份裡。
2. 把備份檔複製到新電腦（例如從外接硬碟或另一台電腦），然後執行：

   ```sh
   deploy/restore.sh /某個/位置/smartagri-backup-20261013-023000.tar
   ```

   它會先把 `deploy/.env` 與 `deploy/certs/` 放回去，第一次會建置映像檔（要幾分鐘），再還原資料並啟動。
3. 照第 3 節最後的 `curl` 檢查，並確認 `PUBLIC_BASE_URL`（Tailscale Funnel 的網址）在新電腦上也通；網址變了的話，
   改 `deploy/.env` 後 `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d`，
   再到每個 LINE 頻道按一次「測試連線」（第 10.1 節）。
4. 在新電腦設定每天備份（14.3 節）。

> 如果新電腦上**已經**有不一樣的 `deploy/.env` 或憑證，腳本會列出哪些檔案不同並拒絕；確定要用備份裡的版本時加 `--force`，
> 原本的檔案會改名保留成 `<檔名>.before-restore-<時間>`。

### 14.6 多久練習一次還原

備份要「還原得回來」才算數。

- **每個月一次**（以及每次升級版本之後），在主機上跑演練腳本。它在一套**完全獨立**的暫時環境裡做完整的
  「建立資料 → 備份 → 全部刪掉 → 還原 → 檢查」，不會碰到正式的資料、`deploy/.env`、`deploy/certs/` 或 `deploy/backups/`：

  ```sh
  deploy/backup-drill.sh
  ```

  大約幾分鐘（第一次要先建置映像檔）。最後一行是 `backup-drill: PASS: ...` 就代表備份與還原腳本在這台電腦上是好的。
  它會檢查：原帳號能用原密碼登入、助理／知識庫／對話都在、加密的 LINE 憑證解得開，以及沒加 `--force` 時會拒絕覆蓋、資料庫沒在跑時備份會失敗。
  CI 的 `docker-smoke` job 在每個 PR 上也會跑同一支腳本（接在冒煙測試之後，約 70 秒）。
- **每週看一次**備份紀錄的最後一行是 `backup: OK`（14.3 節），並確認另一個地方也有最新的檔案（14.4 節）。

### 14.7 可以調整的設定（都可以不設）

| 環境變數 | 預設 | 用途 |
| --- | --- | --- |
| `BACKUP_DIR` | `deploy/backups` | 備份放哪裡（已加入 `.gitignore`） |
| `BACKUP_KEEP` | `14` | 保留最新的幾份 |
| `DEPLOY_ENV_FILE` | `deploy/.env` | 部署用的設定檔 |
| `DEPLOY_CERTS_DIR` | `deploy/certs` | 憑證資料夾 |
| `DEPLOY_COMPOSE_FILES` | `deploy/docker-compose.yml` | compose 檔（多個用空白隔開，見 14.2 節） |
| `DEPLOY_PROJECT` | compose 檔裡的 `smartagri` | compose 專案名稱 |
| `RESTORE_TIMEOUT_SECONDS` | `180` | 還原時等資料庫與 `/health/ready` 的秒數 |

`deploy/backup.sh --help` 與 `deploy/restore.sh --help` 會印出英文的詳細說明。

## 15. 升級到新版本

這一節教你：把系統換成開發者提供的新版本，以及新版有問題時怎麼回到原本的版本（**回滾**）。和第 14 節一樣，
所有指令都在 **repo 根目錄**執行；有另外疊一個 compose 檔（第 3 節的 `docker-compose.limits.yml`）的話，每個
`docker compose` 指令都要把兩個 `-f` 帶齊，`backup.sh`／`restore.sh` 前面加 `DEPLOY_COMPOSE_FILES`（14.2 節）。

> **先知道三件事**
>
> - 升級時服務會中斷大約一分鐘（15.2 的第 6、7 步），挑人少的時段做。
> - **升級前的備份是回滾的唯一保障**：第 3 步一定要做，而且要看到 `backup: OK`。
> - 新版的 api 會更新資料庫的結構（`migrate`）。更新過之後，舊版不一定看得懂新的資料庫，所以回滾時要從
>   升級前的備份還原（15.3 節），**升級之後才新增的資料（新對話、新案件……）會不見**。有問題就盡早決定要不要回滾。

### 15.1 怎麼看系統正不正常：`docker compose ps`

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env ps
```

看 `STATUS` 那一欄，`api` 與 `postgres` 都應該是 `(healthy)`：

| 看到 | 意思 | 要做什麼 |
| --- | --- | --- |
| `Up 3 minutes (healthy)` | 正常 | 不用做什麼 |
| `Up 8 seconds (health: starting)` | 剛啟動，還在檢查 | 等 30 秒再看一次（最多 1 分鐘） |
| `Up 5 minutes (unhealthy)` | api 程式還在跑，但已經不回應 | 先看 log（下面），再 `docker compose -f deploy/docker-compose.yml --env-file deploy/.env restart api`。Docker **不會**自己重啟 unhealthy 的容器 |
| `Restarting (1) 4 seconds ago` | api 一啟動就結束，Docker 一直重試（例如連不上資料庫、讀不到憑證） | 看 log，對照第 9 節 |

api 的 `healthy` 是 Docker 每 10 秒問一次 `/health/live`（網頁伺服器有沒有回應）的結果；要連資料庫一起確認，用
`/health/ready`（`8080` 是 `.env` 的 `API_PORT`，改過就換成你的值；也可以用公開網址 `<網址>/health/ready`）：

```sh
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/health/ready     # 200 = api 與資料庫都正常
docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs --tail 50 api   # 最近 50 行 log
```

> **日誌不會塞滿硬碟**：每個容器的 log 最多保留 5 個 10 MB 的檔案（約 50 MB），更舊的自動刪掉，不用自己清理
> （`docker-compose.yml` 的 `x-logging`）。`docker compose logs` 看得到的也就是這些。

### 15.2 升級步驟

1. **確認現在一切正常**：照 15.1 看到 `api`、`postgres` 都是 `(healthy)`。再確認 repo 裡的檔案沒有被改過
   （`deploy/.env`、`deploy/certs/`、`deploy/backups/` 不算，它們不在 git 裡）：

   ```sh
   git status --short
   ```

   應該**什麼都沒印**。有印出檔案的話先不要升級，把畫面交給開發者。

2. **記下目前的版本，並把目前的映像檔留一份**（回滾時就不必重新建置）：

   ```sh
   git log -1 --format='%h %cd' --date=short
   docker tag smartagri-api "smartagri-api:$(git rev-parse --short HEAD)"
   docker image ls smartagri-api
   ```

   第一行印出例如 `10d2fda 2026-10-13`：**把前面那串（`10d2fda`）抄下來**，下面稱為「舊版」。最後一行的清單裡應該有
   `latest` 和這個版本號兩列。

3. **備份**（第 14.2 節）：

   ```sh
   deploy/backup.sh
   ```

   最後一行要是 `backup: OK ...`，並**記下檔名**（例如 `deploy/backups/smartagri-backup-20261013-140000.tar`）。
   不是 `OK` 就停在這裡，先解決備份的問題。

4. **取得新版**：

   ```sh
   git checkout master
   git pull
   git log -1 --format='%h %cd' --date=short
   ```

   平常就在 `master` 上，第一行會說 `Already on 'master'`；回滾過（15.3 節）之後，這一行會把 repo 帶回 `master`。
   最後一行是新版的版本號。開發者給的是版本標籤（例如 `v1.2.0`）時，把前兩行換成
   `git fetch --tags` 與 `git checkout v1.2.0`。

5. **建置新版的映像檔**（要幾分鐘；這段時間舊版照常服務）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env build api
   ```

   最後出現 `Built` 就是成功。**失敗的話**：正在跑的還是舊版、資料也沒動過，執行 `git checkout <舊版>` 回到舊版的程式
   就好（不用還原備份），把錯誤訊息交給開發者。

6. **停掉 api，更新資料庫結構**（服務從這裡開始中斷）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env stop api
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api migrate && echo MIGRATE-OK
   ```

   最後印出 `MIGRATE-OK` 就是成功。其實 api 每次啟動都會先自動跑一次 `migrate`（`apps/api/src/SmartAgri.Api/entrypoint.sh`），
   這裡先單獨跑，是為了在新版啟動之前看清楚它成功了沒。**沒有印出 `MIGRATE-OK`**：照 15.3 節回滾。

7. **啟動新版**：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d
   ```

   compose 會用新的映像檔重建 api 容器；compose 檔裡 postgres 的設定有變時（例如第一次升級到有日誌輪替的版本），
   postgres 也會重建一次。資料都在 volume 裡，不受影響。

8. **驗證**：
   - 等 30 秒，照 15.1 執行 `docker compose ... ps`：`api` 與 `postgres` 都是 `(healthy)`；
   - `curl` 檢查 `/health/ready` 回 `200`（15.1 節），以及第 3 節最後那四個公開網址的檢查；
   - 用瀏覽器登入管理介面，打開一個助理與知識庫，確認資料都在，用試問問一題；有 LINE 頻道的話按一次「測試連線」（第 10.1 節）。

   都正常就完成了。第 3 步的備份與第 2 步的映像檔先留著，至少用一週沒問題再說。`deploy/smoke-test.sh`（9.1 節）
   **不能**拿來檢查正式環境（它自己起一套暫時的環境、測完就刪），要用它的話，在第 4 步之後、第 5 步之前跑，
   先確認新版在這台電腦上建得起來、啟動得了。

> **舊的映像檔偶爾清一下**：每個版本的映像檔約 400 MB。`docker image ls smartagri-api` 列出所有版本，留下最近兩、三個，
> 其他用 `docker image rm smartagri-api:<版本號>` 刪掉（`latest` 是正在用的，不要刪）。

### 15.3 回滾：回到升級前的版本

在 15.2 第 6 步 `migrate` 失敗，或第 8 步驗證不通過、一時修不好時使用。結果是：程式回到舊版、資料庫回到第 3 步備份的那一刻。

1. **停掉 api**：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env stop api
   ```

2. **回到舊版的程式**（`<舊版>` 換成 15.2 第 2 步抄下來的版本號）：

   ```sh
   git checkout <舊版>
   ```

   會印出一段說明「detached HEAD」的英文，這是正常的：repo 現在停在舊版上，下次升級的第 4 步會把它帶回 `master`。

3. **把舊版的映像檔換回來**（不用重新建置）：

   ```sh
   docker tag smartagri-api:<舊版> smartagri-api
   ```

   如果出現 `No such image`（第 2 步漏做了），改用
   `docker compose -f deploy/docker-compose.yml --env-file deploy/.env build api` 重新建置舊版（要幾分鐘）。

4. **移除用新版映像檔建立的 api 容器**，讓下一步用舊版的映像檔重新建立（資料在 volume 裡，不受影響）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env rm -f api
   ```

5. **從升級前的備份還原**（14.5 節；檔名是 15.2 第 3 步記下的那一個）：

   ```sh
   deploy/restore.sh --force deploy/backups/smartagri-backup-20261013-140000.tar
   ```

   最後一行要是 `restore: OK ...`。它會重新建立 api 容器、還原資料庫與金鑰，再啟動 api。

6. **驗證**：和 15.2 第 8 步一樣。然後把發生了什麼事、錯誤訊息（15.1 的 `logs` 指令）交給開發者。之後要再升級時，
   從 15.2 第 1 步重新開始。

> **可以跳過還原的情況（進階，不確定就不要用）**：兩個版本之間資料庫結構完全沒變時，只做第 1～4 步，再
> `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d`，就能保留升級後新增的資料。判斷方法：
> `git diff --stat <舊版> <新版> -- apps/api/src/SmartAgri.Infrastructure/Migrations` 什麼都沒印。
>
> 本節的回滾需要舊版也有 `deploy/restore.sh`（#342 之後的版本都有）。

### 15.4 正式升級前先演練（選用）

`deploy/upgrade-drill.sh` 在一套**完全獨立**的暫時環境裡，把 15.2 與 15.3 從頭到尾做一次：用舊版建立資料 → 備份 →
升級到目前 repo 的版本 → 確認 `(healthy)` 與資料都在 → 回滾到舊版並從備份還原 → 再確認一次。它不會碰到正式的資料、
`deploy/.env`、`deploy/certs/` 或 `deploy/backups/`，也不會碰正式環境的 `smartagri-api` 映像檔。在 15.2 第 4 步之後、第 5 步之前執行：

```sh
deploy/upgrade-drill.sh <舊版>
```

要建置兩個版本的映像檔：第一次大約十幾分鐘，之後有建置快取時兩、三分鐘。最後一行是 `upgrade-drill: PASS: ...` 就代表這次的升級與回滾在這台電腦上走得通；
`upgrade-drill: FAIL: <原因>` 就先不要升級，把畫面交給開發者。說明：`deploy/upgrade-drill.sh --help`。

## 16. 轉真人通知與 Email（SMTP）

官網或 LINE 的訪客問到助理答不出來的問題時，會收到助理的「找不到資料時的回覆」（例如「店長會在這裡回覆您」）。
這時系統會**通知那個助理的擁有者**（建立助理、設定官網與 LINE 發布的人）：

- **管理介面**：側欄「轉真人」旁邊顯示待處理的數量；點進去可以看到哪個助理、哪個管道（官網／LINE）、什麼時間，
  處理完按「標記已處理」。
- **Email**（選用）：這一節設定好寄信伺服器、而且擁有者在「團隊與設定 → 我的通知」填了自己的 Email，就會同時寄信。
  每個人都可以在同一個地方關掉自己的轉真人 Email。

同一個寄信伺服器也用來寄線上訂位的通知信給顧客（接受、婉拒、過期、店休取消，第 17.12 節「顧客收到的 Email」）。

一些固定的規則：

- 系統**不保存訪客的任何內容**（問題、回答、LINE 的使用者 ID 都不存），信裡也只有組織、助理、管道、時間與後台連結
  （`{管理介面網址}/app/handoffs`）。要回覆 LINE 的使用者，請到 LINE Official Account Manager 的「聊天」（第 10.2 節）；
  官網的訪客則會依「找不到資料時的回覆」裡寫的聯絡方式找你。
- 同一位訪客（官網的同一個對話視窗、LINE 的同一個聊天）半小時內再問到答不出來的問題，**不會再通知一次**。
- 寄信由背景工作負責，**失敗會自動重試**（30 秒、1 分、2 分、4 分後，共 5 次），不會讓訪客的對話變慢或出錯。
- **保存期限**：標記為已處理超過 365 天的通知，會在每天 `STATISTICS_TIME_ZONE` 的 03:00 自動刪除（與第 12 節的對話清理同一時間，
  但不看組織的對話保存期限，每個組織都會執行）；**待處理的不管多久都不刪**。通知本來就不含訪客內容，所以不另外寫組織活動紀錄，
  只記在 api 的紀錄與 `smartagri.retention.cleanup.deleted` 指標（`record` = `visitor-handoff`）。要立刻刪某個組織的，用第 12 節的 `retention-cleanup`。

### 16.1 設定寄信伺服器

在 `deploy/.env` 填（全部留空就是不寄 Email，其他功能照常）：

| 變數 | 填什麼 |
| --- | --- |
| `SMTP_HOST` | 寄信伺服器，例如 `smtp.gmail.com`。留空 = 不寄 Email |
| `SMTP_PORT` | 留空 = `587`（STARTTLS 加密）。**不支援 `465`**，填了 api 會拒絕啟動 |
| `SMTP_USERNAME`、`SMTP_PASSWORD` | 登入寄信伺服器的帳號與密碼；兩個一起填，或一起留空（公司內部不需登入的轉寄伺服器） |
| `SMTP_FROM` | 寄件人地址，例如 `notify@example.org`；填了 `SMTP_HOST` 就必填 |
| `SMTP_FROM_NAME` | 收件匣顯示的寄件人名稱；留空 = 「AI 助理工作台」。線上訂位寄給顧客的信不用這個名稱，而是**店名**（第 17.12 節） |

**用 Gmail 寄（以 `shop@gmail.com` 為例）**：

1. 這個 Google 帳戶要先開啟「兩步驟驗證」（Google 帳戶 → 安全性）。
2. 到 Google 帳戶 →「安全性」→「應用程式密碼」，建立一組應用程式密碼（名稱隨意，例如 smartagri），畫面會顯示 16 個字元的密碼。
   **不要用 Google 帳戶本身的密碼**，也不要把應用程式密碼貼進聊天或工單。
3. 在 `deploy/.env` 填：

   ```sh
   SMTP_HOST=smtp.gmail.com
   SMTP_PORT=587
   SMTP_USERNAME=shop@gmail.com
   SMTP_PASSWORD=<剛才的應用程式密碼>
   SMTP_FROM=shop@gmail.com
   ```

4. 重新啟動 api：`docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d`。
   設定有誤（例如 `SMTP_FROM` 不是 Email、只填了帳號沒填密碼、埠填 465）時 api 不會啟動，
   `docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs api` 會寫出是哪一個 `Smtp:` 設定。

Gmail 個人帳戶每天能寄的信有上限（約幾百封），一般店家的轉真人通知用不到；量大時改用公司的郵件服務。

### 16.2 每個人自己設定收信

每位同仁登入管理介面 →「團隊與設定」→「我的通知」：填自己的 Email、勾選「訪客要找真人時寄 Email 給我」，按「儲存通知設定」。
沒填 Email、或取消勾選，就不會寄信給他（站內通知不受影響）。部署沒有設定 SMTP 時，這裡會寫「這個部署還沒有設定寄信伺服器」。

有設定 SMTP、但自己還沒填 Email（也沒取消勾選）的人，「轉真人」頁上方會提醒「你還沒有填通知 Email」並附「我的通知」的連結；
填好或取消勾選後就不再出現。Email 只能由本人填寫，管理者不能代填。

### 16.3 怎麼確認信有沒有寄出

1. **看 api 的紀錄**：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs api | grep -E 'Visitor handoff|Handoff Email|notifications.handoff-email'
   ```

   | 紀錄 | 意思 |
   | --- | --- |
   | `Visitor handoff ... recorded ...; Email queued: True` | 有一則轉真人，信已排進寄信佇列 |
   | `... Email queued: False` | 有轉真人，但沒排寄信：沒設定 SMTP，或擁有者沒填 Email／關掉了 |
   | `Handoff Email for ... sent (attempt 1)` | 信已交給寄信伺服器 |
   | `Background job ... (notifications.handoff-email) failed on attempt 1 of 5; retrying after ...` | 這次沒寄成功，稍後自動重試 |
   | `Handoff Email job ... gave up after 5 attempts: ...` | 重試 5 次都失敗，不會再寄；後面會寫錯誤的種類（例如 SMTP 的狀態碼） |
   | `Handoff Email for ... not sent: ...` | 決定不寄，後面寫原因（已經處理、擁有者關掉了等） |

   紀錄裡**不會出現**收件人的 Email、SMTP 密碼或訪客的訊息。

2. **看資料庫裡的寄信工作**（最近 10 筆；`Succeeded` = 已寄出，`Queued` 且 `Attempts` 大於 0 = 等著重試，`Failed` = 放棄了）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec postgres \
     psql -U smartagri -d smartagri -c "select \"Status\", \"Attempts\", \"LastError\", \"CreatedAt\" from \"BackgroundJobs\" where \"Kind\" = 'notifications.handoff-email' order by \"CreatedAt\" desc limit 10;"
   ```

3. **常見的失敗**：

   | `LastError` 或紀錄裡看到 | 怎麼辦 |
   | --- | --- |
   | `(SMTP 535)`、`(SMTP AuthenticationRequired)` | 帳號或密碼錯了；Gmail 要用應用程式密碼 |
   | `(SMTP MustIssueStartTlsFirst)` | 伺服器要求加密，但連線沒有加密：確認埠是 587 |
   | `SocketException`、`TimeoutException` | 主機或埠錯了，或這台主機的防火牆擋了往外連 587 |
   | `MailboxUnavailable` | 收件地址不存在：請那位同仁到「我的通知」改正 |

信寄出了卻沒收到時，先請收件人看垃圾郵件匣；Gmail 寄出的信也會留在寄件帳號的「寄件備份」。

## 17. 統籌助理：店家共用的 LINE 帳號與綁定碼

「統籌助理」是**平台共用的一個 LINE 官方帳號**（不是各店自己的帳號，也和第 10 節「助理的 LINE 頻道」無關）。
各店的老闆、店長加它為好友，輸入**綁定碼**，就綁到自己的店（一家店＝一個組織）；之後在 LINE 對它下店務指令。
這個版本（#372–#376）做好友、綁定、**店家設定**（第 17.5 節）、**指令的預覽卡**（第 17.6 節）、**確認後的執行、回報與提醒**（第 17.7 節）
與 **LINE 群發的自動執行**（第 17.8 節），以及讓老闆授權 FB、Google 的**授權連結**（第 17.9 節，#377）：老闆或店長說「今天臨時店休」，統籌助理回一張照店家設定組出的預覽卡，按〔確認〕後逐項回報。
設定了店家自己官方帳號權杖的店，LINE 群發會**自動群發**；老闆授權了粉專、商家的店，FB 貼文（第 17.10 節，#379）與 Google 特殊營業時間
（第 17.11 節，#378）也會自動執行；其他動作都是「提醒」——給手動步驟與〔已完成〕按鈕，點餐系統在開店前 30 分鐘推播提醒。
統籌助理**不回答知識庫問題**；理解指令時會呼叫**那家店的對話模型**（第 11 節；沒有設定對話模型時只會回「抱歉，現在無法理解指令」），用量計入那家店的每月 token 上限（第 6 節）。
回答訊息都用 reply（免費）；**到時間才送的提醒、事後的執行結果與新訂位的卡片用推播**，算在統籌助理這個官方帳號的每月訊息額度
（平台負擔，第 17.7、17.12 節；用 `coordinator-quota` 查看）。
設計見 `docs/plans/2026-11-02-store-coordinator-assistant.md`，各設定的完整行為見 `apps/api/README.md`「Store coordinator」。

### 17.1 建立官方帳號並設定 webhook

1. 在 LINE Official Account Manager 建立「統籌助理」官方帳號，於「設定 → Messaging API」啟用 Messaging API（同第 10.1 節步驟 1）。
2. 在 LINE Developers Console 打開這個 channel，取得：
   - **Channel secret**：「Basic settings」分頁；
   - **Channel access token（long-lived）**：「Messaging API」分頁最下方，按「Issue」。
3. 填進 `deploy/.env`，再 `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d` 讓 api 套用：

   | 變數 | 填什麼 |
   | --- | --- |
   | `COORDINATOR_LINE_CHANNEL_SECRET` | Channel secret |
   | `COORDINATOR_LINE_CHANNEL_ACCESS_TOKEN` | Channel access token（long-lived） |

   兩個都留空 = 不啟用統籌助理。**只填一個，api 不會啟動**，`docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs api`
   會寫出缺的是哪一個 `Coordinator:Line:` 設定（不會印出任何值）。這兩個值是平台的設定，不屬於任何組織，不寫入資料庫、紀錄或 API 回應；
   換 token 之後要重新啟動 api。
4. 回到 LINE Developers Console 的「Messaging API」分頁，把 **Webhook URL** 設成

   ```text
   {PUBLIC_BASE_URL}/api/v1/coordinator/line/webhook
   ```

   例如 `https://assistant.example.org/api/v1/coordinator/line/webhook`，打開「Use webhook」，按「Verify」應該顯示 Success。
   **這個網址要自己貼**：統籌助理沒有第 10 節那種「測試連線」。反向代理要轉發 `/api/v1/coordinator/line/webhook`（第 5 節）。
   簽章錯誤一律回 `401`；送到別的官方帳號的事件、群組與多人聊天室裡的訊息都會被忽略。
5. Official Account Manager 的「回應設定」：**Webhook 開**、**自動回應訊息關**、**加入好友的歡迎訊息關**（統籌助理自己會回歡迎詞，
   開著會收到兩則）。
6. **圖文選單**由營運者在 Official Account Manager 手動建立（不用寫程式）：四個按鈕「臨時店休」「恢復營業」「提早打烊」「延後開店」，
   動作選「文字」，送出和按鈕一樣的字。有店家開放線上訂位（第 17.12 節）時，再加一個「訂位」按鈕（動作「文字」，送出「訂位」）。

### 17.2 老闆綁定碼：`coordinator-binding-code`

店家（組織）先用第 6.1 節的 `add-organization` 建立；再為它產生一組**老闆綁定碼**：

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  coordinator-binding-code --organization harvest
```

輸出範例：

```text
組織 harvest（豐收商行）的老闆綁定碼：K7PQ-3XWM
有效期限：2026-11-09 14:03（Asia/Taipei），只能使用一次。
請老闆加統籌助理的 LINE 好友後輸入這組碼。這組碼不會再顯示，遺失請重新產生。
```

- 預設 168 小時（7 天）內有效；`--hours <1-720>` 可以改，最多 720 小時（30 天）。**只能使用一次。**
- **只顯示這一次**：資料庫只存它的雜湊，遺失了就再產生一組（舊的到期自然失效）。請用安全的方式交給老闆本人。
- 老闆加統籌助理好友後直接輸入這組碼（全形、小寫、有沒有連字號或空格都可以），統籌助理回「已綁定 豐收商行（老闆）。」。
- 結束代碼：`0` 完成；`1` 找不到組織或寫入失敗；`2` 參數錯誤。統籌助理沒有啟用時也可以先產生。

### 17.3 店長綁定碼、多家店

- 老闆在統籌助理傳「**產生店長綁定碼**」，會收到一組**24 小時內有效、只能用一次**的店長綁定碼，附店名與到期時間（台北時間）；
  交給店長，店長加好友後輸入即可，統籌助理回「已綁定 {店名}（店長）。」。
- **只有老闆能產生店長綁定碼**；店長傳「產生店長綁定碼」會收到「只有老闆可以產生店長綁定碼。」。
- **同一個 LINE 帳號可以綁多家店**（每家店各輸入一組碼）。老闆綁了兩家以上時，傳「產生店長綁定碼」會先出現店名按鈕讓他選一家。
- 已經綁定某家店的人再輸入那家店的碼，統籌助理回「你已經綁定 {店名}（{角色}）。」，**那組碼不會被用掉**，可以交給別人。
- 這個版本還不能解除綁定或更改角色。

### 17.4 防猜碼、單一程序與保存的資料

- 同一個 LINE 帳號在 15 分鐘內輸入 5 次無效的碼（不存在、過期或用過），之後的嘗試一律回「嘗試太多次，請 15 分鐘後再試。」，
  連正確的碼也不會被檢查，直到最早那次失敗滿 15 分鐘。
- 和第 10.4 節一樣**只能跑一個 api 容器**：webhook 的佇列、重送去重與上面的嘗試次數都記在 api 的記憶體，重新啟動會歸零。
- 資料庫只存：**成員**（組織、統籌助理看到的 LINE 使用者 ID、老闆或店長、綁定時間）、**綁定碼**（雜湊、角色、產生與到期時間、
  使用的時間與 LINE 使用者 ID、由哪位老闆產生）、第 17.5 節的**店家設定**、第 17.6 節的**指令**（誰下的、哪家店、情境、日期與時間、
  帶入樣板後的公告、狀態、誰在何時確認）與第 17.7 節的**執行紀錄**（每個動作的種類、模式、狀態、預定與完成時間、按〔已完成〕的人、
  確認當下的手動步驟與圖片網址、失敗代碼），**不存任何人傳的訊息內容**（原因只會出現在公告文字裡）。這是系統第一次保存 LINE 的使用者 ID
  （第 10.6、16 節說「不存 LINE 的使用者 ID」指的是各店助理的 LINE 頻道），請在隱私權政策補上統籌助理的說明。

### 17.5 店家設定：`coordinator-shop`

每家店（組織）有一份**店家設定**：統籌助理顯示的**店名**、**時區**、**每週營業時間**，以及四個情境（臨時店休、提早打烊、延後開店、
恢復營業）各自**要不要提供**、**公告樣板與圖片**、**要做哪些動作**。之後的版本下指令時會照這份設定預覽與執行；
點餐系統的提醒時間（當天開店前、下一個營業日開店前）也是用這裡的營業時間算的（不看 Google 上的營業時間）。

**步驟：**

1. 複製範例檔，一家店一個檔案（`deploy/shops/` 不會進 git）：

   ```sh
   mkdir -p deploy/shops
   cp deploy/coordinator-shop.example.json deploy/shops/harvest.json
   ```

2. 照下面的說明修改 `deploy/shops/harvest.json`。
3. 匯入（把 `deploy/shops` 唯讀掛進容器）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T \
     -v "$PWD/deploy/shops:/shops:ro" api \
     coordinator-shop import --organization harvest --file /shops/harvest.json
   ```

   檔案會**先整份檢查**：有任何錯誤就逐條列出「位置：說明」（例如
   `$.weeklyHours.sun[0]：和 sat[0] 的 17:00–01:00（跨午夜） 重疊。`），**一筆都不寫入**；全部正確才寫入，
   輸出「已匯入組織 harvest（王記麵店）的店家設定。」。再匯入一次一樣的內容會說「沒有變更」，不寫任何東西。
4. 看目前的設定（印出的格式就是匯入的格式，可以存檔、修改後再匯入；要加 `-T`，否則存下來的檔案會混進終端機的控制字元）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T api \
     coordinator-shop show --organization harvest > deploy/shops/harvest.current.json
   ```

   存到**另一個檔名**：這家店還沒有設定或讀取失敗時，標準輸出是空的，`>` 會把原本的檔案清空。

結束代碼：`0` 完成；`1` 找不到組織、這家店還沒有設定（`show`）或寫入失敗；`2` 參數錯誤或設定檔不正確。
每次匯入**有變更**時，組織紀錄會記一筆 `coordinator-shop-settings-updated`，只記哪些段落、哪些情境改了，不記店名、樣板或步驟的內容。

**完整範例**（`deploy/coordinator-shop.example.json`）：週一公休；週二到週日午餐 11:00–14:00、晚餐 17:00–21:00，週六晚餐到隔天 01:00。

```json
{
  "storeName": "王記麵店",
  "timeZone": "Asia/Taipei",
  "weeklyHours": {
    "mon": [],
    "tue": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "21:00"
      }
    ],
    "wed": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "21:00"
      }
    ],
    "thu": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "21:00"
      }
    ],
    "fri": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "21:00"
      }
    ],
    "sat": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "01:00"
      }
    ],
    "sun": [
      {
        "open": "11:00",
        "close": "14:00"
      },
      {
        "open": "17:00",
        "close": "21:00"
      }
    ]
  },
  "scenarios": {
    "temporary-closure": {
      "enabled": true,
      "announcement": {
        "template": "【{店名}】{日期} 臨時店休一天，{恢復營業} 恢復營業，造成不便敬請見諒。\n原因：{原因}",
        "imageUrl": "https://example.com/images/wang-closed.jpg"
      },
      "actions": [
        {
          "kind": "line-broadcast",
          "mode": "auto",
          "steps": "LINE 官方帳號管理後台 →「群發訊息」→ 建立訊息，貼上公告文字與圖片 → 傳送。"
        },
        {
          "kind": "google-special-hours",
          "mode": "reminder",
          "steps": "Google 商家檔案 →「編輯個人檔案」→「營業時間」→「特殊營業時間」→ 加入這一天並設為「休息」（提早打烊、延後開店就改成當天的時間）。"
        },
        {
          "kind": "facebook-post",
          "mode": "reminder",
          "steps": "Facebook 粉絲專頁 →「建立貼文」→ 貼上公告文字、加入圖片 →「發佈」。"
        },
        {
          "kind": "ordering",
          "name": "Xurf",
          "mode": "reminder",
          "pauseSteps": "Xurf iPad 點左側選單 →「接單設定」→ 關閉外帶與外送的接單。",
          "resumeSteps": "Xurf iPad 點左側選單 →「接單設定」→ 打開外帶與外送的接單。"
        },
        {
          "kind": "ordering",
          "name": "foodpanda",
          "mode": "reminder",
          "pauseSteps": "foodpanda 商家 App（GO App）→「暫停接單」→ 選到今天結束。",
          "resumeSteps": "foodpanda 商家 App（GO App）→「恢復接單」。"
        }
      ]
    },
    "close-early": {
      "enabled": true,
      "announcement": {
        "template": "【{店名}】{日期} 提早到 {時間} 打烊，請見諒。\n原因：{原因}"
      },
      "actions": [
        {
          "kind": "line-broadcast",
          "mode": "auto",
          "steps": "LINE 官方帳號管理後台 →「群發訊息」→ 建立訊息，貼上公告文字與圖片 → 傳送。"
        },
        {
          "kind": "google-special-hours",
          "mode": "reminder",
          "steps": "Google 商家檔案 →「編輯個人檔案」→「營業時間」→「特殊營業時間」→ 加入這一天並設為「休息」（提早打烊、延後開店就改成當天的時間）。"
        },
        {
          "kind": "ordering",
          "name": "Xurf",
          "mode": "reminder",
          "pauseSteps": "Xurf iPad 點左側選單 →「接單設定」→ 關閉外帶與外送的接單。",
          "resumeSteps": "Xurf iPad 點左側選單 →「接單設定」→ 打開外帶與外送的接單。"
        },
        {
          "kind": "ordering",
          "name": "foodpanda",
          "mode": "reminder",
          "pauseSteps": "foodpanda 商家 App（GO App）→「暫停接單」→ 選到今天結束。",
          "resumeSteps": "foodpanda 商家 App（GO App）→「恢復接單」。"
        }
      ]
    },
    "open-late": {
      "enabled": true,
      "announcement": {
        "template": "【{店名}】{日期} 延後到 {時間} 開店，請見諒。\n原因：{原因}"
      },
      "actions": [
        {
          "kind": "line-broadcast",
          "mode": "auto",
          "steps": "LINE 官方帳號管理後台 →「群發訊息」→ 建立訊息，貼上公告文字與圖片 → 傳送。"
        },
        {
          "kind": "google-special-hours",
          "mode": "reminder",
          "steps": "Google 商家檔案 →「編輯個人檔案」→「營業時間」→「特殊營業時間」→ 加入這一天並設為「休息」（提早打烊、延後開店就改成當天的時間）。"
        },
        {
          "kind": "ordering",
          "name": "Xurf",
          "mode": "reminder",
          "pauseSteps": "Xurf iPad 點左側選單 →「接單設定」→ 關閉外帶與外送的接單。",
          "resumeSteps": "Xurf iPad 點左側選單 →「接單設定」→ 打開外帶與外送的接單。"
        },
        {
          "kind": "ordering",
          "name": "foodpanda",
          "mode": "reminder",
          "pauseSteps": "foodpanda 商家 App（GO App）→「暫停接單」→ 選到今天結束。",
          "resumeSteps": "foodpanda 商家 App（GO App）→「恢復接單」。"
        }
      ]
    },
    "reopen": {
      "enabled": true,
      "announcement": {
        "template": "【{店名}】{日期} 照常營業，先前的店休公告取消，歡迎光臨！"
      }
    }
  },
  "reservations": {
    "enabled": false,
    "slug": "wangji",
    "phone": "02-2345-6789",
    "slotMinutes": 30,
    "minLeadMinutes": 120,
    "maxDaysAhead": 30,
    "lastSeatingMinutes": 60,
    "maxPartySize": 20,
    "pendingExpiryMinutes": 60
  }
}
```

**欄位說明：**

- `storeName`：統籌助理對老闆、店長顯示的店名（綁定回覆、選店按鈕、綁定碼），最多 50 個字、一行；沒有設定的店顯示組織名稱。
- `timeZone`：選填，預設 `Asia/Taipei`；IANA 時區名稱（例如 `Asia/Tokyo`）。日期、營業時間與綁定碼的到期時間都用這個時區。
- `weeklyHours`：`mon`、`tue`、`wed`、`thu`、`fri`、`sat`、`sun` **七天都要寫**；`[]` 是固定公休。每段 `{ "open": "HH:mm", "close": "HH:mm" }`
  是 24 小時制，一天最多 4 段；**打烊時間比開店早表示跨午夜**（`17:00`–`01:00` 是到隔天凌晨 1 點，算在開店那一天）。
  開店與打烊相同、時段重疊（包含跨午夜的時段和隔天第一段重疊）、整週沒有任何時段，都不能匯入。
- `scenarios`：`temporary-closure`（臨時店休）、`close-early`（提早打烊）、`open-late`（延後開店）、`reopen`（恢復營業）**四個都要寫**；
  不提供的情境寫 `{ "enabled": false }`。
  - `announcement`：公告。`template` 最多 1000 個字，換行寫 `\n`；`imageUrl` 選填，必須是完整的 `https://` 公開圖片網址（不檢查網址連不連得上），
    沒有圖片就只發文字。LINE 自動群發時圖片要是 **JPEG 或 PNG、最大 1 MB**（第 17.8 節）。啟用的情境一定要有公告（`reopen` 除外：它的公告是選用的「更正群發」，預設不勾）。
  - `actions`：動作清單，啟用的情境至少一個；`reopen` **不能**寫 `actions`（恢復營業要做的事，由它要恢復的那次指令決定）。
    - `line-broadcast`（LINE 群發）、`google-special-hours`（Google 特殊營業時間）、`facebook-post`（FB 粉專貼文）：每個情境各最多一個；
      `mode` 是 `auto`（自動）或 `reminder`（提醒）；`steps` 必填，是提醒時（或自動做不到時）給老闆的手動步驟，最多 500 個字。
      寫 `auto` 不代表一定自動：還沒串接或還沒授權的服務，執行時會改成提醒並附上 `steps`。
    - `ordering`（點餐系統，例如 Xurf、foodpanda）：`mode` 只能是 `reminder`；`name` 最多 20 個字、同一個情境裡不能重複；
      `pauseSteps`（暫停接單）與 `resumeSteps`（恢復接單）必填，各最多 500 個字。可以有好幾個。
- `reservations`：選填，線上訂位頁（第 17.12 節）。沒有這一段就不收訂位；範例檔先寫 `"enabled": false`。
- **公告樣板的變數**（只有這五個，大括號只能用來寫變數）：

  | 變數 | 帶入 | 哪些情境能用 |
  | --- | --- | --- |
  | `{店名}` | `storeName` | 全部 |
  | `{日期}` | 指令的日期，例如 `11/2（一）` | 全部；臨時店休、恢復營業**必須**有 |
  | `{時間}` | 提早打烊的新打烊時間、延後開店的新開店時間，例如 `16:00` | 只有提早打烊、延後開店，而且**必須**有 |
  | `{恢復營業}` | 店休後下一次開店，例如 `11/3（二）11:00` | 只有臨時店休 |
  | `{原因}` | 老闆說的原因（選填） | 全部 |

  **沒有原因時，含 `{原因}` 的那一整行會省略**，所以 `{原因}` 要自己放一行（例如 `\n原因：{原因}`），不能和這個情境必須有的 `{日期}` 或 `{時間}` 放在同一行。

### 17.6 下指令、預覽卡、修改與確認（#374）

綁好店、而且那家店已經匯入第 17.5 節的設定之後，老闆或店長直接用平常的話傳給統籌助理（圖文選單的四個按鈕也可以）：

| 可以這樣說 | 情境 |
| --- | --- |
| 「今天臨時店休」「明天休息」「11/15 店休」「下週一店休，家裡有事」 | 臨時店休（整天） |
| 「今天提早四點打烊」「週六提早到 9 點收」 | 提早打烊 |
| 「明天延後到中午開」「後天晚一小時開」 | 延後開店 |
| 「恢復營業」「11/15 照常營業」 | 恢復營業（取消先前確認過的店休、提早打烊或延後開店） |

- **日期或時間不清楚時，統籌助理會問，不會猜**：例如「下週休一天」會問「下週要休哪一天？」，下面附「今天」「明天」與「選日期」按鈕；
  提早打烊沒說幾點會問「要提早到幾點打烊？」，附「選時間」按鈕。用按鈕或直接打字回答都可以（打字時會再請模型讀一次，用量一樣計入那家店）。
  問題 30 分鐘內沒回答就作廢；回答「算了」或講別的事，會取消這個指令。
- **統籌助理會再檢查一次**（不相信模型讀到的日期）：日期只能是今天到 90 天後；那天本來就是公休日會說「{日期} 本來就是公休日。」、不出預覽卡；
  提早打烊的新打烊時間、延後開店的新開店時間，要在那天第一段開店之後、最後一段打烊之前（跨午夜的時段算在開店那一天），也不能是已經過了的時間，
  否則會說明營業時間並再問一次；沒有開啟的情境會說「這家店沒有開啟「…」。」；還沒匯入設定的店一律回「這家店還沒設定店務指令，請聯絡平台營運者。」。
- **預覽卡**的內容：店名｜日期（星期）情境（提早打烊、延後開店附時間）；這個情境設定的每個動作與模式（自動／提醒——設定寫 `auto`
  而且服務已經串接、授權的才顯示「自動」：目前只有設定了權杖的 LINE 群發，第 17.8 節）；自動的 LINE 群發下面有一行額度
  （例如「約 150 則（好友 160－封鎖 10），本月剩 170 則」，第 17.8 節）；點餐系統會寫何時提醒關閉、何時提醒重新開啟
  （例如「11/2（一） 開店前提醒關閉；11/3（二）11:00 前提醒重新開啟」）；那天有線上訂位會被取消時，多一行「會取消 N 筆訂位（已接受 a 筆、待處理 b 筆）」
  與前 5 筆的時間和姓名（第 17.12 節「店休與訂位」）；帶入樣板後的公告；「已接的訂單仍需處理」；〔確認〕〔取消〕。
- **修改公告**：回覆「改成：新的公告內容」（全形或半形冒號都可以，可以換行，最多 1000 個字），統籌助理會改掉**你最近一張還沒確認的預覽卡**的公告，
  再傳一張新的卡。舊的卡就不能按了（會說「預覽卡已更新，請按最新的那張。」）。
- **確認與取消**：預覽卡在**最後一次傳出後 30 分鐘內**有效，那家店的老闆或店長都能按（不一定要是下指令的人）；沒綁那家店的人按了會被拒絕。
  同一張卡不管按幾次、幾個人同時按，都只會確認一次（之後按會說「這張預覽卡已經確認過了。」）；超過 30 分鐘按會說「已失效，請重新下指令。」。
  確認之後的執行與回報見第 17.7 節。
- **恢復營業**：只能取消**已確認**、日期是今天或之後、還沒恢復過的臨時店休、提早打烊或延後開店。只有一筆（或說了日期）就直接出預覽卡，
  有好幾筆會先問「要恢復哪一次？」，一筆都沒有會說「目前沒有可以恢復的店休或營業時間調整。」。卡上列出每個動作要怎麼還原；
  原本有 LINE 群發、而且第 17.5 節設了 `reopen` 的公告時，卡上有「加發更正公告」按鈕，預設不發，按一下改成要發（再按一次取消）。
  原本的指令取消過線上訂位時，卡上會寫「因店休取消的 N 筆訂位不會恢復，請視需要聯絡顧客」。
- **綁了好幾家店**：先問「哪一家？」並列出店名按鈕；最多 5 家時還有「全部」（每家各出一張卡，模型用量各算在自己的店）。按鈕要在 10 分鐘內按，
  超過會說「請重新輸入指令。」。選店之前不會呼叫模型。
- **模型與用量**：每次理解指令（包括打字回答問題）都呼叫那家店的對話模型一次，記錄成 `coordinator-command`，計入那家店的每月 token 上限；
  已達上限時回「抱歉，這家店本月的 AI 用量已達上限，暫時無法理解指令，請聯絡平台營運者。」，不會呼叫模型；模型出錯時回「抱歉，現在無法理解指令，請稍後再試。」。
  紀錄只寫錯誤種類，不寫任何人傳的訊息或模型的回答。
- 和第 17.4 節一樣**只能跑一個 api 容器**：「哪一家？」等待中的訊息、等待日期時說過的原因與問題只放在記憶體 10–30 分鐘，重新啟動就忘了
  （要重打一次指令；卡上的公告可能少了原因那一行，可以用「改成：」補上）。

### 17.7 確認之後：執行、回報與提醒（#375）

按下預覽卡的〔確認〕，統籌助理會照**那一刻的店家設定**把這個指令要做的每個動作記下來（之後再改設定，不會影響已經確認的指令），
並馬上回一則**執行報告**，每個動作一行：

| 標示 | 意思 |
| --- | --- |
| ✅ | 已完成（自動完成，或有人按了〔已完成〕） |
| ❌ | 自動執行失敗，附原因與〔重試〕 |
| ⏳ | 請你現在手動處理：附手動步驟（第 17.5 節的 `steps`）與〔已完成〕 |
| ⌛ | 自動執行中，結果稍後另外傳 |
| 🕒 | 到時間會提醒你，例如「11/2（一）10:30 會提醒你關閉接單」 |
| 🚫 | 恢復營業時取消了還沒發生的排程 |

那天的線上訂位被取消時（第 17.12 節「店休與訂位」），標題下面多一行「已取消 N 筆訂位，其中 k 筆會寄信通知顧客。」，最後多一項
「⏳ 打電話通知顧客訂位已取消」，列出要打電話的顧客（時間、姓名、電話）與〔已完成〕。

- **目前 LINE 群發（設定了店家權杖時，第 17.8 節）、FB 粉專貼文（老闆授權了粉專時，第 17.10 節）與 Google 特殊營業時間（老闆授權了商家時，
  第 17.11 節）會自動執行**。自動執行的動作先顯示 ⌛，全部跑完後統籌助理**再傳一則「執行結果」**（✅／❌／⏳）。
  自動執行時服務沒授權或授權失效，那一項會改成提醒（⏳），並寫出原因（例如「LINE 權杖失效，已改為手動；請平台營運者重新設定權杖」）。
- LINE 群發或 FB 貼文要手動發時，報告後面會**另外附一則只有公告文字的訊息**，長按就能複製；有設定圖片時，報告裡會附圖片網址。
- **點餐系統（`ordering`）在開店前 30 分鐘提醒**（推播，附手動步驟與〔已完成〕；已經過了就馬上提醒）：

  | 情境 | 提醒關閉接單 | 提醒重新開啟接單 |
  | --- | --- | --- |
  | 臨時店休 | 當天第一段開店前 30 分鐘 | 之後第一個有營業的日子，第一段開店前 30 分鐘（跳過固定公休日；當天跨午夜的時段算在店休那天） |
  | 提早打烊 | 新打烊時間前 30 分鐘 | 同上（當天晚一點的時段不會再開） |
  | 延後開店 | 當天第一段開店前 30 分鐘 | 新開店時間前 30 分鐘 |

  例如週一 10:00 說「今天臨時店休」、營業時間 11:00 開始：10:30 提醒關閉，下一個營業日 10:30 提醒重新開啟。
  提醒送給**按〔確認〕的那個人**；他已經不是這家店的成員時不送。
- **〔已完成〕**：那家店的老闆或店長都能按，統籌助理回「已記錄：{項目} 已完成。」並記下是誰、什麼時候按的；再按一次只會說「這一項已經記錄完成了。」。
  沒綁這家店的人按了會被拒絕。
- **〔重試〕**：只會重做**失敗的那一項**，已經成功的項目不會重做，也不會因為某一項失敗就回復其他項目。按下去先不回話，重做完再回結果
  （50 秒內做完用 reply，超過就改用推播）。
- **恢復營業**（第 17.6 節）確認後：原本指令裡**還沒發生的排程一律取消**（例如還沒到時間的「提醒關閉接單」，報告標 🚫；失敗的項目也取消，不能再重試）；
  已經做了的**Google 特殊營業時間、FB 貼文**要還原——自動做的由系統刪除（串接版本），手動做的或還在等你做的，會給一則還原提醒（例如「到 Google 商家檔案刪除 11/15（日） 的特殊營業時間。」）；
  **已經提醒過關閉接單**的點餐系統，馬上提醒重新開啟（還沒提醒過的就只是取消）；LINE 群發發出去就收不回，有勾「加發更正公告」時才多一項群發更正公告
  （原本是自動群發、權杖仍有效時，更正公告也自動群發，內容是 `reopen` 的公告）。
  **已取消的線上訂位不會恢復**，報告會寫「因店休取消的 N 筆訂位不會恢復，請視需要聯絡顧客。」；「打電話通知顧客訂位已取消」那一項也不會取消。
- **推播的額度**：到時間的提醒與事後的執行結果用推播，算在**統籌助理官方帳號**的每月訊息額度（平台負擔，不是各店自己的帳號）。
  一次臨時店休、一個點餐系統大約是 2 則推播（關閉、重新開啟）；店家多的時候請在 LINE Official Account Manager 選夠用的方案。推播失敗只記在 api 的紀錄裡，那一項維持「等待手動」。
- 排程放在資料庫的背景工作佇列，api 重新啟動不會遺失；〔重試〕的 reply token 只放在記憶體，重新啟動後結果改用推播。

**查執行紀錄：`coordinator-commands`**

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T api \
  coordinator-commands --organization harvest --limit 5
```

輸出範例：

```text
組織 harvest（王記麵店）最近 1 筆確認的指令（時間為 Asia/Taipei）：

2026-11-02 10:01 確認｜11/2（一） 臨時店休｜確認者 …3f9a｜指令 0194a100-0000-7000-8000-0000000000d1
  1. line-broadcast｜apply｜reminder｜done-manually｜預定 2026-11-02 10:01｜完成 2026-11-02 10:05（…3f9a）
  2. google-special-hours｜apply｜reminder｜awaiting-manual｜預定 2026-11-02 10:01
  3. ordering Xurf｜pause｜reminder｜scheduled｜預定 2026-11-02 10:30
  4. ordering Xurf｜resume｜reminder｜scheduled｜預定 2026-11-03 10:30
  5. reservation-calls｜apply｜reminder｜awaiting-manual｜預定 2026-11-02 10:01
```

（第 5 項 `reservation-calls` 是系統自己加的「打電話通知顧客訂位已取消」，只有取消了線上訂位、而且有顧客收不到 Email 時才有；不列顧客的資料。）

- 列出最近確認的指令（預設 20 筆、`--limit` 最多 200，最新的在前）：情境、日期、誰在何時確認，以及每個動作的種類、步驟
  （`apply` 執行、`undo` 還原、`pause` 關閉接單、`resume` 重新開啟接單）、模式（`auto`／`reminder`）、狀態（`scheduled` 排程中、
  `succeeded` 成功、`failed` 失敗（括號是失敗代碼）、`awaiting-manual` 等待手動、`done-manually` 已手動完成、`cancelled` 已取消）、
  預定時間與完成時間。恢復營業的指令會多一行「恢復的指令 …」。
- **不印公告內容**；LINE 使用者 ID 只顯示最後 4 碼。時間用店家設定的時區。
- 結束代碼：`0` 完成；`1` 找不到組織或讀取失敗；`2` 參數錯誤。

### 17.8 LINE 群發自動執行：店家自己的官方帳號權杖（#376）

每家店的公告要群發給**那家店自己的 LINE 官方帳號**的好友（不是統籌助理這個帳號）。平台營運者替那家店設定一次
**長期 channel access token**，之後設定為 `auto` 的 LINE 群發就會在〔確認〕後自動群發；沒有設定權杖的店照舊是提醒。

**取得權杖（店家或營運者在 LINE 後台操作）：**

1. 用店家的帳號登入 [LINE Official Account Manager](https://manager.line.biz/)，選那家店的官方帳號 →「設定」→「Messaging API」，
   還沒啟用就按「啟用 Messaging API」（選或建立一個 provider）。
2. 到 [LINE Developers Console](https://developers.line.biz/console/) → 那個 provider → 那家店的 channel →「Messaging API」分頁，
   最下面「Channel access token (long-lived)」按〔Issue〕，複製權杖。**不要**用統籌助理（第 17.1 節）那個 channel 的權杖——
   設定時會被拒絕，因為那樣會群發給所有店的老闆。
3. 把權杖存成一個只有一行的檔案（例如 `token.txt`，不要放進 git，設定完就刪掉）。

**設定權杖：`coordinator-shop line-token`**（權杖從**標準輸入**讀，不要寫在指令參數裡，免得留在 shell 歷史紀錄）：

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T api \
  coordinator-shop line-token --organization harvest < token.txt
rm token.txt
```

- 統籌助理會先用這個權杖向 LINE 查帳號資料（`GET /v2/bot/info`）：LINE 不接受（打錯、已重發、已撤銷）就說
  「LINE 不接受這個權杖（HTTP 401）…沒有寫入。」；成功才**加密保存**（和第 10 節的頻道金鑰一樣用資料保護金鑰，只能寫、不能讀出），
  輸出「已設定組織 harvest（王記麵店）的 LINE 官方帳號：王記麵店（@123abcde）。設為 auto 的 LINE 群發之後會自動群發。」。
  **輸出、錯誤訊息與紀錄都不會出現權杖。**`-T` 一定要加，標準輸入才會導進容器。
- 換權杖：用新的檔案再跑一次（會取代舊的）。清除：`coordinator-shop line-token --organization harvest --clear`，之後 LINE 群發改回提醒。
- 每次設定或清除會記一筆組織紀錄（`coordinator-connection-updated`，只記 `line` 與 `set`／`cleared`）。重新匯入店家設定（第 17.5 節）不會動到權杖。
- 結束代碼：`0` 完成；`1` 找不到組織、LINE 不接受權杖或寫入失敗；`2` 參數錯誤、沒有讀到權杖或權杖格式不對（不是一行、含空白、超過 1000 字）。
- 資料保護金鑰遺失或換掉（第 14 節）時，保存的權杖解不開，LINE 群發會改回提醒並寫出「LINE 權杖失效…」；重新設定一次即可。

**預覽卡上的額度：**自動的 LINE 群發下面會有一行（第 17.6 節），數字來自店家帳號的 LINE API：

| 卡上顯示 | 意思 |
| --- | --- |
| 約 150 則（好友 160－封鎖 10），本月剩 170 則 | 這次大約用掉「好友數－封鎖數」則；本月剩「本月上限－已用量」則 |
| 約 150 則（好友 160－封鎖 10），本月不限則數 | 帳號沒有設定每月上限 |
| 則數無法估計（LINE 尚未提供好友數），本月剩 170 則 | LINE 還沒算好好友數（照樣會群發） |
| 額度不足，無法群發：約 150 則…，本月剩 100 則 | 估計的則數比剩下的多（或本月已經用完）：確認後**不會群發**，回報 ❌「LINE 本月剩餘額度不足，沒有群發」 |
| 暫時查不到 LINE 本月額度 | 查額度時 LINE 沒有回應；確認後執行時會再查一次 |

- 好友數用的是 LINE 的好友統計（日本時間的**昨天**，還沒算好就用前天）；它把曾經加入又刪除的帳號也算進去，所以是**偏高的估計**。
  本月已用量包含在 LINE Official Account Manager 手動群發的則數，也是 LINE 給的約略值。
- 這些數字**快取 10 分鐘**（LINE 的好友統計每小時只能查 60 次）；群發成功或重新設定權杖後會重新查。

**執行（按〔確認〕之後）：**

- 群發的內容是預覽卡上的公告（經「改成：」修改後的版本），有 `imageUrl` 時再加一則圖片訊息，**原圖與預覽圖用同一個網址**，所以圖片必須是
  **HTTPS、JPEG 或 PNG、最大 1 MB**（LINE 預覽圖的上限）、網址最長 2000 字。LINE 送出時不會先下載圖片檢查：網址本身不合格時 LINE 拒絕
  （❌「服務拒絕了這次操作」），但圖片太大、格式不對或連不上時照樣送出，好友只會看到破圖——設定前請自己用手機打開網址確認。
  群發會發出通知（不是靜音）。
- 群發前**再查一次額度**：估計的則數超過本月剩下的，就不群發，回報 ❌「LINE 本月剩餘額度不足，沒有群發」，可以加購方案後按〔重試〕。
- 執行結果：✅ 已群發；❌「LINE 本月訊息額度已用完」（LINE 拒絕，什麼都沒送出）、❌「服務忙碌中」（LINE 每小時 60 次的群發上限）、
  ❌「LINE 暫時無法使用」（沒回應或 LINE 出錯）——都可以按〔重試〕；⏳「LINE 權杖失效，已改為手動；請平台營運者重新設定權杖」——
  LINE 不接受權杖（被撤銷或重發）：這家店的連線標成失效、這一則改成手動（附公告文字可以複製），之後的預覽卡顯示「提醒」，直到營運者重新設定權杖。
- **不會重複群發**：每一則群發都帶 LINE 的 `X-Line-Retry-Key`（用那一項動作的 ID），24 小時內不管是背景工作重跑還是按〔重試〕，
  LINE 已經收過的就不會再送（LINE 回 409，統籌助理當作已群發）。超過 24 小時才重試的話，LINE 會當成新的群發。
- 恢復營業勾了「加發更正公告」時，更正公告（`reopen` 的公告）也用同一個權杖群發，一樣會算額度。
- 群發用的是**店家自己帳號**的每月訊息額度（依好友人數計算；台灣的輕用量方案每月 200 則），不是統籌助理的額度。

### 17.9 FB、Google 授權連結（#377）

FB 粉專與 Google 商家只能由**店家老闆本人**授權（設計問答 Q12）。平台營運者為那家店產生一個**授權連結**傳給老闆，老闆用手機或電腦打開、
登入自己的 FB 或 Google 帳號並按同意，就完成授權；統籌助理把拿到的憑證**加密保存**（和第 17.8 節的 LINE 權杖一樣，只能寫、不能讀出）。
FB 的授權與自動貼文見第 17.10 節（#379）；Google 商家的授權與 Google 特殊營業時間的自動執行見第 17.11 節（#378）。

**平台設定（一次）**：

| 服務 | `.env` | 在服務那邊要登記的重新導向網址（redirect URI） |
| --- | --- | --- |
| FB 粉專 | `COORDINATOR_FACEBOOK_APP_ID`、`COORDINATOR_FACEBOOK_APP_SECRET`：Meta 應用程式（設計文件附錄 A.1，類型「商家」）的應用程式編號與密鑰 | `{PUBLIC_BASE_URL}/api/v1/coordinator/oauth/facebook/callback`，填在「Facebook 登入（企業版）→ 設定 → 有效的 OAuth 重新導向 URI」 |
| Google 商家 | `COORDINATOR_GOOGLE_CLIENT_ID`、`COORDINATOR_GOOGLE_CLIENT_SECRET`：Google Cloud「憑證」裡「網頁應用程式」類型的 OAuth 用戶端（設計文件附錄 A.2） | `{PUBLIC_BASE_URL}/api/v1/coordinator/oauth/google/callback`，填在那個用戶端的「已授權的重新導向 URI」 |

- 例如 `https://assistant.example.org/api/v1/coordinator/oauth/facebook/callback`。網址要**一字不差**（https、沒有結尾的 `/`），
  否則服務會拒絕導回。`PUBLIC_BASE_URL` 一定要設，而且要是 **https**（授權時用的 cookie 只走 HTTPS；開發用的 `http://localhost` 例外）。
- 每一組的兩個值都是平台的設定，不屬於任何組織，不寫入資料庫、紀錄或回應；只填一個時 api 拒絕啟動並說缺哪一個（不會印出值）。
- 反向代理（第 5 節）整個網址都轉給 api，所以 `/api/v1/coordinator/connect` 與 `/api/v1/coordinator/oauth/*` 不需要另外設定；
  如果你的代理只轉特定路徑，要把這兩個也加進去。

**產生授權連結：`coordinator-shop auth-link`**

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  coordinator-shop auth-link --organization harvest --service facebook
```

- `--service` 是 `facebook` 或 `google`；`--hours` 選填，預設 72 小時、最多 168 小時。輸出那家店的名稱、**連結本身**（只顯示這一次，
  資料庫只存它的雜湊）與有效期限，例如：

  ```text
  組織 harvest（王記麵店）的FB 粉專授權連結：
  https://assistant.example.org/api/v1/coordinator/connect?token=…
  有效期限：2026-11-05 10:00（Asia/Taipei），只能使用一次。請傳給老闆本人，…
  ```
- 連結**只能完成一次授權**，遺失或過期就再產生一個（舊的會自己過期）。打開連結本身不會用掉它：老闆在服務的畫面按「取消」，可以再打開同一個連結重來。
- 結束代碼：`0` 完成；`1` 找不到組織、這個部署沒有設定那個服務或 `PUBLIC_BASE_URL`、寫入失敗；`2` 參數錯誤。

**老闆看到的畫面**（都是很簡單的繁體中文頁面，不留快取、不帶 referrer）：

1. 打開連結 → 直接轉到 FB 或 Google 的同意畫面（帶著一個 30 分鐘內有效、加密簽章的 `state`，並在瀏覽器存一個只給回呼用的 cookie）。
2. 按同意 → 回到統籌助理：「授權完成：{店名} 已連結FB 粉專：{粉專名稱}。」；一個帳號有好幾個粉專或營業地點時，先出現「選擇要連結的粉專／營業地點」，選一個按〔連結〕。
3. 其他情況只會看到一句話（不顯示細節）：
   - 「連結已失效，請向平台營運者索取新的連結。」——連結不存在、已經用過、過期，或那個服務在這個部署沒有設定。
   - 「已取消授權」——老闆在服務的畫面按了取消（連結沒有用掉，可以再打開）。
   - 「授權沒有完成…」——超過 30 分鐘才回來、換了瀏覽器或清掉 cookie、網址被改過、服務換憑證失敗等；頁面最多附一個簡短代碼。
4. 授權完成會記一筆組織紀錄（`coordinator-connection-updated`，只記 `facebook`／`google` 與 `set`）。重新授權會取代原本的憑證。

**授權失效與重新授權**：FB 或 Google 拒絕保存的憑證（老闆改了密碼、移除了應用程式、撤銷授權）時，那一項動作改成提醒
（執行結果寫「FB 粉專授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權」），連線標成失效，統籌助理**自動產生一個 72 小時的新連結**，
用推播傳給那家店**每一位老闆**（店長不會收到，只有老闆能授權）：

```text
王記麵店 的 FB 粉專 授權已失效，相關動作先改為提醒。請點此重新授權：https://assistant.example.org/api/v1/coordinator/connect?token=…
```

- **每次失效只傳一次**：之後其他動作再失敗也不會一直推播；老闆完成重新授權後，下一次失效才會再傳。新連結過期沒用的話，請用 `auth-link` 再產生一個。
- 推播算在統籌助理這個官方帳號的額度（平台負擔）。LINE 群發的權杖不走這個流程，仍由營運者用 `line-token` 重新設定（第 17.8 節）。

**頻率限制**：打開連結、服務的回呼與選擇表單三者合計，每個用戶端 IP 每分鐘最多 20 次（一次授權只用兩到三次），超過回 `429`
（一分鐘後再試）。要調整就設 `PublicChannels__RateLimits__CoordinatorAuthorizationRequestsPerIpPerMinute`（第 3 節「頻率限制」的做法）；
代理後面一定要設 `TRUSTED_PROXIES`（第 5 節），否則所有老闆共用代理的一個 IP。

**保存的資料與紀錄**：資料庫只存連結的雜湊、有效期限與是否用過；憑證加密保存。連結、服務給的 code、`state`、憑證都不會出現在 api 的紀錄、
API 回應或指令輸出（`auth-link` 只印一次連結本身）。注意兩件事：

- **不要把 `Logging__LogLevel__Microsoft.AspNetCore` 調到 `Warning` 以下**：ASP.NET Core 的請求紀錄會寫出完整網址（含查詢字串），
  也就會寫出連結與服務回傳的 code。預設設定（`appsettings.json`）是 `Warning`。
- 反向代理的 access log 預設也會記完整網址。連結只能用一次、最多 7 天，code 只能換一次，但仍建議別把代理的 access log 公開或長期保存。

### 17.10 FB 粉專貼文自動執行（#379）

情境的動作清單裡寫 `{ "kind": "facebook-post", "mode": "auto", … }`（第 17.5 節），而且那家店的老闆用授權連結選好了粉專（第 17.9 節），
確認指令後統籌助理就會**自動發到粉專**；恢復營業時**自動刪掉那則店休貼文**（設計問答 Q3、Q21）。

**Meta 應用程式（平台設定一次，設計文件附錄 A.1）**：

1. 在 developers.facebook.com 建立應用程式，類型選「商家」，綁定平台營運者的企業管理平台；加入產品「Facebook 登入（企業版）」，
   並填好隱私權政策、服務條款、資料刪除說明的網址與應用程式圖示。
2. 「Facebook 登入（企業版）→ 設定 → 有效的 OAuth 重新導向 URI」填 `{PUBLIC_BASE_URL}/api/v1/coordinator/oauth/facebook/callback`（一字不差）。
3. 權限是 **`pages_show_list`、`pages_manage_posts`、`pages_read_engagement`**（Graph API 文件對 `POST /{粉專編號}/photos` 的要求；
   2026-11-02 查證）。預設由統籌助理在授權畫面直接要求這三個權限；如果你偏好 Meta 建議的「設定（configuration）」做法：在
   「Facebook 登入（企業版）→ 設定」建立一個設定，**權杖類型選「用戶存取權杖」**、勾這三個權限，把它的編號填進
   `COORDINATOR_FACEBOOK_CONFIG_ID`，授權畫面就改用那個設定（`config_id`，不再帶 `scope`）。
4. `.env` 填 `COORDINATOR_FACEBOOK_APP_ID`、`COORDINATOR_FACEBOOK_APP_SECRET`（「應用程式設定 → 基本資料」的應用程式編號與密鑰），
   重新啟動 api。統籌助理每次呼叫 Graph API 都會帶 `appsecret_proof`，所以可以放心在「應用程式設定 → 進階 → 安全性」開啟
   「需要應用程式密鑰」。
5. Graph API 固定用 **v26.0**（2026-07-29 發布，查證當時最新的版本）。Meta 會在版本到期前寄信通知，屆時要更新 api。

**開發模式、App Review 與審核通過之前**：

- 應用程式在**開發模式**時，只有在應用程式裡有角色的人（管理員、開發者、測試人員）能完成授權，也只看得到他管理的粉專。用這個方式
  先跑一次完整流程並錄影：把你自己或老闆加為測試人員、準備一個測試粉專 →
  `coordinator-shop auth-link --organization <組織代碼> --service facebook` → 打開連結、選粉專、同意 → 在統籌助理說「今天臨時店休」
  並〔確認〕→ 粉專出現貼文 → 說「今天恢復營業」並〔確認〕→ 貼文被刪除。
- 送 **App Review**：三個權限各寫用途說明，附上上一步的錄影。這些權限的進階存取需要先完成**商家驗證**（附錄 A.1 第 2 步）；
  「Facebook 登入（企業版）」上線前也需要 `public_profile` 的進階存取。通過後把應用程式切到「上線」模式。
- **通過之前**：不在應用程式角色裡的老闆無法完成授權，那家店的 FB 貼文維持提醒（手動步驟＋可以複製的公告文字）。授權了、但 FB
  回「權限不足」（例如授權時沒有允許發文）時，那一項也改成提醒，原因寫「FB 權限不足（應用程式審核未通過或未授權發文），先以提醒處理」；
  授權本身不受影響，不會推播重新授權的連結。

**授權時連結哪個粉專**：統籌助理把 FB 給的短期權杖換成**長期使用者權杖**，再讀出老闆的粉專，只算他能「建立內容」的粉專：

- 剛好一個 → 直接連結，「授權完成：{店名} 已連結FB 粉專：{粉專名稱}。」
- 好幾個 → 先讓老闆選一個（「選擇要連結的粉專」）。
- 一個都沒有 → 「這個 Facebook 帳號沒有可以發文的粉專…」（代碼 `facebook-no-pages`）：請老闆確認自己是粉專的管理者、授權時有勾那個粉專，
  再用 `auth-link` 產生新的連結重來。
- 保存的是**那個粉專的權杖**（加密，只寫不讀；由長期使用者權杖換得，不會過期，但老闆改密碼、移除應用程式、不再是粉專管理者時會失效——
  那時走第 17.9 節的重新授權）。使用者權杖不保存。其他代碼：`facebook-permission`（沒有允許讀取粉專清單）、`facebook-unavailable`
  （FB 暫時無法使用）、`facebook-exchange`（FB 不接受這次授權）。

**發文的內容**：那次指令的公告（設計問答 Q13）。情境有 `imageUrl` 時發**圖文貼文**（圖片＋公告文字），沒有就發**純文字貼文**。圖片網址
要是公開、FB 下載得到的 HTTPS 網址，JPG 或 PNG，4MB 以內（PNG 建議 1MB 以內）。

**什麼時候算「自動」**：預覽卡寫「FB 粉專貼文（自動）」要三個條件都成立——設定寫 `auto`、這個部署設定了 FB 的應用程式（上面第 4 步）、
那家店有有效的 FB 授權。少一個就是「（提醒）」，執行時也照預覽卡做。

**執行結果**：

| 情況 | 結果 |
| --- | --- |
| 發文成功 | ✅ FB 粉專貼文 |
| FB 拒絕權杖（改密碼、移除應用程式、撤銷授權、失去粉專角色） | ⏳ 改成提醒並附公告文字；統籌助理推播重新授權連結給每一位老闆（第 17.9 節） |
| 權限不足（審核未通過、沒有允許發文） | ⏳ 改成提醒：「FB 權限不足（應用程式審核未通過或未授權發文），先以提醒處理」 |
| FB 限流 | ❌「服務忙碌中」〔重試〕 |
| FB 故障或連不上 | ❌「FB 暫時無法使用」〔重試〕 |
| FB 讀不到圖片 | ❌「FB 讀不到公告圖片（圖片網址要能公開下載，JPG 或 PNG、4MB 以內）」——先修好設定檔的 `imageUrl` 再 `import`，然後〔重試〕 |
| FB 沒有及時回應 | ❌「FB 沒有及時回應，貼文可能已經發出；按〔重試〕前請先到粉專確認」 |
| 其他錯誤 | ❌「服務拒絕了這次操作」〔重試〕 |

- **不會重複發文**：FB 一回貼文編號，統籌助理就先把它記在那一項上；同一項再執行一次（背景工作重跑、〔重試〕）看到編號就不再發。
  **剩下的風險**：FB 已經收下貼文、回應卻沒傳回來（逾時）——這時沒有編號可記，FB 也沒有防重複的機制，按〔重試〕會再發一次；所以那一項的原因
  才請你先到粉專看看：已經發出的話就不要重試，而且恢復營業時這則貼文**不會自動刪除**，要自己刪。
- **恢復營業**刪除的是當初記下編號的那則貼文。老闆已經自己刪掉的也算完成（✅）。只刪「目前連結的粉專」上的貼文：之後換連結了別的粉專，
  就改成提醒你到原本的粉專手動刪除。

**紀錄**：api 的紀錄只寫動作編號、粉專與貼文編號、FB 回的 HTTP 狀態、錯誤代碼與 `fbtrace_id`（向 Meta 回報問題時用），不寫權杖、
應用程式密鑰、`appsecret_proof`、公告內容或 FB 的錯誤訊息文字。呼叫 Graph API 時權杖照文件放在參數裡（`POST` 在內容、`GET`／`DELETE`
在網址的查詢字串）；api 不記錄這些網址，追蹤（OpenTelemetry）裡的網址查詢值也會被遮蔽。

### 17.11 Google 特殊營業時間自動執行（#378）

客人最常在 Google 地圖查「現在有沒有開」。設定為 `auto` 的 `google-special-hours`，會在〔確認〕後直接改那家店 Google 商家檔案的
**特殊營業時間**（不是「暫停營業」，設計問答 Q3、Q21）：

| 情境 | Google 上那一天變成 |
| --- | --- |
| 臨時店休 | 整天「休息」 |
| 提早打烊 T | 那天原本的時段，T 以前的照舊、跨過 T 的那一段在 T 結束（例如 11:00–14:00、17:00–21:00 提早到 19:00 → 11:00–14:00、17:00–19:00） |
| 延後開店 T | T 以後的時段照舊、跨過 T 的那一段從 T 開始（例如 12:00 開 → 12:00–14:00、17:00–21:00） |
| 恢復營業 | 刪除那一天的特殊營業時間（回到 Google 的一般營業時間） |

- 時段**一律依第 17.5 節店家設定的每週營業時間**計算（設計問答 Q22），不看 Google 上的一般營業時間。跨午夜的時段（例如週六 17:00–01:00）
  寫成「到隔天 01:00」；在凌晨那一段才延後開店的話，那一天在 Google 上會是休息（只能動那一天，不能動到隔天）。
- **其他日期的特殊營業時間完全不動**：統籌助理先讀出那個地點現有的特殊營業時間，只換掉那一天的、其他日期原樣寫回（Google 一次只能整份覆蓋）。
  那一天原本已經有特殊營業時間（例如老闆自己設過）會先被取代，統籌助理記下原本那幾筆；**恢復營業時還原成原本那幾筆**，
  原本沒有就整天移除。原本的設定太長記不下時（例如一天好幾個時段），恢復營業時改成整天移除，api 紀錄會寫一筆警告。
- 前提：那個地點**要先有一般營業時間**（Google 的規定）。沒有的話這一項改成提醒（附手動步驟與〔已完成〕），執行結果寫
  ⏳「Google 商家沒有設定一般營業時間，無法自動設定特殊營業時間，先以提醒處理」；請老闆到 Google 商家檔案補上一般營業時間，下一次就會自動設定。

**平台設定（一次，設計文件附錄 A.2）：**

1. 在 [Google Cloud Console](https://console.cloud.google.com/) 建立專案（記下專案編號），用設計文件附錄 A.2 的條件填
   「Basic API Access」申請表（GBP API contact form → Application for Basic API Access）。
2. 「API 和服務 → 程式庫」啟用 **My Business Account Management API** 與 **My Business Business Information API**
   （Google 的設定說明要你把 Business Profile 的 API 全部啟用；統籌助理只用這兩個）。
3. 看申請有沒有通過：「API 和服務 → 已啟用的 API → My Business Business Information API → 配額」：**每分鐘 0 次＝還沒通過**，300 次＝通過。
4. 「API 和服務 → OAuth 同意畫面」：使用者類型「外部」，填應用程式名稱、支援 email、隱私權政策與服務條款網址，加入範圍
   `https://www.googleapis.com/auth/business.manage`。**發布狀態要改成「正式版」**：停在「測試」時只有測試使用者能授權，而且授權**7 天就失效**。
   對外使用前，依 Google 的規定確認這個範圍是否要送 OAuth 應用程式驗證（附錄 A.2 第 6 步）。
5. 「API 和服務 → 憑證 → 建立憑證 → OAuth 用戶端 ID」，類型「網頁應用程式」，「已授權的重新導向 URI」填
   `{PUBLIC_BASE_URL}/api/v1/coordinator/oauth/google/callback`（第 17.9 節的表格），把用戶端 ID 與密鑰填進 `.env` 的
   `COORDINATOR_GOOGLE_CLIENT_ID`、`COORDINATOR_GOOGLE_CLIENT_SECRET`，重新啟動 api。

**讓老闆授權：**`coordinator-shop auth-link --organization harvest --service google`（第 17.9 節）。老闆用**管理那家店 Google 商家檔案的帳號**
登入並按同意：

- 只管理一個地點：直接「授權完成：王記麵店 已連結Google 商家：{地點名稱}。」
- 管理好幾個地點（可能分屬不同帳號或地點群組）：先選「{地點名稱}（{地址}）」再按〔連結〕。
- 「這個 Google 帳號沒有可管理的商家」：老闆登入的帳號不是那家店商家檔案的擁有者或管理員。連結在 Google 同意之後就算用掉了，請重新產生一個，讓老闆換對的帳號授權。
- 「Google 商家 API 尚未開通…」：步驟 3 的配額還是 0，**審核通過前無法授權**；Google 的動作維持提醒。通過後重新產生連結。
- 「你沒有勾選「管理商家檔案」的權限…」：老闆在 Google 的畫面取消勾選了那個權限；重新產生連結，請老闆授權時勾選。
- 統籌助理保存的是 Google 給的 **refresh token**（加密保存，只能寫、不能讀出）；每次執行時才用它換一個短效的存取權杖，存取權杖不保存。

**執行結果：**

- ✅「Google 特殊營業時間：11/2（一） 休息」等；恢復營業是「Google 特殊營業時間：移除 11/2（一） 的設定」（有原本的設定時是還原它）。
  重跑或〔重試〕寫進去的內容都一樣，不會重複。
- ❌「服務忙碌中」（Google 的頻率限制：專案每分鐘 300 次、**每個商家檔案每分鐘 10 次修改**）、❌「Google 暫時無法使用」（沒回應、連不上或
  Google 出錯）、❌「服務拒絕了這次操作」（Google 不接受這次的時段，或地點已被刪除）——都可以按〔重試〕。
- ⏳「Google 商家 API 尚未開通，先以提醒處理」：平台專案的配額是 0 或 API 沒有啟用；改成手動，附手動步驟與〔已完成〕。
- ⏳「Google 商家沒有設定一般營業時間，無法自動設定特殊營業時間，先以提醒處理」：見上面的前提。
- ⏳「Google 商家授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權」：老闆撤銷了授權、換了帳號權限、應用程式還在「測試」而過了 7 天，
  或資料保護金鑰換掉而解不開保存的憑證。連線標成失效，統籌助理推播一個新的授權連結給每位老闆（第 17.9 節），之後的預覽卡顯示「提醒」。
- api 的紀錄只寫動作 ID、Google 回的 HTTP 狀態與錯誤代碼（例如 `RATE_LIMIT_EXCEEDED`），不寫權杖、用戶端密鑰或地點的營業時間內容。

### 17.12 線上訂位頁與通知（#389、#390、#391、#392）

每家店可以有一個**免登入的訂位網頁** `{PUBLIC_BASE_URL}/reserve/{代號}`（線上訂位設計文件 `docs/plans/2026-11-06-store-reservations.md`）：
顧客選日期、時間、人數，填姓名、電話（必填）、Email 與備註（選填）就送出；頁面是 api 直接產生的繁體中文頁面，手機優先，
**不需要 JavaScript**（有 JavaScript 時，換日期會直接換成那天的時間），不留快取、不帶 referrer、不載入任何外部資源。

新訂位會用 LINE 卡片通知這家店的老闆與店長，由他們按〔接受〕或〔婉拒〕（#390，見下面「老闆與店長收到的通知」）。顧客有留 Email、
平台也設定了寄信伺服器（SMTP，第 16 節）時，訂位被接受、婉拒、過期或因店休取消後，系統會**自動寄信通知顧客**（#391，見下面「顧客收到的 Email」）。
之後宣布臨時店休、提早打烊或延後開店時，那天受影響的訂位會列在預覽卡上，確認後取消並通知顧客（#392，見下面「店休與訂位」）。
SMTP 是選用的：沒有設定時訂位頁不顯示 Email 欄位，完成頁寫「店家會來電與您確認」，老闆與店長收到的卡片與按鈕回覆都會請他們來電。

> 開放前建議先開一家測試店、用自己的 Email 訂一筆，看卡片有沒有送到每位成員、按〔接受〕後有沒有收到確認信（也看一下垃圾郵件匣），
> 再用下面的 `coordinator-reservations list` 確認。

**開啟訂位**：在第 17.5 節的設定檔加上 `reservations`，再 `coordinator-shop import`：

```text
"reservations": {
  "enabled": true,
  "slug": "wangji",
  "phone": "02-2345-6789"
}
```

| 欄位 | 說明 | 預設 |
| --- | --- | --- |
| `enabled` | 是否開放訂位。`false` 時訂位網址和不存在的代號顯示同一個「找不到這家店的訂位頁」。 | 必填 |
| `slug` | 網址裡的代號：3–40 個小寫英文字母、數字或 `-`，不能以 `-` 開頭或結尾，**全平台唯一**（已經是別家店的就不能匯入）。開放時必填。 | — |
| `phone` | 店家電話，顯示在訂位頁、完成頁與寄給顧客的通知信，讓顧客來電更改或取消。開放時必填。 | — |
| `slotMinutes` | 每隔幾分鐘一個可選時間，從每段營業時間的開店時間算起：`10`、`15`、`20`、`30` 或 `60`。 | `30` |
| `minLeadMinutes` | 最晚要在幾分鐘前訂位（1 到 10080）。 | `120` |
| `maxDaysAhead` | 最多可以訂到今天之後幾天（1 到 90）。 | `30` |
| `lastSeatingMinutes` | 打烊前幾分鐘停止接受訂位（0 到 720）：21:00 打烊、`60` 的話最晚可訂 20:00。 | `60` |
| `maxPartySize` | 一筆訂位最多幾位（1 到 100）；更多人頁面請顧客來電。 | `20` |
| `pendingExpiryMinutes` | 沒人處理的訂位在訂位時間前幾分鐘過期（見下面「過期」）；**必須小於** `minLeadMinutes`。 | `60` |

匯入成功時會多印一行網址，`show` 則把網址印在**標準錯誤**（不會混進 `> 檔案` 存下來的設定）：

```text
已匯入組織 harvest（王記麵店）的店家設定。
線上訂位網址：https://assistant.example.org/reserve/wangji
```

**可以選的時間**（伺服器每次都重新計算，送出時也重新檢查，不信任表單送來的值）：

- 依第 17.5 節的**每週營業時間**，每段從開店時間每隔 `slotMinutes` 一個，最晚到打烊前 `lastSeatingMinutes`。跨午夜的時段（例如週六 17:00–01:00）
  算在開店那一天，凌晨的時間標成「00:00（隔天）」。日光節約時間跳過的時間會變成跳過之後的那一刻，同一刻只出現一次。
- 從現在起 `minLeadMinutes` 之後、到今天之後 `maxDaysAhead` 天為止（店家時區的日期）。
- **已確認的店休類指令**（第 17.6 節）：臨時店休那天整天不能訂；提早打烊、延後開店那天改用新的打烊、開店時間計算（最後訂位時間也從新的打烊時間往前算）；
  已確認的恢復營業會讓那天恢復可訂。還沒確認或已取消的指令不影響。
- 不控管名額，同一個時間可以有很多筆，由老闆逐筆決定。已經存下的訂位遇到之後的店休，見下面「店休與訂位」（#392）。

**要放到哪裡**：把 `https://{PUBLIC_BASE_URL 的網域}/reserve/{代號}` 放在

- Facebook 粉絲專頁：「編輯動作按鈕」選「立即預約」或「預訂」，連結填這個網址；
- Google 商家檔案：「編輯個人檔案 →（預訂）連結」或「預訂」按鈕，填這個網址；
- 店家**自己的** LINE 官方帳號的圖文選單：動作選「連結」，填這個網址（在 LINE 裡會用 LINE 內建瀏覽器開啟，一般網頁即可，不需要 LIFF）；
- 店門口的 QR code：用這個網址產生。

**反向代理**：第 5 節的設定把整個網站都轉給 api，`/reserve/` 不需要另外設定；如果你的代理**只轉特定路徑**（例如只轉 `/api/`），
要把 `/reserve/` 也轉給 api，並保留原本的 `Host` 與 `X-Forwarded-*` 標頭。`/reserve/...` 永遠由 api 回答，不會變成管理後台的頁面。

**防濫用**：

- **頻率限制**：顯示訂位頁、選日期（不用 JavaScript 時）與送出合計，**每個用戶端 IP 每分鐘 30 次**（不分店家），超過回 `429` 的「操作太頻繁了，
  請稍候一分鐘再試」頁面。要調整就設 `PublicChannels__RateLimits__CoordinatorReservationRequestsPerIpPerMinute`（第 3 節「頻率限制」的做法）；
  代理後面一定要設 `TRUSTED_PROXIES`（第 5 節），否則所有顧客共用代理的一個 IP。手機網路常常很多人共用一個 IP，覺得太緊就調高。
- 一個看不見的欄位擋機器人：填了它的請求一樣看到「已送出訂位」，但什麼都不存。
- **同一支電話在同一家店最多 3 筆待處理（時間還沒到）的訂位**，第 4 筆會請顧客等店家確認或直接來電。電話會先整理成數字（`+886 912-345-678` 和
  `0912345678` 算同一支），只收台灣的手機與市話。
- 每張表單帶一個一次性的送出代碼：重複按送出、重新整理完成頁，都只算一筆，顯示同一個完成頁。

**顧客看到的完成頁**：訂位內容，以及「店家確認後會寄 Email 通知您；也可能直接來電。」（有留 Email），或「店家會來電與您確認。」（沒留）；
「如需更改或取消，請來電 {店家電話}」。平台沒有設定 SMTP（第 16 節）時，訂位頁不顯示 Email 欄位。

**老闆與店長收到的通知（#390）**

- **誰收到**：顧客送出後幾秒內（背景工作），統籌助理把一張「新訂位｜{店名}」卡片**推播給這家店目前綁定的每位成員**——老闆與店長都會收到，
  之後才綁定的成員不會收到之前的卡片。卡片上有日期時間（跨午夜的寫「00:30（隔天）」，和訂位頁一樣）、人數、姓名、電話、Email（沒留的寫
  「沒有留 Email，請來電確認」）與備註，以及〔撥打電話〕（直接開手機的撥號）、〔接受〕、〔婉拒〕三個按鈕。平台沒有設定 SMTP 時，
  卡片只在顧客留了 Email 時才列出 Email，最後一行是「請來電確認」：系統不會通知顧客，要由店家打電話。
- **接受、婉拒**：只算**第一個按的人**。按的人收到「已接受：陳小姐 11/12（四） 18:30 4 位。會寄確認信給顧客。」（或「已婉拒：…
  會寄婉拒信給顧客。」）；顧客沒有留 Email 時是「…這位顧客沒有留 Email，請來電 0912-345-678。」，平台沒有設定 SMTP 時是
  「…平台沒有設定寄信，請來電 0912-345-678。」，都附〔撥打電話〕。
  其他人之後再按，會收到「這筆訂位已由王老闆在 11/12（四）14:02 接受。」（名字是那位成員的 LINE 顯示名稱；拿不到時寫「老闆」或「店長」），
  不會再寄信。
  已過期、已取消的訂位按了會照實回覆（因店休取消的會說原因，例如「這筆訂位因臨時店休已取消。」）。別家店的成員按不了。按鈕的回覆都用 reply，**不算則數**。
- **過期**：到了訂位時間前 `pendingExpiryMinutes`（預設 60）分鐘還沒有人處理，訂位就變成「已過期」（狀態 `expired`）；之後按〔接受〕會回
  「這筆訂位已過期：訂位時間前 60 分鐘還沒有人處理。」。改了 `pendingExpiryMinutes` 會套用到還沒過期的訂位。有留 Email 的顧客會收到過期通知信（見下面）。
- **「訂位」指令**：成員對統籌助理傳「訂位」（或「查看訂位」，或按圖文選單的「訂位」）就列出這家店**今天起 7 天內**待處理與已接受的訂位
  （時間早的在前），待處理的附〔接受〕〔婉拒〕；LINE 一次最多 12 張，超過時會說還有幾筆沒列出。綁了好幾家店的成員會先被問「要查看哪一家店的訂位？」。
  不需要呼叫模型，也不算則數。統籌助理的能力選單也多了「查看訂位」。
- **花多少則**：每筆新訂位用掉**這家店的成員人數**則（老闆＋店長 2 人＝2 則）統籌助理的每月訊息額度（平台負擔）；封鎖統籌助理的成員收不到、
  也不算則數。估算方式見設計文件第 7 節（例：每月 60 筆訂位、2 位成員、2 次臨時店休 ≈ 130 則；免費方案每月 200 則只夠一家店）。
- **推播失敗**：某位成員推不到（例如封鎖了統籌助理）只記下來，不影響其他人；LINE 暫時沒回應會自動重試幾次，重試**不會讓同一個人收到兩張**。
  至少一個人收到就是「已通知成員」；一個都沒收到（沒有成員、全部被拒）就是「未通知成員」。
- **本月額度用完**：LINE 會拒絕統籌助理的所有推播（輕用量、中用量不能加購）。這時訂位**照樣保存**，標成「未通知成員」，api 記一行警告
  `The 統籌助理's monthly message limit is reached`（不含顧客資料），**不會一直重試**。`coordinator-reservations list` 最前面會多一行
  「注意：其中 N 筆「未通知成員」」，請另外通知店家；成員自己傳「訂位」仍看得到、也能按（回覆不算則數）。要繼續推播就升級方案（第 7 節）。

**顧客收到的 Email（#391）**

- **什麼時候寄**：第一個按〔接受〕或〔婉拒〕的人按下、訂位過期、或店休類指令確認而取消訂位的那一刻，顧客有留 Email、平台有設定 SMTP（第 16 節），
  就排一封信進背景寄信佇列，通常幾秒內寄出；按鈕的回覆不等寄信。**每次狀態改變最多一封**：別人同時按、之後再按、過期工作重跑、重按〔確認〕，都不會再寄。
  被成員搶先處理的訂位不會再寄過期信；已接受、確認信還沒寄出就被店休取消的，只寄取消信。
- **四種信**（純文字；日期時間的寫法和訂位頁一樣，跨午夜寫「（隔天）」；電話是設定檔的 `reservations.phone`）：

  | 情況 | 主旨 | 內容 |
  | --- | --- | --- |
  | 接受 | 「王記麵店：訂位已確認」 | 「王記麵店已確認您的訂位：11/12（四） 18:30，4 位。如需更改或取消，請來電 02-2345-6789。」 |
  | 婉拒 | 「王記麵店：無法接受您的訂位」 | 「很抱歉，王記麵店無法接受您 11/12（四） 18:30 的訂位，歡迎改選其他時間或來電 02-2345-6789。」 |
  | 過期 | 「王記麵店：訂位未能確認」 | 「很抱歉，王記麵店未能及時確認您 11/12（四） 18:30 的訂位，這筆訂位已失效。如仍需訂位，請來電 02-2345-6789。」 |
  | 店休取消（#392） | 「王記麵店：訂位已取消」 | 「很抱歉，王記麵店因臨時店休，必須取消您 11/12（四） 18:30 的訂位，造成不便，敬請見諒。如有問題請來電 02-2345-6789。」（提早打烊寫「因當天提早在 20:00 打烊」，延後開店寫「因當天延後到 12:00 開店」） |

  每封信最後有一行「這封信由線上訂位系統代王記麵店寄出；回覆這封信不會有人讀取。」。信裡不含顧客填的姓名、電話或備註。
- **寄件者**：地址是平台的 `SMTP_FROM`（所有店家共用），顯示名稱是**店名**（不用 `SMTP_FROM_NAME`；轉真人的信仍用它）。
  顧客按「回覆」會寄到 `SMTP_FROM`，所以信裡請顧客來電；`SMTP_FROM` 建議用專門寄通知、不需要有人讀的信箱。
  有些信箱（例如 Gmail）會在店名旁邊顯示實際的寄件地址。
- **沒有設定 SMTP**：不排任何寄信工作，訂位頁不顯示 Email 欄位，卡片與按鈕回覆請店家來電（見上面）。已經排進佇列、之後才拿掉 SMTP 的信不會寄出。
- **失敗與重試**：和轉真人 Email 一樣（第 16 節）：30 秒、1 分、2 分、4 分後自動重試，共 5 次；都失敗就放棄並記一行錯誤
  （系統不會另外通知店家：營運者在紀錄裡看到時，請轉告店家來電）。
  寄信伺服器已收下信、api 卻剛好在記錄完成前停止的極少數情況，重新啟動後可能再寄一次。
- **怎麼確認有沒有寄出**：

  ```sh
  docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs api | grep -E 'Reservation Email|Email job|coordinator.reservation-email'
  ```

  | 紀錄 | 意思 |
  | --- | --- |
  | `Reservation Email (accepted) for reservation ... sent (attempt 1)` | 信已交給寄信伺服器（`declined` 婉拒、`expired` 過期、`cancelled` 店休取消） |
  | `Background job ... (coordinator.reservation-email) failed on attempt 1 of 5; retrying after ...` | 這次沒寄成功，稍後自動重試 |
  | `The Email job (accepted) of reservation ... gave up after 5 attempts: ...` | 重試 5 次都失敗，不會再寄；後面寫錯誤的種類（第 16.3 節的表） |
  | `Reservation Email (...) for reservation ... not sent: ...` | 決定不寄，後面寫原因（訂位已刪除、狀態已改變、拿掉了 SMTP 等） |

  紀錄裡**不會出現**顧客的 Email、姓名、電話或備註。資料庫裡的寄信工作：第 16.3 節的查詢把 `Kind` 換成 `'coordinator.reservation-email'`。

**店休與訂位（#392）**

- **哪些訂位會被取消**：老闆宣布臨時店休、提早打烊或延後開店（第 17.6 節）時，那個**營業日**裡**待處理或已接受、時間還沒到**的訂位：
  臨時店休是整天；提早打烊是**新打烊時間（含）之後**的；延後開店是**新開店時間之前**的（剛好在新開店時間的不取消）。跨午夜的時段（例如週六 17:00–01:00
  的凌晨）算在開店那一天，和訂位頁的算法一樣。已婉拒、已過期、已取消的不動；新打烊時間前 `lastSeatingMinutes` 內已經訂好的也不取消（只是不再開放新的）。
- **預覽卡**多一行「會取消 3 筆訂位（已接受 1 筆、待處理 2 筆）」，下面列前 5 筆的時間與姓名（例如「18:30 陳小姐」），更多的寫「還有 N 筆」；
  沒有受影響的訂位就沒有這一行。卡片每次重出（例如「改成：」）都會重新計算。
- **按〔確認〕之後**：這些訂位改成「已取消」（`cancelled`，取消原因記下情境與是哪一個指令），有留 Email 的顧客收到取消信（上面「四種信」）；
  其他顧客（沒有留 Email，或平台沒有設定 SMTP 時的每一位）列在執行報告最後一項「⏳ 打電話通知顧客訂位已取消」，每行「18:30 陳小姐 0912-345-678」，
  打完電話按〔已完成〕。報告標題下一行寫「已取消 3 筆訂位，其中 1 筆會寄信通知顧客。」。名單最多列 50 筆（超過會寫還有幾筆沒有列出，請營運者協助查詢）。
- **之後才送出的舊頁面**：顧客在店休確認之前打開的訂位頁，確認之後才按送出，被拿掉的時段不會存下來，頁面重新列出可選的時間，並寫
  「這個時段已經無法訂位（店家剛宣布臨時店休），請改選其他時間。」。確認與送出剛好同時發生時也一樣：被拿掉的時段不是被取消、就是送不出去。
- **筆數**：確認時用同一條規則重新計算；預覽之後又有人訂位、或成員先婉拒了一筆，實際取消的筆數會跟著變，報告寫實際的數字。
  重按〔確認〕、多人同時按，都只會取消一次、每位顧客只寄一封取消信。已接受、確認信還沒寄出的訂位被取消時，只寄取消信。
- **之後再按卡片上的〔接受〕〔婉拒〕**：回「這筆訂位因臨時店休已取消。」（提早打烊是「…因當天提早在 20:00 打烊已取消。」）。
- **恢復營業不會恢復已取消的訂位**：恢復營業的預覽卡與報告會寫「因店休取消的 N 筆訂位不會恢復，請視需要聯絡顧客」；訂位頁那天會重新開放，
  要不要請顧客重新訂位由店家決定。「打電話通知顧客訂位已取消」那一項不會因為恢復營業而取消。
- **紀錄**：`統籌助理 command ... started: ... N reservations cancelled (k to be told by Email) ...`，只有筆數；寄信看上面的 `Reservation Email (cancelled) ...`。
  `coordinator-reservations list` 會看到這些訂位的狀態是 `cancelled`。

**看額度：`coordinator-quota`**

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api coordinator-quota
```

印出統籌助理官方帳號**本月的訊息額度**（每月上限，或「無上限」）、LINE 統計的**本月已用量**（約略值，含在 Official Account Manager 送出的訊息）
與剩餘，接著是**本月各店由統籌助理送出的推播數**（依推播紀錄：訂位卡片、提醒、執行結果、重新授權連結，只算 LINE 接受的），以及每家店本月的
訂位筆數、成員人數、確認的店休類指令數與設計文件第 7 節的估算。只印數字與店名，不印顧客資料。月份以 `Statistics:TimeZone`（預設台北）計算；
LINE 的已用量以 LINE 自己的月份為準。建議每月看一次，再決定統籌助理要用哪個方案。
結束代碼：`0` 完成；`1` 統籌助理沒有設定、向 LINE 查詢失敗或讀取資料庫失敗（其他部分仍會印出）；`2` 參數錯誤。

**看訂位：`coordinator-reservations list`**

```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
  coordinator-reservations list --organization harvest
```

列出即將到來的訂位（時間早的在前）與最近 7 天內已經過了時間的（`--days` 1 到 90 可調），每筆一行：時間、人數、姓名、電話**只顯示最後 3 碼**、
狀態（`pending` 待處理、`accepted`、`declined`、`cancelled`、`expired`）、是否已通知成員（「已通知成員」「未通知成員」「尚未通知成員」），
以及有沒有 Email、備註（不印內容）；有「未通知成員」的訂位時最前面多一行提醒。
結束代碼：`0` 完成；`1` 找不到組織或讀取失敗；`2` 參數錯誤。

**保存的資料與紀錄**：訂位存在 `CoordinatorReservations`（訂位時間以 UTC 存、電話存成數字）。**訂位時間過後 90 天整筆刪除**（姓名、電話、Email、
備註一起），由每天凌晨 3 點的保存期限清理對每個組織執行，`retention-cleanup` 也會刪（第 12 節）。api 的紀錄與組織紀錄**不寫**顧客的姓名、電話、Email
或備註，只寫訂位 ID、組織、日期、人數與被拒絕的原因；LINE 卡片與回覆只送給這家店的成員。統籌助理每次推播 LINE 接受後，
會在 `CoordinatorPushLogs` 記一筆（店家、時間、用途、則數，沒有收件人或內容），保存 400 天。平台代店家處理這些資料：請在隱私權政策加上 `deploy/privacy-policy-template.md` 的「線上訂位」一節。

## 18. 線上點餐（#397 起）

線上點餐是獨立的模組，**不需要**統籌助理：每家店（組織）一份**點餐設定**與**菜單**，用 `ordering-shop` 匯入。
設計與後續的工作單見 `docs/plans/2026-11-06-online-ordering.md`。#398 起顧客可以在 `/order/<slug>` 點餐（第 18.2 節），
店員在後台「點餐」頁接單（#399）；#400 起有留 Email 的顧客會收到通知信，沒人處理的訂單會自動取消，個資在取餐 90 天後清除（第 18.3 節）；
#401 起可以開放外送（第 18.4 節）。

### 18.1 點餐設定與菜單：`ordering-shop`

1. 複製範例檔，一家店一個檔案（`deploy/shops/` 不會進 git）：

   ```sh
   mkdir -p deploy/shops
   cp deploy/ordering-shop.example.json deploy/shops/harvest-ordering.json
   ```

2. 修改檔案。`shop` 是店家資訊與點餐規則，`menu` 是菜單，兩段都必填：
   - `slug`：點餐網址的代號（之後的網址是 `/order/<slug>`），和訂位頁的代號分開設定，不能和別家店重複。
   - `services`：`takeoutScheduled`（外帶預約）、`takeoutAsap`（外帶即時，「儘快」）、`delivery`（外送預約，選填，預設關）、
     `deliveryAsap`（外送儘快，選填，要先開 `delivery`），至少開一種。開了外送就要有 `delivery` 段（第 18.4 節）。
   - `orderingHours`：可點餐時間，格式和第 17.5 節的 `weeklyHours` 相同（可跨午夜，例如 `17:00`–`01:00`）。
   - `slotMinutes`（10、15、20、30、60，預設 30）、`daysAhead`（可預約幾天，含今天，1–14，預設 3）、
     `minLeadMinutes`（最短準備時間，預設 15）、`maxOrdersPerSlot`（每個取餐時段最多幾筆，不填＝不限）。
   - `pendingTimeoutMinutes`（#400）：待確認的訂單在**可點餐時間內**幾分鐘沒有人接單或婉拒，就自動取消並通知顧客，5–120，預設 15。
     只在可點餐時間內計算：晚上送出的明天預約單，從明天第一段可點餐時間開始算；最晚到顧客選的取餐時間就取消。
     改這個設定只影響之後送出的訂單。
   - 菜單：分類 → 品項 → 選項群組 → 選項，選項底下可以再有選項群組，最多四層。`id` 由你自己取（小寫英數與 `-`），
     **之後不要改**：售完狀態與訂單都用它對應品項。價格是新台幣整數元；選項的 `price` 是加價。
     群組的 `min`／`max` 是至少、最多選幾個（`min: 1, max: 1` 就是必選單選），`repeatable: true` 可以同一選項選多份。
   - 暫停接單、即時單準備時間不在檔案裡：由店員在後台切換（#399），匯入不會改動它們。
3. 匯入：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T \
     -v "$PWD/deploy/shops:/shops:ro" api \
     ordering-shop import --organization harvest --file /shops/harvest-ordering.json
   ```

   檔案會**先整份檢查**，有任何錯誤就逐條列出「位置：說明」，**一筆都不寫入**。菜單和上一次不同時會存成新的一版
   （「菜單第 N 版」）；內容完全相同時輸出「沒有變更」，不寫任何東西。
4. 看目前的設定摘要（取餐方式、可點餐時間、時段規則、待確認逾時、外送區域與時間、暫停接單、菜單版本與品項數）：

   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api \
     ordering-shop show --organization harvest
   ```

結束代碼：`0` 完成；`1` 找不到組織、這家店還沒有點餐設定（`show`）或寫入失敗；`2` 參數錯誤、設定檔不正確或網址代號已被別家店使用。
每次匯入**有變更**時，組織紀錄會記一筆 `ordering-shop-updated`，只記哪些欄位改了與菜單版本，不記店名、公告或菜單內容。

### 18.2 顧客點餐網頁 `/order/<slug>`（#398）

- **網址**：`<PUBLIC_BASE_URL>/order/<slug>`；訂單狀態頁是 `/order/<slug>/o#<token>`（送出訂單時產生的隨機長字串，只顯示一次；放在 `#` 後面，瀏覽器不會送到伺服器，所以不會出現在 api 的紀錄裡）。
  映像內含點餐網頁（`apps/shop`，設定 `Shop__RootPath=wwwroot/shop`，設為空字串就不提供）。反向代理只轉特定路徑時，`/order/` 也要轉給 api。
- 網頁呼叫的公開 API 在 `/api/v1/public/ordering/<slug>/…`，只接受同一個網址來源的瀏覽器請求（別的網站的頁面送出會被拒絕）。
- **頻率限制**（每個用戶端 IP、不分店家）：讀取店家、菜單、時段、訂單狀態（狀態頁每 10 秒更新一次）與取消合計每分鐘 120 次
  （`PublicChannels__RateLimits__OrderingRequestsPerIpPerMinute`）；送出訂單每分鐘 10 次（`…__OrderingSubmissionsPerIpPerMinute`）。
  代理後面一定要設 `TRUSTED_PROXIES`（第 5 節）。
- **防亂下單**：同一支電話在同一家店最多 3 筆「等待店家確認」且取餐時間還沒到的訂單；每次結帳帶一個送出代碼，重送只算一筆；
  價格一律由伺服器依目前菜單計算，送出時再檢查時段、菜單版本、售完與分類供應時間。
- **保存的資料與紀錄**：訂單存在 `OrderingOrders`（姓名、電話、Email、備註、明細快照、金額）。api 的紀錄**不寫**顧客的姓名、電話、
  Email、備註或狀態頁的 token，只寫訂單 ID、組織、訂單號碼、品項數與金額。個資在取餐 90 天後清除（第 18.3 節）。

### 18.3 通知信、逾時自動取消與個資保存期限（#400）

- **通知信**：顧客有留 Email、而且設定了 SMTP（第 16 節）時，店員**接單**（含預計取餐時間）、**婉拒**（含理由與說明）、
  **店家取消**（含理由與說明）、**顧客自己取消**，以及**逾時自動取消**時，各寄一封純文字信。寄件人地址是 `SMTP_FROM`，
  顯示名稱是**店名**（不是 `SMTP_FROM_NAME`）。信裡有訂單編號、店名、取餐方式與時間、明細與金額、「取餐時付款」、店家電話；
  設了 `PUBLIC_BASE_URL` 時另附店家的點餐網頁 `<PUBLIC_BASE_URL>/order/<slug>`。**不附訂單狀態頁的連結**（系統只存 token 的雜湊，
  而且 token 不能放進網址路徑或查詢字串），也不含顧客填的姓名、電話與備註。
  信由背景工作在按下按鈕之後寄出（`ordering.order-email`），寄失敗會依背景工作的規則重試，**不會改變訂單狀態**；
  沒設 SMTP 時只是不寄，其他功能照常。
- **逾時自動取消**：每筆待確認的訂單送出時就決定「最晚確認時間」（店家的 `pendingTimeoutMinutes`，只算可點餐時間，
  最晚是取餐時間），由延後執行的背景工作（`ordering.expire-order`）在那時把仍待確認的訂單改成「逾時未處理」並寄信。
  店員同時按〔接單〕或〔婉拒〕、或顧客同時取消時，只有一個會生效。後台「點餐」頁的待確認卡片會顯示「HH:mm 前未確認將自動取消」，
  剩不到 5 分鐘時以紅字顯示。背景工作由 api 的工作佇列執行（`Jobs__WorkerEnabled`，預設開啟）。
- **個資保存期限**：取餐時間（接單時店員另訂的時間，否則是顧客選的時間）**滿 90 天**後，每天 03:00 的清理（與第 12 節相同時間，
  每個組織都會執行，不論對話保存期限）會清除該訂單的顧客姓名、電話、Email、訂單備註與品項備註；訂單編號、狀態、時間、品項、
  選項與金額**保留**作為營業額統計。後台與訂單狀態頁仍看得到這筆訂單，姓名顯示「（已刪除）」、沒有電話。
  也可以手動執行 `retention-cleanup --organization <代碼>`（第 12 節），輸出會多一行「線上點餐：清除 N 筆…」。
  隱私權政策範本的第 5.6 節照這個寫（`deploy/privacy-policy-template.md`）。

### 18.4 外送（#401）

外送**不用地圖、不算距離**：店家自己列出外送區域（例如「新營區・民權里」或「3 公里內」這種文字），每區有運費與最低消費；
顧客選區域、填地址，伺服器依區域計算運費、檢查最低消費。範例檔 `deploy/ordering-shop.example.json` 有三個區域。

- **設定**（`shop` 段）：`services.delivery: true`（外送預約），要開「外送儘快」再加 `services.deliveryAsap: true`；
  然後加上 `delivery`：

  ```json
  "delivery": {
    "zones": [
      { "id": "minquan", "name": "新營區・民權里", "fee": 0, "minimumSubtotal": 150 },
      { "id": "xinying", "name": "新營區其他里", "fee": 30, "minimumSubtotal": 200 }
    ],
    "orderingHours": { "mon": [], "tue": [{ "open": "11:00", "close": "13:30" }], "…": [] },
    "minLeadMinutes": 45,
    "asapExtraMinutes": 20,
    "note": "外送由店員騎車送達，不確定是否在範圍內請先來電。"
  }
  ```

  - `zones`：1–50 個區域。`id` 小寫英數與 `-`、不能重複，**之後不要改**（訂單記著它）；`name` 一行、最多 40 字，顧客與店員都看得到；
    `fee` 運費 0–10,000 元；`minimumSubtotal` 最低消費 0–100,000 元（以餐點小計計算，不含運費；不填＝ 0）。
  - `orderingHours`（選填）：外送的時間，格式同可點餐時間；不填就和外帶一樣。外送時段的長度、可預約天數、每時段上限沿用外帶的
    `slotMinutes`、`daysAhead`、`maxOrdersPerSlot`，但**名額和外帶分開算**（同一個時間外帶滿了，外送仍可訂）。
  - `minLeadMinutes`（選填，0–1440，預設 45）：外送最早可選的時間（外送比外帶久）。
  - `asapExtraMinutes`（選填，0–120，預設 20）：外送儘快的估計＝後台的即時單準備時間再加上這幾分鐘（例如 15–30 分鐘＋20＝「約 35–50 分鐘」）。
  - `note`（選填，最多 300 字）：外送範圍說明，顯示在顧客選區域的地方。
  - 關掉外送（`services.delivery: false`）時 `delivery` 段可以留著，不會用到。`ordering-shop show` 會列出區域、運費、最低消費、外送時間與說明。
- **顧客端**：取餐方式多了「外送（預約）」「外送（儘快）」；選外送要先選區域（看得到運費與最低消費）再選時間。購物車顯示
  「還差 NT$X 達外送門檻」，未達門檻不能結帳；結帳要填地址（必填，最多 120 字）與「給外送的備註」（選填，最多 100 字），
  合計含外送費，付款方式是貨到付款。伺服器也會再檢查：未達門檻回 `422 below-minimum`、區域不存在回 `422 unknown-zone`、
  店家沒開外送回 `409 service-unavailable`；顧客送來的運費一律忽略。
- **後台**：外送訂單的卡片有「外送」標記、區域、完整地址（附 Google 地圖搜尋連結）、給外送的備註與運費；
  〔可取餐〕按鈕在外送訂單是〔已出發〕（訂單狀態沿用「可取餐」，顧客的訂單頁顯示「外送已出發」）。
- **通知信**：外送訂單的信寫「外送」「預計送達」與「貨到付款」，只寫**區域名稱**，**不寫地址**與給外送的備註。
- **個資**：地址與給外送的備註和姓名、電話一樣，在送達時間滿 90 天後清除（第 18.3 節）；區域名稱與運費保留作為統計。
  api 的紀錄不寫地址。

### 18.5 內用掃碼（#402）

每張桌子一個 QR code；顧客掃碼直接點餐（不選時間），可以多次加點，用餐後在櫃台結帳。同一桌還沒結帳的訂單是一個「桌次」，
後台「點餐」頁的「內用」依桌號合併顯示並加總，店員收完錢按〔已結帳〕結束桌次，之後同一個 QR code 開始新的桌次。

- **設定**（`shop` 段）：`services.dineIn: true`，再加上 `dineIn`：

  ```json
  "dineIn": {
    "tables": [{ "id": "a1", "name": "A1" }, { "id": "window-1", "name": "窗邊 1" }],
    "autoAccept": false,
    "orderingHours": { "mon": [], "tue": [{ "open": "11:00", "close": "14:00" }], "…": [] },
    "note": "用餐後請至櫃台結帳，可用現金或 LINE Pay。",
    "maxPendingPerTable": 5
  }
  ```

  - `tables`：1–200 張桌子。`id` 小寫英數與 `-`、不能重複，**之後不要改**（QR code 跟著它）；`name` 一行、最多 20 字（「A1」「窗邊 2」）。
  - `autoAccept`（選填，預設 `false`）：`true` 時內用訂單一送出就是「已接單」（後台顯示「自動接單」），不會逾時取消。
  - `orderingHours`（選填）：內用點餐的時間，格式同可點餐時間；不填就和外帶一樣。暫停接單、售完、分類供應時間都照常適用。
  - `note`（選填，最多 300 字）：顧客結帳與訂單頁的付款說明（內用時取代 `notices.checkout`，那段通常寫的是外帶的取餐付款）；不填顯示「用餐後至櫃台結帳」。
  - `maxPendingPerTable`（選填，1–20，預設 5）：同一桌最多幾筆等待確認的訂單（內用不問電話，所以用桌號限制）。
  - 關掉內用（`services.dineIn: false`）時 `dineIn` 段可以留著：桌號與 QR code 都保留，但掃了會顯示「已失效」，重新開放後恢復。
- **QR code 與網址**：伺服器在桌號第一次出現在匯入檔時產生一個隨機代碼（token），之後重新匯入都不變；從檔案拿掉的桌號立刻失效，
  同一個 `id` 再加回來會拿到**新的**代碼（要重新列印）。網址是 `https://<平台網址>/order/<slug>/table#<代碼>`：代碼在 `#` 後面，
  瀏覽器不會把它送到伺服器的網址裡（API 從 `X-Ordering-Table` 標頭讀），所以不會出現在紀錄或追蹤裡。代碼原樣存在資料庫
  （它本來就印在桌上給大家看），只能用來向那一桌點餐、看那一桌這次的品項與金額，看不到任何人的稱呼或備註。

  ```sh
  # 列出每一桌的網址（有設定 PublicChannels:PublicBaseUrl 才是完整網址，否則只有路徑）
  docker compose --env-file .env -f deploy/docker-compose.yml run --rm api ordering-shop tables --organization <組織代碼>
  # 某一桌的 QR code 被拍走或貼紙壞了：換一個新代碼（舊的立刻失效，要重新列印那一桌）
  docker compose --env-file .env -f deploy/docker-compose.yml run --rm api ordering-shop tables --organization <組織代碼> --regenerate a1
  # 印 QR code：寫成可列印的 HTML（A4，每頁 6 張：店名、桌號、QR code、「掃描點餐，用餐後至櫃台結帳」）
  docker compose --env-file .env -f deploy/docker-compose.yml run --rm -v "$PWD:/out" api \
    ordering-shop tables --organization <組織代碼> --html /out/tables.html
  ```

  `--html` 需要先設定 `PublicChannels:PublicBaseUrl`（QR code 要指向完整網址）。用瀏覽器開啟 HTML 後列印（紙張 A4、邊界「無」或「預設」皆可），
  沿虛線剪下護貝。印好後用手機掃一張確認會打開那一桌的點餐頁。輸出含每一桌的代碼，不要貼到公開的地方。
- **顧客端**：掃碼後頁首顯示「內用・桌號 A1」，不選取餐方式與時間；結帳只問「稱呼（選填）」與訂單備註，付款寫「用餐後至櫃台結帳」
  （或 `note`）。送出後的訂單頁有〔繼續加點〕回菜單（桌號保留在這個瀏覽器分頁），菜單上方顯示這桌已點幾筆、共多少錢。
  QR code 失效或桌號被拿掉時顯示「這個桌號的 QR code 已失效，請洽店員」。內用不寄通知信（沒有 Email）。
- **後台**：內用訂單和其他訂單一樣出現在「待確認」（自動接單的直接到「處理中」），卡片寫「內用・A1」，〔可取餐〕叫〔已出餐〕。三欄下面的「內用桌次」列出還沒結帳的桌次：
  桌號、開始時間、每一筆訂單與狀態、合計；按〔已結帳〕確認金額後結束桌次，已接單與已出餐的訂單一併變成「完成」。
  還有待確認的訂單時不能結帳（先接單或婉拒）；確認對話框開著時同一桌又送來新訂單，會提示重新確認金額。
- **個資**：內用的稱呼與備註和其他訂單一樣，在送出時間滿 90 天後清除（第 18.3 節）；桌號與桌次不含個資。
