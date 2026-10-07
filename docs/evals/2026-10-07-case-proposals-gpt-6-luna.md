# 案件提議觸發評測（#257）

- 執行時間：2026-10-07 06:40:12 +08:00（139.4 秒）
- 題庫：`apps/api/eval/case-proposals`（指紋 `908ca7a11e48`，50 題）
- 範例表單：「田間異常回報」，收集目的：記錄田區病蟲害與作物生長異常，方便技術人員追蹤處理。
- 可提議的案件類型（依序提供）：
  - `repair`＝「設備報修」：農機、灌溉、溫室、冷藏庫等設備故障或損壞，需要派人到場維修。
  - `purchase`＝「採購申請」：購買肥料、農藥、資材或設備，需要主管核准後才能下單。
  - `return`＝「客戶退貨」：客戶收到的農產品有品質問題、數量不符或寄錯，要求退貨、換貨或退款。
- 對話模型：gpt-6-luna（openai）
- 查詢層一律以 `DatabaseQueryTools.AsksForStatistics`（提供查詢工具的前置檢查）判定，不呼叫查詢模型。

## 摘要：整條提議階段（決定 L：數據庫查詢 → 表單 → 案件）

| 觸發方式 | 漏觸 | 誤觸（合計） | 誤觸：不應提議 | 誤觸：應給表單 | 誤觸：應走查詢 | 選錯類型 | 表單題給表單 | 查詢題走查詢 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| keyword | 13/16（81.3%） | 5/26（19.2%） | 5/15 | 0/6 | 0/5 | 0/3（0.0%） | 2/6 | 5/5 | 1/8（12.5%） | 0 |
| model | 16/16（100.0%） | 1/26（3.8%） | 1/15 | 0/6 | 0/5 | 0/0（—） | 6/6 | 5/5 | 2/8（25.0%） | 0 |

## 摘要：只看案件層（假設前面各層都沒有成立）

| 觸發方式 | 漏觸 | 誤觸（合計） | 誤觸：不應提議 | 誤觸：應給表單 | 誤觸：應走查詢 | 選錯類型 | 表單題給表單 | 查詢題走查詢 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| keyword | 13/16（81.3%） | 7/26（26.9%） | 5/15 | 0/6 | 2/5 | 0/3（0.0%） | — | — | 2/8（25.0%） | 0 |
| model | 0/16（0.0%） | 8/26（30.8%） | 1/15 | 6/6 | 1/5 | 0/16（0.0%） | — | — | 5/8（62.5%） | 0 |

漏觸＝應提議案件卻沒有提議；誤觸＝不應提議、應給表單或應走查詢的題目卻提議了案件；選錯類型的分母是有提議案件的應提議題。

## 類型名稱本身含開案關鍵字

非案件題中，問題提到「名稱本身含開案關鍵字」的類型（「設備報修」、「採購申請」、「客戶退貨」）的有 8 題：關鍵字在案件層提議了 8 題、整條提議階段 6 題；模型在案件層 2 題、整條提議階段 1 題。

題目：n01、n02、n03、n13、n15、q02、q03、a07。

## Token 用量

| 呼叫 | 次數（有回報用量） | 輸入合計 | 輸出合計 | 每次平均（入／出） |
| --- | --- | --- | --- | --- |
| 表單選擇（request_database_form） | 50 | 21000 | 1679 | 420.0／33.6 |
| 案件選擇（propose_case） | 50 | 32650 | 3419 | 653.0／68.4 |
| 合計 | 100 | 53650 | 5098 | — |

每題都呼叫表單與案件各一次（案件層要單獨評測），所以合計是正式環境的上限：正式環境只有表單沒有成立時才做案件選擇，而統計問題兩者都不做。

## 逐題結果

| id | 類別 | 標記 | 關鍵字：案件層 | 關鍵字：提議階段 | 模型：案件層 | 模型：提議階段 | tokens 表單（入／出） | tokens 案件（入／出） | 問題 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| c01 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 419／36 | 652／72 | 曳引機發不動了，請派人來修 |
| c02 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 424／36 | 657／82 | 3 號溫室的捲揚馬達壞了，要報修 |
| c03 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 425／36 | 658／83 | 冷藏庫溫度一直降不下來，麻煩安排師傅來看 |
| c04 | case | case:repair | case:repair ✓ | case:repair ✓ | case:repair ✓ | form ✗ | 416／36 | 649／67 | 我要設備報修：抽水馬達漏水 |
| c05 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 427／36 | 660／81 | 噴霧機的噴頭堵住清不通，誰可以來處理一下？ |
| c06 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 420／36 | 653／77 | 自動餵料機卡住不動，需要有人來維修 |
| c07 | case | case:purchase | case:purchase ✓ | case:purchase ✓ | case:purchase ✓ | form ✗ | 422／36 | 655／73 | 我要提採購申請，下個月要買 20 包有機肥 |
| c08 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | form ✗ | 422／36 | 655／72 | 育苗盤快用完了，幫我申請買 500 個 |
| c09 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | form ✗ | 422／36 | 655／69 | 請幫我跟主管申請購買防蟲網兩捲 |
| c10 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | form ✗ | 417／36 | 650／64 | 包裝紙箱剩不到一百個，需要叫貨 |
| c11 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | form ✗ | 423／36 | 656／68 | 農藥庫存不夠了，要再訂一批亞磷酸 |
| c12 | case | case:return | none ✗ | none ✗ | case:return ✓ | form ✗ | 423／36 | 656／80 | 客戶說收到的芒果有一半壓傷，要求退貨 |
| c13 | case | case:return | none ✗ | none ✗ | case:return ✓ | form ✗ | 422／36 | 655／77 | 王小姐訂的有機蔬菜箱少了兩樣，她要退款 |
| c14 | case | case:return | case:return ✓ | case:return ✓ | case:return ✓ | form ✗ | 427／36 | 660／92 | 我要處理客戶退貨：上週出的 30 箱番茄有腐爛 |
| c15 | case | case:return | none ✗ | none ✗ | case:return ✓ | form ✗ | 428／36 | 661／76 | 有客人反映收到的雞蛋破了好幾顆，要換一盒新的給他 |
| c16 | case | case:return | none ✗ | none ✗ | case:return ✓ | form ✗ | 423／36 | 656／85 | 批發商說這批高麗菜重量不足，要我們退一部分貨款 |
| n01 | none | none | case:purchase ✗ | case:purchase ✗ | none ✓ | none ✓ | 416／8 | 649／8 | 採購申請要多久才會核准？ |
| n02 | none | none | case:repair ✗ | case:repair ✗ | none ✓ | none ✓ | 414／8 | 647／8 | 設備報修的流程是什麼？ |
| n03 | none | none | case:return ✗ | case:return ✗ | none ✓ | none ✓ | 420／8 | 653／8 | 客戶退貨的規定是收到後幾天內？ |
| n04 | none | none | none ✓ | none ✓ | case:repair ✗ | case:repair ✗ | 416／8 | 649／62 | 上次報修的冷氣修好了嗎？ |
| n05 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 419／8 | 652／8 | 我申請的肥料採購進度到哪了？ |
| n06 | none | none | none ✓ | none ✓ | none ✓ | form ✗ | 418／36 | 651／138 | 番茄葉子捲曲是什麼原因？ |
| n07 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 416／106 | 649／69 | 曳引機多久要換一次機油？ |
| n08 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 415／71 | 648／44 | 今天下午適合噴藥嗎？ |
| n09 | none | none | none ✓ | none ✓ | none ✓ | form ✗ | 417／36 | 650／103 | 有機肥跟化學肥差在哪裡？ |
| n10 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 419／92 | 652／77 | 我想申請農業補助，要準備哪些文件？ |
| n11 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 417／8 | 650／8 | 幫我安排明天的採收工作順序 |
| n12 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 421／50 | 654／81 | 冷藏庫溫度應該設定幾度比較好？ |
| n13 | none | none | case:purchase ✗ | case:purchase ✗ | none ✓ | none ✓ | 417／7 | 650／62 | 採購申請表要填哪些欄位？ |
| n14 | none | none | none ✓ | none ✓ | none ✓ | form ✗ | 424／36 | 657／289 | 不用開案，告訴我怎麼自己修理滴灌管就好 |
| n15 | none | none | case:return ✗ | case:return ✗ | none ✓ | none ✓ | 420／89 | 653／82 | 客戶退貨後的蔬菜可以再賣嗎？ |
| f01 | form | form | none ✓ | form ✓ | case:repair ✗ | form ✓ | 420／36 | 653／72 | 我要回報今天 A 區的病蟲害狀況 |
| f02 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 427／36 | 660／91 | 3 號溫室的番茄葉子出現黃斑，我要通報一下 |
| f03 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 431／36 | 664／81 | 稻田裡出現福壽螺卵，幫我記一筆給技術員 |
| f04 | form | form | none ✓ | form ✓ | case:purchase ✗ | form ✓ | 417／36 | 650／73 | 我要登記今天巡田看到的蚜蟲 |
| f05 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 427／36 | 660／83 | 東區的玉米葉子大量枯黃，要提報給技術人員追蹤 |
| f06 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 428／36 | 661／87 | 西瓜田一區突然萎凋，請派技術員來看並記錄下來 |
| q01 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 415／8 | 648／8 | 這個月報修了幾件？ |
| q02 | query | query | case:purchase ✗ | query ✓ | none ✓ | query ✓ | 416／7 | 649／8 | 今年採購申請一共有幾筆？ |
| q03 | query | query | case:return ✗ | query ✓ | case:return ✗ | query ✓ | 417／7 | 650／67 | 上個月客戶退貨的件數是多少？ |
| q04 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 418／8 | 651／8 | 本季的肥料採購金額加總是多少？ |
| q05 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 421／7 | 654／8 | 統計一下近 30 天的病蟲害回報次數 |
| a01 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 414／36 | 647／70 | 灌溉系統壞了 |
| a02 | ambiguous | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 419／56 | 652／71 | 想買一台新的割草機，要怎麼申請？ |
| a03 | ambiguous | none | none ✓ | none ✓ | case:return ✗ | form ✗ | 415／36 | 648／65 | 客戶抱怨芒果太酸 |
| a04 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 422／36 | 655／83 | 溫室的感測器好像怪怪的，數字一直跳 |
| a05 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 407／36 | 640／64 | 報修 |
| a06 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | form ✗ | 423／36 | 656／81 | 冷藏庫壞了，裡面的蔬菜也開始爛了 |
| a07 | ambiguous | none | case:purchase ✗ | case:purchase ✗ | case:purchase ✗ | case:purchase ✗ | 417／7 | 650／68 | 我想取消剛剛的採購申請 |
| a08 | ambiguous | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 417／36 | 650／66 | 葉子黃掉了，要不要找人來看？ |

## 模型草擬的案件標題（案件層）

| id | 類型 | 標題草稿 |
| --- | --- | --- |
| c01 | repair | 曳引機故障需派人維修 |
| c02 | repair | 3 號溫室捲揚馬達報修 |
| c03 | repair | 冷藏庫溫度無法下降，安排師傅檢修 |
| c04 | repair | 抽水馬達漏水報修 |
| c05 | repair | 噴霧機噴頭堵塞需處理 |
| c06 | repair | 自動餵料機卡住不動，請安排維修 |
| c07 | purchase | 申請採購 20 包有機肥 |
| c08 | purchase | 申請採購 500 個育苗盤 |
| c09 | purchase | 申請購買防蟲網兩捲 |
| c10 | purchase | 申請採購包裝紙箱 |
| c11 | purchase | 採購一批亞磷酸 |
| c12 | return | 客戶收到的芒果有一半壓傷並要求退貨 |
| c13 | return | 王小姐有機蔬菜箱缺少兩樣並要求退款 |
| c14 | return | 上週出貨的 30 箱番茄有腐爛，需處理客戶退貨 |
| c15 | return | 雞蛋破損要求換貨 |
| c16 | return | 批發商反映高麗菜重量不足並要求部分退款 |
| n04 | repair | 冷氣設備報修 |
| f01 | repair | 回報今天 A 區病蟲害狀況 |
| f02 | repair | 通報 3 號溫室番茄葉片出現黃斑 |
| f03 | repair | 稻田出現福壽螺卵 |
| f04 | purchase | 申請處理田間蚜蟲 |
| f05 | repair | 東區玉米葉大量枯黃，請技術人員追蹤 |
| f06 | repair | 西瓜田一區作物突然萎凋，請派技術員查看 |
| q03 | return | 查詢上個月客戶退貨件數 |
| a01 | repair | 灌溉系統故障報修 |
| a02 | purchase | 申請採購一台新的割草機 |
| a03 | return | 客戶反映芒果太酸 |
| a04 | repair | 溫室感測器數值持續跳動 |
| a05 | repair | 設備報修 |
| a06 | repair | 冷藏庫故障並影響蔬菜保存 |
| a07 | purchase | 取消剛剛的採購申請 |
| a08 | repair | 葉片黃化請人檢查 |
