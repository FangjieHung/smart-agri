# 案件提議觸發評測（#257；#286 起整條提議階段走合成選擇）

- 執行時間：2026-10-07 11:42:06 +08:00（168.4 秒）
- 題庫：`apps/api/eval/case-proposals`（指紋 `908ca7a11e48`，50 題）
- 範例表單：「田間異常回報」，收集目的：記錄田區病蟲害與作物生長異常，方便技術人員追蹤處理。
- 可提議的案件類型（依序提供；說明取自 `apps/api/eval/case-proposals/types-with-exclusions.json`，指紋 `3ac6101d152e`，取代題庫內建的說明）：
  - `repair`＝「設備報修」：農機、灌溉、溫室、冷藏庫、感測器等設備故障或損壞，需要派人到場檢查或維修。包括機器不能運轉、管線漏水、溫控或電控失靈。不包括作物病蟲害、作物生長異常，例如葉子變色、枯萎、長蟲，這些請用田間異常回報，由技術人員追蹤。
  - `purchase`＝「採購申請」：向廠商購買肥料、農藥、種苗、包材等資材或設備，需要主管核准後才能下單。包括庫存不足要補貨、要添購新的工具或機器。不包括設備壞了要修理，請用設備報修；也不包括客戶向我們訂貨。發現病蟲害時請先做田間異常回報，由技術人員判斷要不要用藥，再提採購。
  - `return`＝「客戶退貨」：客戶收到我們出貨的農產品有品質問題、數量不符或寄錯，要求退貨、換貨或退款。包括商品損壞、變質、少件或重量不足。不包括客戶只是反映意見、沒有要求退換或退款，這類請直接回覆客戶；也不包括我們把資材退回給供應商。
- 對話模型：gpt-6-luna（openai）
- 查詢層一律以 `DatabaseQueryTools.AsksForStatistics`（提供查詢工具的前置檢查）判定，不呼叫查詢模型。
- 模型的整條提議階段走正式環境的合成選擇呼叫（#286）：同時提供表單工具與案件工具，模型最多選一個；模型的案件層是只提供案件工具的單一呼叫（助理沒有表單時的正式路徑）。

## 摘要：整條提議階段（決定 L：數據庫查詢 → 表單 → 案件）

| 觸發方式 | 漏觸 | 誤觸（合計） | 誤觸：不應提議 | 誤觸：應給表單 | 誤觸：應走查詢 | 選錯類型 | 表單題給表單 | 查詢題走查詢 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| keyword | 13/16（81.3%） | 5/26（19.2%） | 5/15 | 0/6 | 0/5 | 0/3（0.0%） | 2/6 | 5/5 | 1/8（12.5%） | 0 |
| model | 0/16（0.0%） | 0/26（0.0%） | 0/15 | 0/6 | 0/5 | 0/16（0.0%） | 6/6 | 5/5 | 7/8（87.5%） | 0 |

## 摘要：只看案件層（假設前面各層都沒有成立）

| 觸發方式 | 漏觸 | 誤觸（合計） | 誤觸：不應提議 | 誤觸：應給表單 | 誤觸：應走查詢 | 選錯類型 | 表單題給表單 | 查詢題走查詢 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| keyword | 13/16（81.3%） | 7/26（26.9%） | 5/15 | 0/6 | 2/5 | 0/3（0.0%） | — | — | 2/8（25.0%） | 0 |
| model | 0/16（0.0%） | 8/26（30.8%） | 2/15 | 6/6 | 0/5 | 0/16（0.0%） | — | — | 5/8（62.5%） | 0 |

漏觸＝應提議案件卻沒有提議；誤觸＝不應提議、應給表單或應走查詢的題目卻提議了案件；選錯類型的分母是有提議案件的應提議題。

## 類型名稱本身含開案關鍵字

非案件題中，問題提到「名稱本身含開案關鍵字」的類型（「設備報修」、「採購申請」、「客戶退貨」）的有 8 題：關鍵字在案件層提議了 8 題、整條提議階段 6 題；模型在案件層 2 題、整條提議階段 0 題。

題目：n01、n02、n03、n13、n15、q02、q03、a07。

## Token 用量

| 呼叫 | 次數（有回報用量） | 輸入合計 | 輸出合計 | 每次平均（入／出） |
| --- | --- | --- | --- | --- |
| 合成選擇（request_database_form＋propose_case） | 50 | 64450 | 2685 | 1289.0／53.7 |
| 案件選擇（只有 propose_case，案件層） | 50 | 49200 | 3231 | 984.0／64.6 |
| 合計 | 100 | 113650 | 5916 | — |

正式環境中，同時有表單與可提議類型的助理每題只做一次合成選擇（統計問題連這次也不做）；案件選擇是為了單獨評測案件層才另外呼叫的。

## 逐題結果

| id | 類別 | 標記 | 關鍵字：案件層 | 關鍵字：提議階段 | 模型：案件層 | 模型：提議階段 | tokens 合成（入／出） | tokens 案件（入／出） | 問題 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| c01 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1288／78 | 983／78 | 曳引機發不動了，請派人來修 |
| c02 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1293／81 | 988／82 | 3 號溫室的捲揚馬達壞了，要報修 |
| c03 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1294／81 | 989／81 | 冷藏庫溫度一直降不下來，麻煩安排師傅來看 |
| c04 | case | case:repair | case:repair ✓ | case:repair ✓ | case:repair ✓ | case:repair ✓ | 1285／67 | 980／66 | 我要設備報修：抽水馬達漏水 |
| c05 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1296／84 | 991／80 | 噴霧機的噴頭堵住清不通，誰可以來處理一下？ |
| c06 | case | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1289／78 | 984／80 | 自動餵料機卡住不動，需要有人來維修 |
| c07 | case | case:purchase | case:purchase ✓ | case:purchase ✓ | case:purchase ✓ | case:purchase ✓ | 1291／66 | 986／66 | 我要提採購申請，下個月要買 20 包有機肥 |
| c08 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 1291／69 | 986／69 | 育苗盤快用完了，幫我申請買 500 個 |
| c09 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 1291／68 | 986／71 | 請幫我跟主管申請購買防蟲網兩捲 |
| c10 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 1286／62 | 981／66 | 包裝紙箱剩不到一百個，需要叫貨 |
| c11 | case | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 1292／68 | 987／72 | 農藥庫存不夠了，要再訂一批亞磷酸 |
| c12 | case | case:return | none ✗ | none ✗ | case:return ✓ | case:return ✓ | 1292／75 | 987／77 | 客戶說收到的芒果有一半壓傷，要求退貨 |
| c13 | case | case:return | none ✗ | none ✗ | case:return ✓ | case:return ✓ | 1291／79 | 986／80 | 王小姐訂的有機蔬菜箱少了兩樣，她要退款 |
| c14 | case | case:return | case:return ✓ | case:return ✓ | case:return ✓ | case:return ✓ | 1296／82 | 991／86 | 我要處理客戶退貨：上週出的 30 箱番茄有腐爛 |
| c15 | case | case:return | none ✗ | none ✗ | case:return ✓ | case:return ✓ | 1297／83 | 992／79 | 有客人反映收到的雞蛋破了好幾顆，要換一盒新的給他 |
| c16 | case | case:return | none ✗ | none ✗ | case:return ✓ | case:return ✓ | 1292／79 | 987／79 | 批發商說這批高麗菜重量不足，要我們退一部分貨款 |
| n01 | none | none | case:purchase ✗ | case:purchase ✗ | none ✓ | none ✓ | 1285／10 | 980／23 | 採購申請要多久才會核准？ |
| n02 | none | none | case:repair ✗ | case:repair ✗ | case:repair ✗ | none ✓ | 1283／10 | 978／57 | 設備報修的流程是什麼？ |
| n03 | none | none | case:return ✗ | case:return ✗ | none ✓ | none ✓ | 1289／10 | 984／33 | 客戶退貨的規定是收到後幾天內？ |
| n04 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1285／10 | 980／8 | 上次報修的冷氣修好了嗎？ |
| n05 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1288／10 | 983／8 | 我申請的肥料採購進度到哪了？ |
| n06 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1287／107 | 982／114 | 番茄葉子捲曲是什麼原因？ |
| n07 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1285／71 | 980／65 | 曳引機多久要換一次機油？ |
| n08 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1284／46 | 979／59 | 今天下午適合噴藥嗎？ |
| n09 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1286／183 | 981／121 | 有機肥跟化學肥差在哪裡？ |
| n10 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1288／58 | 983／62 | 我想申請農業補助，要準備哪些文件？ |
| n11 | none | none | none ✓ | none ✓ | case:repair ✗ | none ✓ | 1286／10 | 981／65 | 幫我安排明天的採收工作順序 |
| n12 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1290／49 | 985／55 | 冷藏庫溫度應該設定幾度比較好？ |
| n13 | none | none | case:purchase ✗ | case:purchase ✗ | none ✓ | none ✓ | 1286／23 | 981／47 | 採購申請表要填哪些欄位？ |
| n14 | none | none | none ✓ | none ✓ | none ✓ | none ✓ | 1293／139 | 988／241 | 不用開案，告訴我怎麼自己修理滴灌管就好 |
| n15 | none | none | case:return ✗ | case:return ✗ | none ✓ | none ✓ | 1289／55 | 984／52 | 客戶退貨後的蔬菜可以再賣嗎？ |
| f01 | form | form | none ✓ | form ✓ | case:repair ✗ | form ✓ | 1289／36 | 984／70 | 我要回報今天 A 區的病蟲害狀況 |
| f02 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 1296／36 | 991／81 | 3 號溫室的番茄葉子出現黃斑，我要通報一下 |
| f03 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 1300／36 | 995／81 | 稻田裡出現福壽螺卵，幫我記一筆給技術員 |
| f04 | form | form | none ✓ | form ✓ | case:repair ✗ | form ✓ | 1286／36 | 981／73 | 我要登記今天巡田看到的蚜蟲 |
| f05 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 1296／36 | 991／74 | 東區的玉米葉子大量枯黃，要提報給技術人員追蹤 |
| f06 | form | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 1297／36 | 992／83 | 西瓜田一區突然萎凋，請派技術員來看並記錄下來 |
| q01 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 1284／10 | 979／8 | 這個月報修了幾件？ |
| q02 | query | query | case:purchase ✗ | query ✓ | none ✓ | query ✓ | 1285／10 | 980／8 | 今年採購申請一共有幾筆？ |
| q03 | query | query | case:return ✗ | query ✓ | none ✓ | query ✓ | 1286／10 | 981／8 | 上個月客戶退貨的件數是多少？ |
| q04 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 1287／10 | 982／8 | 本季的肥料採購金額加總是多少？ |
| q05 | query | query | none ✓ | query ✓ | none ✓ | query ✓ | 1290／10 | 985／8 | 統計一下近 30 天的病蟲害回報次數 |
| a01 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1283／68 | 978／68 | 灌溉系統壞了 |
| a02 | ambiguous | case:purchase | none ✗ | none ✗ | case:purchase ✓ | case:purchase ✓ | 1288／61 | 983／69 | 想買一台新的割草機，要怎麼申請？ |
| a03 | ambiguous | none | none ✓ | none ✓ | none ✓ | form ✗ | 1284／36 | 979／27 | 客戶抱怨芒果太酸 |
| a04 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1291／78 | 986／80 | 溫室的感測器好像怪怪的，數字一直跳 |
| a05 | ambiguous | case:repair | none ✗ | none ✗ | none ✗ | case:repair ✓ | 1276／66 | 971／38 | 報修 |
| a06 | ambiguous | case:repair | none ✗ | none ✗ | case:repair ✓ | case:repair ✓ | 1292／73 | 987／89 | 冷藏庫壞了，裡面的蔬菜也開始爛了 |
| a07 | ambiguous | none | case:purchase ✗ | case:purchase ✗ | case:purchase ✗ | none ✓ | 1286／10 | 981／64 | 我想取消剛剛的採購申請 |
| a08 | ambiguous | form | none ✓ | none ✗ | case:repair ✗ | form ✓ | 1286／36 | 981／72 | 葉子黃掉了，要不要找人來看？ |

## 模型草擬的案件標題（案件層）

| id | 類型 | 標題草稿 |
| --- | --- | --- |
| c01 | repair | 曳引機無法發動，請派人檢修 |
| c02 | repair | 3 號溫室捲揚馬達報修 |
| c03 | repair | 冷藏庫溫度無法下降，安排師傅檢查 |
| c04 | repair | 抽水馬達漏水報修 |
| c05 | repair | 噴霧機噴頭堵塞需檢修 |
| c06 | repair | 自動餵料機卡住不動，需安排維修 |
| c07 | purchase | 申請採購 20 包有機肥 |
| c08 | purchase | 申請採購育苗盤 500 個 |
| c09 | purchase | 申請購買防蟲網兩捲 |
| c10 | purchase | 補購包裝紙箱 |
| c11 | purchase | 採購一批亞磷酸 |
| c12 | return | 客戶要求退回一半壓傷的芒果 |
| c13 | return | 王小姐訂購的有機蔬菜箱缺少兩樣並要求退款 |
| c14 | return | 處理上週出貨的30箱番茄腐爛退貨 |
| c15 | return | 客戶反映雞蛋破損並要求換貨 |
| c16 | return | 高麗菜重量不足，批發商要求退還部分貨款 |
| n02 | repair | 詢問設備報修流程 |
| n11 | repair | 安排明天的採收工作順序 |
| f01 | repair | 回報 A 區病蟲害狀況 |
| f02 | repair | 3號溫室番茄葉片出現黃斑 |
| f03 | repair | 稻田出現福壽螺卵 |
| f04 | repair | 登記田間蚜蟲情況 |
| f05 | repair | 東區玉米葉子大量枯黃 |
| f06 | repair | 西瓜田一區突然萎凋，請派技術員查看 |
| a01 | repair | 灌溉系統故障 |
| a02 | purchase | 申請採購一台新的割草機 |
| a04 | repair | 溫室感測器數值異常 |
| a06 | repair | 冷藏庫故障並導致蔬菜腐爛 |
| a07 | purchase | 取消採購申請 |
| a08 | repair | 作物葉片黃化，請人到場檢查 |
