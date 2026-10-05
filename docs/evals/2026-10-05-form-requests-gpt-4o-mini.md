# 表單請求觸發評測（#164）

- 執行時間：2026-10-05 14:16:15 +08:00（43.9 秒）
- 題庫：`apps/api/eval/form-requests`（指紋 `4d4528733d5d`，48 題）
- 範例表單：「田間異常回報」，收集目的：記錄田區病蟲害、作物與設備異常，方便技術人員追蹤處理。
- 對話模型：gpt-4o-mini（openai）

## 摘要

| 觸發方式 | 漏觸（應給表單卻沒給） | 誤觸（不應給卻給了） | 正反題正確率 | 模糊題與標記一致 | 模型呼叫失敗 |
| --- | --- | --- | --- | --- | --- |
| keyword | 10/18（55.6%） | 8/18（44.4%） | 50.0% | 3/12（25.0%） | 0 |
| model | 1/18（5.6%） | 0/18（0.0%） | 97.2% | 5/12（41.7%） | 0 |

模型每題平均用量：輸入 335.5、輸出 18.8 tokens。

## 逐題結果

| id | 類別 | 標記 | 關鍵字 | 模型 | 問題 |
| --- | --- | --- | --- | --- | --- |
| p01 | positive | form | form ✓ | form ✓ | 我要回報今天 A 區的病蟲害狀況 |
| p02 | positive | form | form ✓ | form ✓ | 幫我登記一下今天東區的異常 |
| p03 | positive | form | form ✓ | none ✗ | 我想填寫田間巡查發現的問題 |
| p04 | positive | form | form ✓ | form ✓ | 給我異常回報的表單 |
| p05 | positive | form | none ✗ | form ✓ | 3 號溫室的番茄葉子出現黃斑，我要通報一下 |
| p06 | positive | form | none ✗ | form ✓ | 東邊那塊田今天發現蚜蟲，要怎麼記下來給技術員？ |
| p07 | positive | form | none ✗ | form ✓ | 剛剛發現滴灌管破了一段，幫我記一筆 |
| p08 | positive | form | none ✗ | form ✓ | 稻田裡出現福壽螺卵，我要提報 |
| p09 | positive | form | none ✗ | form ✓ | 幫我開一張異常單，雞舍風扇停了 |
| p10 | positive | form | none ✗ | form ✓ | 今天巡田看到稻熱病的病斑，想登錄進系統 |
| p11 | positive | form | none ✗ | form ✓ | 怎麼把今天的病蟲害狀況報上去？ |
| p12 | positive | form | form ✓ | form ✓ | 想回報一下 B 棟溫室的濕度感測器壞了 |
| p13 | positive | form | none ✗ | form ✓ | 幫我記錄：第二批芒果出現炭疽病 |
| p14 | positive | form | form ✓ | form ✓ | 我要填表，北區的果樹有天牛蛀孔 |
| p15 | positive | form | form ✓ | form ✓ | 我想留下資料，讓技術員來看一下這片葉子 |
| p16 | positive | form | form ✓ | form ✓ | 我要提交資料：今天噴藥後有三排葉片焦掉 |
| p17 | positive | form | none ✗ | form ✓ | 西瓜田有一區突然大量萎凋，請幫我送給技術人員處理 |
| p18 | positive | form | none ✗ | form ✓ | 颱風過後網室破了兩處，要通知你們 |
| n01 | negative | none | none ✓ | none ✓ | 番茄葉子出現黃斑是什麼原因？ |
| n02 | negative | none | none ✓ | none ✓ | 蚜蟲要用什麼藥防治比較好？ |
| n03 | negative | none | form ✗ | none ✓ | 上次回報的病蟲害處理好了嗎？ |
| n04 | negative | none | form ✗ | none ✓ | 報名下週的植保講習要去哪裡？ |
| n05 | negative | none | none ✓ | none ✓ | 福壽螺有什麼天然的防治方法？ |
| n06 | negative | none | none ✓ | none ✓ | 今天下午適合噴藥嗎？ |
| n07 | negative | none | none ✓ | none ✓ | 水稻追肥一分地大概要放多少？ |
| n08 | negative | none | none ✓ | none ✓ | 你們的服務時間是幾點到幾點？ |
| n09 | negative | none | form ✗ | none ✓ | 我不想填表單，直接告訴我怎麼處理就好 |
| n10 | negative | none | form ✗ | none ✓ | 登記有機驗證多久要更新一次？ |
| n11 | negative | none | none ✓ | none ✓ | 稻熱病跟胡麻葉枯病要怎麼分辨？ |
| n12 | negative | none | none ✓ | none ✓ | 溫室的濕度應該控制在多少比較好？ |
| n13 | negative | none | form ✗ | none ✓ | 上個月回報的資料可以修改嗎？ |
| n14 | negative | none | none ✓ | none ✓ | 雞舍溫度太高要怎麼降溫？ |
| n15 | negative | none | form ✗ | none ✓ | 我昨天已經回報過了，還需要再填一次嗎？ |
| n16 | negative | none | none ✓ | none ✓ | 芒果炭疽病在什麼天氣下容易發生？ |
| n17 | negative | none | form ✗ | none ✓ | 本月回報了幾筆異常？ |
| n18 | negative | none | form ✗ | none ✓ | 表單送出後誰會看到我的資料？ |
| a01 | ambiguous | form | none ✗ | none ✗ | 葉子黃掉了怎麼辦？要不要跟你們說一聲？ |
| a02 | ambiguous | form | none ✗ | form ✓ | 田裡好像有病蟲害 |
| a03 | ambiguous | form | none ✗ | form ✓ | 灌溉系統壞了 |
| a04 | ambiguous | form | form ✓ | none ✗ | 要怎麼回報病蟲害？ |
| a05 | ambiguous | form | none ✗ | none ✗ | 可以幫我跟技術員說一下 C 區有積水嗎？ |
| a06 | ambiguous | none | form ✗ | none ✓ | 異常回報表有哪些欄位？ |
| a07 | ambiguous | none | none ✓ | none ✓ | 我想取消剛剛送出的資料 |
| a08 | ambiguous | form | none ✗ | none ✗ | 這週的巡田紀錄 |
| a09 | ambiguous | form | form ✓ | none ✗ | 回報 |
| a10 | ambiguous | none | form ✗ | none ✓ | 填寫表單時日期格式要怎麼寫？ |
| a11 | ambiguous | form | none ✗ | none ✗ | 剛剛拍了一張病斑的照片，要傳給誰？ |
| a12 | ambiguous | form | none ✗ | none ✗ | 我們家的雞今天精神不太好 |
