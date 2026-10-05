# #164 對話表單請求觸發方式評測：關鍵字門檻 vs 模型選擇工具

> **狀態：模型結果待真實模型。** 目前沒有可用的真實模型金鑰，本文件只有關鍵字門檻的結果（不需要模型即可計算）。模型欄位等真實模型就緒後，以下方「如何以真實模型執行」補上；不得以 `Fake` 的結果代替。

## 1. 目的

#148 讓對話中的表單請求由伺服器端關鍵字門檻觸發（`AssistantFormRequestRules.AsksForForm`：填寫、填表、表單、回報、登記、報名、留下資料、提交資料），不經模型。#164 新增「由模型從伺服器列出的表單中選擇 `request_database_form`」的觸發方式（設定 `Chat:FormRequests:Trigger = Model`），本評測比較兩者的**漏觸**（應給表單卻沒給）與**誤觸**（不應給卻給了）比率，作為是否切換預設值的依據。

## 2. 方法

- **題庫**：`apps/api/eval/form-requests/questions.json`，48 題繁體中文、農業助理情境的提問，對照同一份範例表單「田間異常回報」（收集目的：記錄田區病蟲害、作物與設備異常，方便技術人員追蹤處理）。
  - `positive` 18 題：使用者想回報／登記／交出這份表單收集的資料；其中多題刻意使用關鍵字以外的說法（通報、提報、記一筆、登錄、報上去…）。
  - `negative` 18 題：知識問題、詢問規則或進度、查詢過去紀錄，或關鍵字用在別的事情上（報名講習、登記有機驗證、「我不想填表單」）。
  - `ambiguous` 12 題：意圖不明確，標記為標記者的最佳判斷；只報告「與標記一致」的比例，不計入漏觸／誤觸率。
- **判定**：關鍵字＝`AsksForForm(question)`；模型＝以正式環境相同的工具定義（`AssistantFormRequestRules.Declaration`，`databaseId` 為只含範例表單 id 的 `enum`）與提示（`SelectionPrompt`）呼叫一次對話模型，只有「呼叫 `request_database_form` 且 id 是列出的那一個」才算給表單（`ParseCall`，與正式環境同一個檢查）；其他工具或 id 視為不給表單並在逐題表標示；模型呼叫失敗只計數、不判定。
- **限制**：題庫是為了找出關鍵字門檻的弱點而寫的，比率不是實際使用時的比率；樣本小（每類 18 題），一題約 5.6 個百分點。屬於 #149 統計查詢範圍的問題（n17「本月回報了幾筆異常？」）在這裡只評表單觸發；正式環境中查詢會先執行。

## 3. 如何以真實模型執行

```sh
# Development 環境；不需要資料庫
export Ai__Chat__Provider=OpenAI          # 或 AzureOpenAI／OpenAICompatible（另設 Ai__Chat__Endpoint）
export Ai__Chat__Model=<模型或部署名稱>
export Ai__Chat__ApiKey=<金鑰>
dotnet run --project apps/api/src/SmartAgri.Api -- eval-form-requests
# 報告寫到 docs/evals/<日期>-form-requests-<模型>.md；只評關鍵字：加 --trigger keyword
```

模型的呼叫不寫入 `ModelInvocations`（評測沒有組織），報告會列出每題平均 token 用量。跑完後把摘要表的模型列與逐題的模型欄補進本文件第 4 節，並在第 5 節寫下是否切換預設值的建議。

## 4. 結果

### 4.1 關鍵字門檻（2026-10-05，題庫指紋 `4d4528733d5d`）

| 觸發方式 | 漏觸（應給表單卻沒給） | 誤觸（不應給卻給了） | 正反題正確率 | 模糊題與標記一致 |
| --- | --- | --- | --- | --- |
| 關鍵字 | 10/18（55.6%） | 8/18（44.4%） | 50.0% | 3/12（25.0%） |
| 模型 | **待真實模型** | **待真實模型** | **待真實模型** | **待真實模型** |

觀察（僅關鍵字）：

- **漏觸**集中在關鍵字清單以外的動詞：通報、提報、記一筆、記錄、登錄、報上去、開單、通知、送給技術人員。補關鍵字可以改善，但每加一個詞都會帶來新的誤觸（例如「記錄」也出現在「查紀錄」）。
- **誤觸**來自「關鍵字出現但意圖不是要填」：問進度（上次回報的處理好了嗎）、問規則（回報的資料可以修改嗎、表單送出後誰會看到）、別的事情（報名講習、登記有機驗證）、甚至明確拒絕（我不想填表單）。這類問題只看字面無法分辨，是模型選擇最可能改善的部分。
- 模糊題中，只陳述異常而沒有明說要回報的問題（田裡好像有病蟲害、灌溉系統壞了），關鍵字一律不給表單。

### 4.2 模型選擇工具

**待真實模型。** `Fake` 模型在沒有指示詞時照關鍵字門檻決定，所以它的結果與 4.1 相同，只證明評測流程可以執行（`EvalFormRequestsCommandTests`），不代表模型的表現。

### 4.3 逐題結果（關鍵字）

| id | 類別 | 標記 | 關鍵字 | 問題 |
| --- | --- | --- | --- | --- |
| p01 | positive | form | form ✓ | 我要回報今天 A 區的病蟲害狀況 |
| p02 | positive | form | form ✓ | 幫我登記一下今天東區的異常 |
| p03 | positive | form | form ✓ | 我想填寫田間巡查發現的問題 |
| p04 | positive | form | form ✓ | 給我異常回報的表單 |
| p05 | positive | form | none ✗ | 3 號溫室的番茄葉子出現黃斑，我要通報一下 |
| p06 | positive | form | none ✗ | 東邊那塊田今天發現蚜蟲，要怎麼記下來給技術員？ |
| p07 | positive | form | none ✗ | 剛剛發現滴灌管破了一段，幫我記一筆 |
| p08 | positive | form | none ✗ | 稻田裡出現福壽螺卵，我要提報 |
| p09 | positive | form | none ✗ | 幫我開一張異常單，雞舍風扇停了 |
| p10 | positive | form | none ✗ | 今天巡田看到稻熱病的病斑，想登錄進系統 |
| p11 | positive | form | none ✗ | 怎麼把今天的病蟲害狀況報上去？ |
| p12 | positive | form | form ✓ | 想回報一下 B 棟溫室的濕度感測器壞了 |
| p13 | positive | form | none ✗ | 幫我記錄：第二批芒果出現炭疽病 |
| p14 | positive | form | form ✓ | 我要填表，北區的果樹有天牛蛀孔 |
| p15 | positive | form | form ✓ | 我想留下資料，讓技術員來看一下這片葉子 |
| p16 | positive | form | form ✓ | 我要提交資料：今天噴藥後有三排葉片焦掉 |
| p17 | positive | form | none ✗ | 西瓜田有一區突然大量萎凋，請幫我送給技術人員處理 |
| p18 | positive | form | none ✗ | 颱風過後網室破了兩處，要通知你們 |
| n01 | negative | none | none ✓ | 番茄葉子出現黃斑是什麼原因？ |
| n02 | negative | none | none ✓ | 蚜蟲要用什麼藥防治比較好？ |
| n03 | negative | none | form ✗ | 上次回報的病蟲害處理好了嗎？ |
| n04 | negative | none | form ✗ | 報名下週的植保講習要去哪裡？ |
| n05 | negative | none | none ✓ | 福壽螺有什麼天然的防治方法？ |
| n06 | negative | none | none ✓ | 今天下午適合噴藥嗎？ |
| n07 | negative | none | none ✓ | 水稻追肥一分地大概要放多少？ |
| n08 | negative | none | none ✓ | 你們的服務時間是幾點到幾點？ |
| n09 | negative | none | form ✗ | 我不想填表單，直接告訴我怎麼處理就好 |
| n10 | negative | none | form ✗ | 登記有機驗證多久要更新一次？ |
| n11 | negative | none | none ✓ | 稻熱病跟胡麻葉枯病要怎麼分辨？ |
| n12 | negative | none | none ✓ | 溫室的濕度應該控制在多少比較好？ |
| n13 | negative | none | form ✗ | 上個月回報的資料可以修改嗎？ |
| n14 | negative | none | none ✓ | 雞舍溫度太高要怎麼降溫？ |
| n15 | negative | none | form ✗ | 我昨天已經回報過了，還需要再填一次嗎？ |
| n16 | negative | none | none ✓ | 芒果炭疽病在什麼天氣下容易發生？ |
| n17 | negative | none | form ✗ | 本月回報了幾筆異常？ |
| n18 | negative | none | form ✗ | 表單送出後誰會看到我的資料？ |
| a01 | ambiguous | form | none ✗ | 葉子黃掉了怎麼辦？要不要跟你們說一聲？ |
| a02 | ambiguous | form | none ✗ | 田裡好像有病蟲害 |
| a03 | ambiguous | form | none ✗ | 灌溉系統壞了 |
| a04 | ambiguous | form | form ✓ | 要怎麼回報病蟲害？ |
| a05 | ambiguous | form | none ✗ | 可以幫我跟技術員說一下 C 區有積水嗎？ |
| a06 | ambiguous | none | form ✗ | 異常回報表有哪些欄位？ |
| a07 | ambiguous | none | none ✓ | 我想取消剛剛送出的資料 |
| a08 | ambiguous | form | none ✗ | 這週的巡田紀錄 |
| a09 | ambiguous | form | form ✓ | 回報 |
| a10 | ambiguous | none | form ✗ | 填寫表單時日期格式要怎麼寫？ |
| a11 | ambiguous | form | none ✗ | 剛剛拍了一張病斑的照片，要傳給誰？ |
| a12 | ambiguous | form | none ✗ | 我們家的雞今天精神不太好 |

## 5. 建議（待模型結果後更新）

- 預設維持 `Keyword`：與 #148 行為相同，`Fake` 模型的 E2E 不受影響，也不多一次模型呼叫。
- 真實模型結果出來後，若模型的漏觸與誤觸都明顯低於關鍵字，且單次呼叫的成本與延遲可接受（每個有寫入對象的助理，每題多一次選擇呼叫），再由負責人決定是否把正式環境切到 `Model`。模型呼叫失敗時會自動退回關鍵字門檻。
