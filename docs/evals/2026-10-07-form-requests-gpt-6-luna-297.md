# 表單請求觸發評測（#164）

- 執行時間：2026-10-07 13:33:57 +08:00（144.6 秒）
- 題庫：`apps/api/eval/form-requests`（指紋 `4d4528733d5d`，48 題）
- 範例表單：「田間異常回報」，收集目的：記錄田區病蟲害、作物與設備異常，方便技術人員追蹤處理。
- 對話模型：gpt-6-luna（openai）
- 合成呼叫（#286）：表單工具之外，同時提供這些可提議的案件類型（正式環境中助理同時有表單與可提議類型時的路徑），以及明確的「都不符合」工具 `no_matching_type`（#297）：
  - `repair`＝「設備報修」：農機、灌溉、溫室、冷藏庫等設備故障或損壞，需要派人到場維修。
  - `purchase`＝「採購申請」：購買肥料、農藥、資材或設備，需要主管核准後才能下單。
  - `return`＝「客戶退貨」：客戶收到的農產品有品質問題、數量不符或寄錯，要求退貨、換貨或退款。

## 摘要

| 觸發方式 | 漏觸（應給表單卻沒給） | 誤觸（不應給卻給了） | 正反題正確率 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- |
| keyword | 10/18（55.6%） | 8/18（44.4%） | 50.0% | 3/12（25.0%） | 0 |
| model | 0/18（0.0%） | 0/18（0.0%） | 100.0% | 12/12（100.0%） | 0 |
| model＋案件類型（合成呼叫） | 0/18（0.0%） | 0/18（0.0%） | 100.0% | 10/12（83.3%） | 0 |

模型每題平均用量：輸入 419.5、輸出 58.0 tokens。

合成呼叫改提議案件（沒有給表單）的有 1 題：正題 0、反題 0、模糊題 1（a03→repair）。

合成呼叫明確選「都不符合」的有 22 題：正題 0、反題 18、模糊題 4。

合成呼叫每題平均用量：輸入 1246.5、輸出 26.6 tokens。

## 逐題結果

| id | 類別 | 標記 | 關鍵字 | 模型 | 合成呼叫 | 問題 |
| --- | --- | --- | --- | --- | --- | --- |
| p01 | positive | form | form ✓ | form ✓ | form ✓ | 我要回報今天 A 區的病蟲害狀況 |
| p02 | positive | form | form ✓ | form ✓ | form ✓ | 幫我登記一下今天東區的異常 |
| p03 | positive | form | form ✓ | form ✓ | form ✓ | 我想填寫田間巡查發現的問題 |
| p04 | positive | form | form ✓ | form ✓ | form ✓ | 給我異常回報的表單 |
| p05 | positive | form | none ✗ | form ✓ | form ✓ | 3 號溫室的番茄葉子出現黃斑，我要通報一下 |
| p06 | positive | form | none ✗ | form ✓ | form ✓ | 東邊那塊田今天發現蚜蟲，要怎麼記下來給技術員？ |
| p07 | positive | form | none ✗ | form ✓ | form ✓ | 剛剛發現滴灌管破了一段，幫我記一筆 |
| p08 | positive | form | none ✗ | form ✓ | form ✓ | 稻田裡出現福壽螺卵，我要提報 |
| p09 | positive | form | none ✗ | form ✓ | form ✓ | 幫我開一張異常單，雞舍風扇停了 |
| p10 | positive | form | none ✗ | form ✓ | form ✓ | 今天巡田看到稻熱病的病斑，想登錄進系統 |
| p11 | positive | form | none ✗ | form ✓ | form ✓ | 怎麼把今天的病蟲害狀況報上去？ |
| p12 | positive | form | form ✓ | form ✓ | form ✓ | 想回報一下 B 棟溫室的濕度感測器壞了 |
| p13 | positive | form | none ✗ | form ✓ | form ✓ | 幫我記錄：第二批芒果出現炭疽病 |
| p14 | positive | form | form ✓ | form ✓ | form ✓ | 我要填表，北區的果樹有天牛蛀孔 |
| p15 | positive | form | form ✓ | form ✓ | form ✓ | 我想留下資料，讓技術員來看一下這片葉子 |
| p16 | positive | form | form ✓ | form ✓ | form ✓ | 我要提交資料：今天噴藥後有三排葉片焦掉 |
| p17 | positive | form | none ✗ | form ✓ | form ✓ | 西瓜田有一區突然大量萎凋，請幫我送給技術人員處理 |
| p18 | positive | form | none ✗ | form ✓ | form ✓ | 颱風過後網室破了兩處，要通知你們 |
| n01 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 番茄葉子出現黃斑是什麼原因？ |
| n02 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 蚜蟲要用什麼藥防治比較好？ |
| n03 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 上次回報的病蟲害處理好了嗎？ |
| n04 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 報名下週的植保講習要去哪裡？ |
| n05 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 福壽螺有什麼天然的防治方法？ |
| n06 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 今天下午適合噴藥嗎？ |
| n07 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 水稻追肥一分地大概要放多少？ |
| n08 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 你們的服務時間是幾點到幾點？ |
| n09 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 我不想填表單，直接告訴我怎麼處理就好 |
| n10 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 登記有機驗證多久要更新一次？ |
| n11 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 稻熱病跟胡麻葉枯病要怎麼分辨？ |
| n12 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 溫室的濕度應該控制在多少比較好？ |
| n13 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 上個月回報的資料可以修改嗎？ |
| n14 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 雞舍溫度太高要怎麼降溫？ |
| n15 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 我昨天已經回報過了，還需要再填一次嗎？ |
| n16 | negative | none | none ✓ | none ✓ | none ✓（都不符合） | 芒果炭疽病在什麼天氣下容易發生？ |
| n17 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 本月回報了幾筆異常？ |
| n18 | negative | none | form ✗ | none ✓ | none ✓（都不符合） | 表單送出後誰會看到我的資料？ |
| a01 | ambiguous | form | none ✗ | form ✓ | form ✓ | 葉子黃掉了怎麼辦？要不要跟你們說一聲？ |
| a02 | ambiguous | form | none ✗ | form ✓ | form ✓ | 田裡好像有病蟲害 |
| a03 | ambiguous | form | none ✗ | form ✓ | case:repair ✗ | 灌溉系統壞了 |
| a04 | ambiguous | form | form ✓ | form ✓ | form ✓ | 要怎麼回報病蟲害？ |
| a05 | ambiguous | form | none ✗ | form ✓ | form ✓ | 可以幫我跟技術員說一下 C 區有積水嗎？ |
| a06 | ambiguous | none | form ✗ | none ✓ | none ✓（都不符合） | 異常回報表有哪些欄位？ |
| a07 | ambiguous | none | none ✓ | none ✓ | none ✓（都不符合） | 我想取消剛剛送出的資料 |
| a08 | ambiguous | form | none ✗ | form ✓ | form ✓ | 這週的巡田紀錄 |
| a09 | ambiguous | form | form ✓ | form ✓ | form ✓ | 回報 |
| a10 | ambiguous | none | form ✗ | none ✓ | none ✓（都不符合） | 填寫表單時日期格式要怎麼寫？ |
| a11 | ambiguous | form | none ✗ | form ✓ | none ✗（都不符合） | 剛剛拍了一張病斑的照片，要傳給誰？ |
| a12 | ambiguous | form | none ✗ | form ✓ | form ✓ | 我們家的雞今天精神不太好 |
