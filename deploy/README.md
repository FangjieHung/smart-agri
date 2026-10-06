# 部署與官網嵌入發布指南（營運人員）

這份文件寫給**部署與維運系統的人**：把助理放上客戶官網（M5a「官網嵌入」）之前，伺服器端要準備什麼、
`deploy/.env` 要填什麼、哪些東西要備份、出問題時看哪裡。第一次安裝（建立第一個組織與管理者）、
各設定的完整行為與錯誤碼，請看英文的 [`apps/api/README.md`](../apps/api/README.md)；本文只照操作順序串起來，
並在每一節標出該去哪一節看細節。

## 1. 這個部署長什麼樣子

`deploy/docker-compose.yml` 啟動兩個服務：`postgres`（pgvector 資料庫）與 `api`。`api` 容器同時提供：

- 管理介面用的 API（`/api/*`、`/connect/*`）；
- 訪客用的對話視窗（`/use/{助理 id}`、`/widget/*`）與載入器（`/embed.js`），都不需要登入；
- 訪客 API（`/api/v1/public/*`）。

容器只講**純 HTTP**（容器內 8080，對外映射到 `API_PORT`，預設 8080），**不處理 TLS**。對外一定要有一層
反向代理（nginx、雲端負載平衡器、CDN）負責 HTTPS。客戶官網是 HTTPS，瀏覽器不會在 HTTPS 頁面裡載入 HTTP 的
`<script>` 或 iframe，所以**沒有 HTTPS 的公開網址就不能做官網嵌入**。

> 管理介面（`apps/admin`）是另外部署的單頁應用，不由 `api` 容器提供；它的來源網址要填在 `ADMIN_SPA_ORIGIN`
>（見 `apps/api/README.md`「Sign-in and tokens」）。

## 2. 事前準備

| 項目 | 說明 |
| --- | --- |
| 公開的 HTTPS 網址 | 訪客瀏覽器連得到的網址，例如 `https://assistant.example.org`，指向反向代理，再轉到 `api` 容器的 8080。憑證用公開 CA 簽發（Let's Encrypt 等）；自簽憑證的網址，客戶官網的訪客瀏覽器會拒絕載入。 |
| 三個 `.pfx` 憑證 | `deploy/certs/signing.pfx`、`encryption.pfx`、`dataprotection.pfx`。這三個是**API 自己用的金鑰憑證，不是網站的 TLS 憑證**，自簽即可，產生方式見下一節與 `apps/api/README.md`「Sign-in and tokens」。 |
| 對話模型與嵌入模型 | `CHAT_*` 與 `EMBEDDING_*` 都要設好（見 `deploy/.env.example`）。沒有對話模型時，訪客看到的是無法回覆；建議使用會回傳 token 用量的供應商，否則每月用量上限算不到（見第 6 節）。 |
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
| `PUBLIC_BASE_URL` | 訪客瀏覽器連到 api 的網址，例如 `https://assistant.example.org`（不用加路徑） | api 照常啟動，但沒有嵌入碼、**官網頻道無法發布**（`422 public-base-url`） |
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

啟動後驗證（`<網址>` 換成你的公開網址；兩個都應該回 `200`）：

```sh
curl -s -o /dev/null -w '%{http_code}\n' <網址>/health/ready
curl -s -o /dev/null -w '%{http_code}\n' <網址>/embed.js
```

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
`X-Forwarded-For`（訪客真正的 IP）、`X-Forwarded-Proto`（`https`）、`Host`。

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
**只有對外的官網回覆暫停**（訪客看到「目前暫停服務」），組織內部照常使用；下個月（以 `STATISTICS_TIME_ZONE`
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

## 10. 哪些指令必須在真實部署上才能驗證

本文的 `openssl`、`docker compose config`、`set-token-limit`（含說明、未知組織、參數錯誤與實際設定）與 nginx 設定語法，
都已在本機實際執行過。需要真實伺服器、公開網域與 TLS 才能驗證的是：`docker compose up`、對外網址的 `curl`、
備份用的 `docker run … tar`（需要已存在的金鑰 volume），以及在真實官網上的嵌入驗收。
