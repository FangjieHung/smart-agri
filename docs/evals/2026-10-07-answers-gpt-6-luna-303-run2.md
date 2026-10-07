# 回答評測：gpt-6-luna（2026-10-07）

> 由 `eval-answers` 產生（apps/api/README.md「Evaluating answers」；M3 計畫 Slice 13，#83）。
> 每題以 `GroundedAnswerService.AnswerAsync`（`company-data-only`）回答，題庫與判定方式見 `apps/api/eval/answers/README.md`。

## 執行設定

| 項目 | 值 |
| --- | --- |
| 嵌入提供者／模型 | `openai` / `text-embedding-3-small` |
| 對話提供者／模型 | `openai` / `gpt-6-luna` |
| Retrieval:MinScore | 0.406 |
| 回答提示版本 | `grounded-answer/2026-10-07.1` |
| 題庫 | `apps/api/eval/answers`（24 題） |
| 題庫指紋（SHA-256 前 12 碼） | `c12ebaedaf08` |
| 資料 | 4 個知識庫、5 份文件 |
| 執行時間 | 2026-10-07 13:34:33 +08:00，耗時 22.4 秒 |

## 結果摘要

| 指標 | 值 |
| --- | --- |
| 回覆類型正確率 | 18/24 = 75.0% |
| 引用命中率（company-data 題） | 10/16 = 62.5% |
| 平均輸入 token | 597.4 |
| 平均輸出 token | 22.5 |

## 拒絕原因分布

| 拒絕原因 | 次數 |
| --- | ---: |
| `below-threshold` | 11 |
| `cannot-answer` | 3 |

## 逐題結果

| 題號 | 追問 | 問題 | 預期類型 | 實際類型 | 類型正確 | 預期引用 | 實際引用 | 引用命中 | 拒絕原因 | 最高分 | 回覆內容 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | ---: | --- |
| returns-01 | — | 退貨期限是幾天？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — | 0.600 | 收到商品後七天內可申請退貨；生鮮蔬果不接受退貨。[1] |
| returns-02 | returns-01 | 那退款大概要多久才會退回來？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — | 0.623 | 退貨經確認後，退款會在五個工作天內退回原付款方式。[1] |
| returns-03 | — | 商品送到的時候就壞了該怎麼處理？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — | 0.626 | 請於到貨當天拍照並聯絡客服，將安排免費換貨，換貨以一次為限。[1] |
| product-01 | — | 蔬菜箱大箱大概適合幾個人的家庭？ | company-data | company-data | 是 | 商品使用指南.md | 商品使用指南.md | 是 | — | 0.748 | 蔬菜箱大箱約適合 4 至 5 人的家庭。[1] |
| product-02 | product-01 | 那如果是白米，開封以後要怎麼保存？ | company-data | company-data | 是 | 商品使用指南.md | 商品使用指南.md | 是 | — | 0.675 | 開封後請將白米倒入密封罐，並置於陰涼乾燥處，建議一個月內食用完畢。[1] |
| faq-01 | — | 可以貨到付款嗎？ | company-data | company-data | 是 | 常見問題.md | 常見問題.md | 是 | — | 0.670 | 可以，本島訂單結帳時可選擇貨到付款。[1] |
| faq-02 | — | 公司報帳需要統一編號，可以開嗎？ | company-data | company-data | 是 | 常見問題.md | 常見問題.md | 是 | — | 0.782 | 可以，結帳時填寫統一編號與公司抬頭即可開立電子發票。[1] |
| notice-01 | — | 有沒有最新的系統公告？ | company-data | no-result | **否** | 系統公告.md |  | **否** | `cannot-answer` | 0.440 | — |
| none-01 | — | 可以幫我訂下週的機票嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.370 | — |
| none-02 | — | 今天台北會下雨嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.206 | — |
| none-03 | — | 你們有賣寵物飼料嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.324 | — |
| none-04 | none-03 | 那貓砂呢？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.308 | — |
| returns-04 | — | 收到 5 天了可以退嗎？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — | 0.597 | 可以，收到商品後七天內可申請退貨。[1] |
| returns-05 | — | 收到 10 天了可以退嗎？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — | 0.540 | 已收到 10 天，超過七天退貨期限，無法申請退貨。[1] |
| store-01 | — | 地址在哪裡？ | company-data | no-result | **否** | 門市資訊.md |  | **否** | `below-threshold` | 0.304 | — |
| store-02 | — | 電話幾號？ | company-data | no-result | **否** | 門市資訊.md |  | **否** | `below-threshold` | 0.343 | — |
| store-03 | — | 每週哪一天公休？ | company-data | no-result | **否** | 門市資訊.md |  | **否** | `below-threshold` | 0.313 | — |
| store-04 | — | 週二有營業嗎？ | company-data | no-result | **否** | 門市資訊.md |  | **否** | `below-threshold` | 0.332 | — |
| store-05 | — | 週三有營業嗎？ | company-data | no-result | **否** | 門市資訊.md |  | **否** | `below-threshold` | 0.337 | — |
| store-06 | — | 有套餐嗎？ | company-data | company-data | 是 | 門市資訊.md | 門市資訊.md | 是 | — | 0.497 | 沒有套餐，餐點都是單點。[1] |
| trap-01 | — | 現在有什麼優惠活動嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.349 | — |
| trap-02 | — | 店裡有幾個座位？ | no-result | no-result | 是 |  |  | — | `below-threshold` | 0.374 | — |
| trap-03 | — | 退貨的運費要自己付嗎？ | no-result | no-result | 是 |  |  | — | `cannot-answer` | 0.457 | — |
| trap-04 | — | 外送要另外收運費嗎？ | no-result | no-result | 是 |  |  | — | `cannot-answer` | 0.446 | — |
