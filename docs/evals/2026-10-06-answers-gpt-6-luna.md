# 回答評測：gpt-6-luna（2026-10-06）

> 由 `eval-answers` 產生（apps/api/README.md「Evaluating answers」；M3 計畫 Slice 13，#83）。
> 每題以 `GroundedAnswerService.AnswerAsync`（`company-data-only`）回答，題庫與判定方式見 `apps/api/eval/answers/README.md`。

## 執行設定

| 項目 | 值 |
| --- | --- |
| 嵌入提供者／模型 | `openai` / `text-embedding-3-small` |
| 對話提供者／模型 | `openai` / `gpt-6-luna` |
| Retrieval:MinScore | 0.406 |
| 題庫 | `apps/api/eval/answers`（12 題） |
| 題庫指紋（SHA-256 前 12 碼） | `9afcf3c05d67` |
| 資料 | 3 個知識庫、4 份文件 |
| 執行時間 | 2026-10-06 10:06:42 +08:00，耗時 24.0 秒 |

## 結果摘要

| 指標 | 值 |
| --- | --- |
| 回覆類型正確率 | 11/12 = 91.7% |
| 引用命中率（company-data 題） | 7/8 = 87.5% |
| 平均輸入 token | 475.6 |
| 平均輸出 token | 25.1 |

## 拒絕原因分布

| 拒絕原因 | 次數 |
| --- | ---: |
| `below-threshold` | 4 |
| `cannot-answer` | 1 |

## 逐題結果

| 題號 | 追問 | 問題 | 預期類型 | 實際類型 | 類型正確 | 預期引用 | 實際引用 | 引用命中 | 拒絕原因 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| returns-01 | — | 退貨期限是幾天？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — |
| returns-02 | returns-01 | 那退款大概要多久才會退回來？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — |
| returns-03 | — | 商品送到的時候就壞了該怎麼處理？ | company-data | company-data | 是 | 退換貨辦法.md | 退換貨辦法.md | 是 | — |
| product-01 | — | 蔬菜箱大箱大概適合幾個人的家庭？ | company-data | company-data | 是 | 商品使用指南.md | 商品使用指南.md | 是 | — |
| product-02 | product-01 | 那如果是白米，開封以後要怎麼保存？ | company-data | company-data | 是 | 商品使用指南.md | 商品使用指南.md | 是 | — |
| faq-01 | — | 可以貨到付款嗎？ | company-data | company-data | 是 | 常見問題.md | 常見問題.md | 是 | — |
| faq-02 | — | 公司報帳需要統一編號，可以開嗎？ | company-data | company-data | 是 | 常見問題.md | 常見問題.md | 是 | — |
| notice-01 | — | 有沒有最新的系統公告？ | company-data | no-result | **否** | 系統公告.md |  | **否** | `cannot-answer` |
| none-01 | — | 可以幫我訂下週的機票嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` |
| none-02 | — | 今天台北會下雨嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` |
| none-03 | — | 你們有賣寵物飼料嗎？ | no-result | no-result | 是 |  |  | — | `below-threshold` |
| none-04 | none-03 | 那貓砂呢？ | no-result | no-result | 是 |  |  | — | `below-threshold` |
