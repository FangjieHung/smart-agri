# 部署、官網嵌入與 LINE 發布指南（營運人員）

這份文件寫給**部署與維運系統的人**：把助理放上客戶官網（M5a「官網嵌入」）或客戶的 LINE 官方帳號（M5b，第 10 節）之前，伺服器端要準備什麼、
`deploy/.env` 要填什麼、哪些東西要備份、出問題時看哪裡。第一次安裝（建立第一個組織與管理者）、
各設定的完整行為與錯誤碼，請看英文的 [`apps/api/README.md`](../apps/api/README.md)；本文只照操作順序串起來，
並在每一節標出該去哪一節看細節。對話模型清單（讓組織選模型）與對話保存期限的部署注意事項在第 11、12 節。

## 1. 這個部署長什麼樣子

`deploy/docker-compose.yml` 啟動兩個服務：`postgres`（pgvector 資料庫）與 `api`。**一個網址（`PUBLIC_BASE_URL`）
同時提供管理介面、API、LINE 與官網嵌入**，`api` 容器提供：

- 管理介面（`apps/admin`，已打包在映像檔裡）：`/`、`/login`、`/app/...` 等不屬於下列路徑的網址都回管理介面；
- 管理介面用的 API（`/api/*`、`/connect/*`、`/.well-known/*`）；
- 訪客用的對話視窗（`/use/{助理 id}`、`/widget/*`）與載入器（`/embed.js`），都不需要登入；
- 訪客 API（`/api/v1/public/*`）與 LINE 的 webhook（`/api/v1/line/webhook/{助理 id}`）；
- 健康檢查（`/health/live`、`/health/ready`）。

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
  資料庫裡的機敏設定是密文，只有這組金鑰打得開。金鑰 volume 的備份範例：

  ```sh
  docker run --rm -v smartagri_smartagri-dataprotection-keys:/keys:ro -v "$PWD":/backup alpine \
    tar czf /backup/dataprotection-keys.tgz -C /keys .
  ```

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
| 重建容器後所有人被登出 | 金鑰 volume 沒有掛上或被刪（第 4 節）。 |
| LINE 使用者收到兩則回覆，或一則罐頭回覆加一則助理回答 | Official Account Manager 的「自動回應訊息」沒關（第 10.2 節）。 |
| LINE 的「測試連線」第三項（webhook-test）失敗 | `PUBLIC_BASE_URL` 不是 LINE 連得到的公開 HTTPS 網址，或反向代理沒轉發 `/api/v1/line/webhook/*`；網址變了要重按「測試連線」（第 10.1 節）。 |
| 群組裡 @ 官方帳號沒反應 | 要用 LINE 提及清單選到官方帳號，手打「@名稱」不算提及；也要確認官方帳號允許加入群組（第 10.2、10.5 節）。 |
| LINE 一對一的回答很慢才出現，admin 的「本月補送次數」增加 | 回答超過回覆期限（預設 50 秒）改用 push 補送，會消耗 LINE 訊息額度（第 10.3 節）。 |

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
| **聊天**（回應設定） | 擁有者想自己看客戶訊息時才開 | 開了之後，擁有者可在 Official Account Manager 的聊天介面看到客戶訊息並手動回覆。**系統不會通知任何人、也不會轉接真人**：「找不到資料時的回覆」若寫「店長會在這裡回覆您」，只有擁有者真的去看聊天介面才成立。 |

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

- **非文字訊息**：一對一聊天傳貼圖、照片等，回固定的「目前只能回答文字問題。」（暫時無法由擁有者修改，見 #291）；群組不回應。
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

  結束碼 `0` 完成、`1` 組織不存在或清理失敗、`2` 參數錯誤。`--as-of`（模擬未來時間）只給開發與測試用，Production 會拒絕。
- **立即刪除既有對話**：管理者可以在「系統設定 → 對話保存」對單一助理立即刪除所有成員已保存的對話，不等保存期限；處理事項裡的問答副本與回答紀錄不受影響。
  這是管理介面的操作，不需要營運人員介入，但刪除後無法復原，資料庫備份是唯一的救回方式。

## 13. 哪些指令必須在真實部署上才能驗證

本文的 `openssl`、`docker compose config`、`set-token-limit`（含說明、未知組織、參數錯誤與實際設定）與 nginx 設定語法，
都已在本機實際執行過。需要真實伺服器、公開網域與 TLS 才能驗證的是：`docker compose up`、對外網址的 `curl`、
備份用的 `docker run … tar`（需要已存在的金鑰 volume），以及在真實官網上的嵌入驗收。

LINE 部分已在真實的 LINE 官方帳號上以 Cloudflare 通道與真實模型驗收過（一對一、群組、歡迎訊息、非文字訊息、暫停與恢復、逾時補送、
自動回應訊息未關閉時的行為），紀錄見 [`docs/evals/2026-10-07-235-line-acceptance.md`](../docs/evals/2026-10-07-235-line-acceptance.md)。
客戶自己的官方帳號、公開網域與方案額度仍需在交付時各自確認（第 10 節）。

對話模型清單（第 11 節）已在本機以兩個真實模型（部署預設 `gpt-6-luna`、額外的 `gpt-5.6-terra`，推理強度都是 `None`）實際切換驗收：
試問、題組重跑、表單請求、數據庫查詢與報表摘要都記到切換後的模型，兩個模型都完成了表單與數據庫查詢的工具呼叫，紀錄見
[`docs/evals/2026-10-07-245-model-switch-acceptance.md`](../docs/evals/2026-10-07-245-model-switch-acceptance.md)。compose 預留的
`CHAT_MODELS_0_*` 空白與填寫兩種情況由啟動測試（`ChatModelCatalogStartupTests`）解析 compose 檔驗證，`docker compose config` 也實際展開過；
填了之後在真實部署上 `up`、看設定頁出現兩個模型，仍需在交付時確認。
