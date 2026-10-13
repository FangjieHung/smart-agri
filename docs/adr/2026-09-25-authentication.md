# ADR｜登入與身分驗證

**狀態：** 已確認（2026-09-25）；2026-10-13 修訂登入時效，加上 refresh token（見文末「修訂」）

## 決策

- 預設使用 ASP.NET Core Identity，帳號與密碼存在本系統資料庫。
- 登入憑證由自架的 OpenIddict（Apache-2.0）核發，不依賴任何雲端身分服務。
- 個別組織可選擇改接自己的 OIDC 單一登入或內部 AD。
- 匿名訪客（網站嵌入、`/use/:assistantId`）與 LINE 使用者走獨立的身分流程，不建立一般登入帳號。網站訪客以 Data Protection 簽發的短期訪客憑證識別（M5a #196）；LINE 使用者由 Webhook 的簽章確認來源，以「助理＋聊天對象 id」區分前文，兩者都不建立帳號，也不寫入資料庫（M5b）。
- 前端 Demo 的三組固定帳號與密碼 `1234` 不進入正式後端。
- 帳號名稱只需在同一組織內唯一；登入時以「組織代碼＋帳號名稱＋密碼」識別。部署內只有一個組織時可省略組織代碼。Identity 內部的 `UserName` 存成「組織代碼/帳號名稱」組合值，以沿用 Identity 的全域唯一索引，畫面與 API 只顯示帳號名稱。
- 前端把 access token 存在分頁的 sessionStorage，30 分鐘到期即重新登入，暫不發 refresh token。（2026-10-13 修訂：改發 refresh token，見文末。）

## 理由

中小企業需要開箱即用的帳號系統；農會、政府單位多半已有既有帳號系統，需要能串接。自架 OpenIddict 讓完全地端部署時仍有標準 OIDC 流程。

不同組織常有相同的帳號名稱（例如 `admin`、員工編號），要求全系統唯一會讓客戶互相搶名稱；組合值寫法讓我們不必改寫 Identity 的使用者儲存層。

## 考慮過的選項

- **一律使用外部身分服務（Keycloak、Entra ID）**：小客戶需要額外部署或訂閱，增加導入門檻。
- **只用內建帳號**：無法滿足既有帳號系統的客戶。
- **帳號名稱全系統唯一（例如用 email）**：登入不必輸入組織代碼，但不同組織不能有同名帳號，且同一人屬於多個組織時難以處理。

## 影響

- 組織代碼建立後不可修改；若要修改，必須同時改寫該組織所有帳號的內部 `UserName`。

## 修訂（2026-10-13，負責人決定，#354）

同仁登入 30 分鐘後一定被登出、而且不會延長，日常使用太常要重新登入。改為：

- **access token 維持 30 分鐘**。admin SPA 登入時多要求 `offline_access`，取得 refresh token，在背景換發新的 access token。
- **閒置 8 小時就要重新登入**：refresh token 的效期是 8 小時，每次換發都換成新的一個（rotation），新的一個又是 8 小時（OpenIddict 的 sliding expiration）。
- **登入滿 7 天一定要重新登入**，不論期間換發過幾次。OpenIddict 本身的設定做不到這個上限（sliding 每次重新計時；關掉 sliding 會讓 8 小時變成絕對上限），所以登入時把「登入時間」與帳號的 security stamp 寫進授權碼與每一個 refresh token（沒有 destination 的 claim，只存在加密的授權碼與 refresh token 裡，不進 access token、identity token），token 端點每次換發都檢查；新發的 refresh token 與 access token 效期也不會超過 7 天的終點。
- **重複使用視為外洩**：已經換發過的 refresh token 超過 30 秒後再被使用時，OpenIddict 撤銷同一個授權底下的所有 token（整條鏈），使用者要重新登入。30 秒內再用一次照樣換發（OpenIddict 預設的重用寬限，#360 起明設 `RefreshTokenReuseLeeway`）：換發回應遺失後重試，或瀏覽器「複製分頁」讓兩個分頁共用同一份 sessionStorage 時，不會兩個分頁一起被登出。代價是偷到 token 的人在使用者換發後 30 秒內也能再換一次，而且這條鏈會分岔成兩支、都不會觸發撤銷；超過 30 秒任何一方再用舊的就撤銷整條鏈。最初（#354）設為 0，負責人 2026-10-13 改回 30 秒（#360）。
- **每次換發都重新檢查帳號**，與授權碼兌換相同：帳號仍存在且屬於同一組織、沒有被鎖定、可以登入、security stamp 與登入時相同（更改密碼、`reset-password` 都會換 stamp；改密碼的那次登入改用寫在它授權上的新 stamp，見下一點）。須先改密碼的帳號照樣可以換發，`PasswordChangeRequired` 仍是每個請求從資料庫讀取。
- **撤銷**：登出時前端先呼叫 OpenIddict 的撤銷端點 `/connect/revoke` 撤銷 refresh token，再走 end-session；`reset-password` 撤銷這個帳號的所有 token 紀錄（`RevokeBySubjectAsync`，涵蓋 refresh token），而且換過的 stamp 本身也讓舊的 refresh token 無法換發。
- **更改密碼保留這次登入、結束其他裝置（#360，負責人 2026-10-13 決定）**：一次登入就是一個 OpenIddict 授權（authorization），授權碼、之後的每個 refresh token 與 access token 都帶著它的 id（`oi_au_id`）。改密碼時，在同一個 transaction 裡換 stamp、把呼叫者 access token 所屬授權以外的所有授權與它們的 token 撤銷，並把新的 stamp 寫在保留的授權上（`smartagri_security_stamp` 屬性，只存在資料庫）；token 端點換發時優先採用授權上的 stamp，所以這台裝置手上（帶舊 stamp）的 refresh token 照常換發，不必重新登入，首次登入強制改密碼也一樣。登入時間不變，7 天上限仍從原本登入時起算。其他裝置的 refresh token 換發得到 `invalid_grant`；它們手上的 access token 從下一個請求起就是 `401`：每個請求本來就會讀帳號狀態的那個查詢，多讀一個 access token 所屬授權是否仍有效（`DatabaseAccountStandingSource`，同一個查詢，不另外查）。之後再從任何一台改密碼或 `reset-password`，stamp 又換了，原本保留的登入也一樣結束。
  - 取捨：被偷走的 access token 若剛好屬於改密碼的那次登入（例如 XSS 就發生在這個分頁），改密碼救不了；要結束全部登入請登出後重新登入、或請管理者執行 `reset-password`。前端不必配合：改密碼仍回 `204`，token 不變。
- **前端換發時機**：access token 到期前 1 分鐘，若這組 token 發出後有過 API 請求就主動換發（分頁開著沒人用時不換發，閒置才真的由 8 小時決定）；送出請求時 token 已過期或快到期就先換發；收到 401 就換發一次並重送那個請求一次。同時需要換發的請求共用同一次。伺服器拒絕換發時清除工作階段，帶著目前的網址（只接受 `/app` 底下）回登入頁，重新登入後回到原頁；伺服器暫時無法回應時保留工作階段。
- **儲存位置不變**：refresh token 與 access token 一樣只存在分頁的 sessionStorage（關閉分頁就結束；不同分頁各自登入）。

### 取捨：XSS

sessionStorage 裡的 token 能被頁面上執行的任何腳本讀取。原本被偷走的只是最多 30 分鐘的 access token；現在 refresh token 也在同一處，偷到的人可以持續換發，最長到 7 天的上限。緩解：rotation 讓攻擊者與使用者其中一方一換發，另一方超過 30 秒再用舊的就會觸發整條鏈撤銷；登出、`reset-password` 都會撤銷，改密碼撤銷其他裝置的登入；閒置 8 小時失效。更強的作法是把 refresh token 放在 HttpOnly cookie、由後端代為換發（BFF），但要多一層後端代理與 CSRF 防護，目前不採用；若之後要接受更高風險的客戶（例如政府單位）或發現 XSS，再改為 BFF。

### 影響

- `admin-spa` 用戶端多了 refresh token 授權類型與撤銷端點的權限，由 `migrate` 更新（升級時照常執行 `migrate` 即可）。
- 只有 admin SPA 用這套登入。`/use/:assistantId`、`/chat/:assistantId` 在 API 模式轉到工作區，沿用同一個登入；網站嵌入的匿名訪客與 LINE 不受影響。
- mock 模式沒有 token，Demo 身分仍是閒置 30 分鐘逾時；API 模式本機的換頁逾時改為 7 天，閒置由伺服器的 8 小時決定。

## 修訂（2026-10-13，負責人決定，#356）：停用成員

管理者停用離職的成員時要指定接手人，資產一次轉給接手人；帳號與歷史紀錄保留，之後可以重新啟用。對登入的影響：

- 帳號多一個 `DeactivatedAt`。停用的帳號用密碼登入得到與密碼錯誤相同的 `401`（也不累計失敗次數），`/connect/authorize` 不發授權碼，token 端點不換發（`CanContinueAsync` 先檢查它）。
- 停用時在同一個 transaction 裡換 security stamp（登入 cookie 失效）並撤銷這個帳號所有 token 紀錄（`RevokeBySubjectAsync`，含 refresh token）。
- **手上的 access token 也立刻失效**：`DeactivatedAt` 和 `PasswordChangeRequired` 在同一個每個請求都會跑的查詢裡讀取，停用的帳號從下一個請求起，所有受保護的端點（包含 `/api/v1/me`）都回 `401`，不必等 30 分鐘到期。
- 重新啟用只清掉 `DeactivatedAt`，成員用原本的密碼重新登入；已轉出的資產不會轉回。

## 修訂（2026-10-13，負責人決定，#361）：停用規則補強

- 只有管理者（`smb-admin`）能停用或重新啟用另一位管理者；停用內部同仁與外部客戶仍只需要「管理助理與團隊」。後端以資料庫裡呼叫者的角色判斷（`422 admin-required`），前端不顯示按鈕。
- 名下沒有任何要轉移的東西（助理、草稿、知識庫、數據庫、未解決的處理事項、承辦中的案件）時可以不選接手人；有東西時仍必填。
- 所有「選帳號」的選單都不列停用的帳號；已分享、已指定、案件群組的成員資格保留（重新啟用後恢復），但不能新選。

