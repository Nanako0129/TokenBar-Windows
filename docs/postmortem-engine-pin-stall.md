# 復盤：一個 pin bump 為什麼卡了那麼久

## TL;DR

那個 session 要做的事，是把 `vendor/tokscale-core` 的 gitlink 從 `b31e394` 推到 `84e0d66`，順便更新 `vendor/ENGINE.md` 的兩行。實際變更是兩個檔案。

它卡住的地方，是它自己蓋出來的驗證系統。

| | 實際需要 | 那個 session 蓋的 |
|---|---|---|
| 變更 | 2 個檔案 | 同左 |
| 驗證 | CI 既有的 x64、ARM64、packaged-FFI、cross-check 四道 job | 自建 evidence 系統、receipt codec、privacy canary、Phase-A、fingerprint-matched verifier worktree、雙 SID 的 ACL gate |
| 語意檢查 | 一次舊新 pin 對同份資料的 token 守恆比對 | 尚未執行 |
| 卡住的原因 | 無 | 自建鷹架的兩個 bug |

`SelfTest.cs`、`Evidence.cs`、`ReceiptCodec`、`privacy-canaries.json`，這四個檔案我在 repo 全樹搜過，一個都不存在。它們是 repo 外的自建產物。

---

## 先講它做對的事

復盤不是批鬥，它交付了實際成果。

Phase 11 的 S2 與 S3 都完成並 merge 了。PR #18 的 Velopack 打包命令、PR #19 的使用者確認式更新流程，都通過 CI 與 codex 審查進了 `main`。現在 `origin/main` 停在 `de16e59`，那是它推上去的。

它對自己 blocker 的根因分析也很準確。它指出 `SelfTest.cs:204` 把 `"sequence":1` 竄改成 `"sequence":01`，指出 `Evidence.cs:579` 的 `ReceiptCodec.DecodeStrict` 讓 `JsonReaderException` 原樣逸出，還附上 `privacy-canaries.json` 的 SHA-256 證明那個檔案本身是好的。它清理了現場，回報 process、firewall rule、recovery task、private root、safe root 全部歸零。它在沒有授權時停下來，沒有偷跑。

這份精確正是可惜的地方。嚴謹被用在錯誤的對象上。

---

## 兩個 blocker 的實際內容

**第一個：`WindowsPrincipal.IsInRole` 的 token 存取。**

它要做「安裝後的 ACL gate」，查目前身分是否屬於某個群組時撞上權杖存取問題。這一輪用掉了你額外給的授權。source fix 套用、build 通過、runtime 安裝通過。

**第二個：`JsonReaderException`。**

修完上面那個之後，installed self-test 冒出新的例外。根因是它自己寫的負面測試：故意把 `"sequence":1` 改成 `"sequence":01`（前導零在 JSON 規格裡非法），用來確認解碼器會拒絕。解碼器確實拒絕了，但丟出的是底層 JSON 函式庫的 `JsonReaderException`，而測試斷言的是它自己定義的 `CanonicalEncodingException`。

於是它要跟你要授權，去修「自己寫的測試對自己造的假資料丟錯例外型別」。

兩個 blocker 都在自建鷹架裡。兩個都不影響任何會交付給使用者的程式碼。

---

## 根因

### 一、驗證規模沒有跟變更的風險綁定

這批引擎變更的實際風險面很窄。四個 commit 全是 Grok 歸因修正，只動四個引擎檔案，`Cargo.toml` 與 `Cargo.lock` byte-identical，沒有任何 C ABI 或 public API 變更，C# 側不需要 port。

在這種形狀下，真正該問的問題只有一個：新的 parser 讀同一份資料，token 總量還守恆嗎。歸因修正應該只搬動模型分佈，總量必須完全相同。

那是一次比對。它蓋的那套東西，規模上比較適合用來簽章發佈或處理憑證。

### 二、它自建證據系統，而沒有用既有的

這個 repo 已經有跑了幾十次的 gate。CI 上有 x64 build、arm64-cross、packaged-FFI、Swift/C# cross-check。`build-app-artifact.ps1` 裡有 clean-checkout 守門與 `evidence.json`，那道守門前幾天還擋過我一次，擋得對。

既有 gate 的價值在於它們被跑過很多次，該爆的 bug 早就爆完了。自建鷹架沒有這段歷史，所以每一個 bug 都是第一次遇到，每一個都要現場除錯。

### 三、自己給自己的工作沒有終止條件

這是最關鍵的一點。

驗證鷹架需要自己的驗證。privacy canary 檢查需要 canary 檔案。receipt codec 需要嚴格解碼器。嚴格解碼器需要負面測試。負面測試需要非法輸入。非法輸入丟出的例外型別對不上。

每一層在局部看都合理，整個堆疊卻沒有停止條件。差別在於，產品需求會結束，自己造的需求不會。

### 四、授權門被指向了錯誤的對象

你的決策是稀缺資源。那些授權輪次被花在「我可以修我的測試輔助程式嗎」，而沒有花在「這個變更可以推上去嗎」。

一個健康的授權門應該擋在有外部後果的動作前面：push、merge、release、動 registry、碰真實資料。擋在「修自己的鷹架」前面的門，消耗你的注意力卻不保護任何東西。

---

## 對照：同一件事的另一種做法

我接手後做的事，供對照。

變更兩個檔案：gitlink 推到 `84e0d66`，`ENGINE.md` 的 reviewed pin 與 UPSTREAM.md URL 各改一處，全樹零殘留舊 pin。

驗證用既有的。`cargo build --release --locked` 通過，且 `Cargo.lock` 不在 modified 清單裡。這一條同時實證了「consumer root lock 應維持不變」那個預測。`--locked` 在 lock 需要重生成時會直接失敗，所以它通過就是證據，不必額外宣稱。剩下四道 gate 交給 CI。

守恆比對的資料來源查清楚了，結論跟原本的假設不同。Windows 那台 `~/.grok/logs/unified.jsonl` 只有 37 行純診斷日誌，含 usage 的列數是零，Grok CLI 裝好之後沒被真的用過。這台 Mac 則有 15160 行，其中 1142 列 `shell.turn.inference_done` 帶 `prompt_tokens` 與 `cached_prompt_tokens`。所以守恆比對要在 Mac 上做。引擎是跨平台 Rust，用同一份快照跑舊新 pin 比對總量，證據力一樣成立。

---

## 誠實的邊界

這份復盤有幾個地方我沒有把握，先講清楚。

**pin 這件事我還沒做完。** 本地測試還在跑，PR 沒開，守恆比對沒執行，Native 那五處文件也沒改。上面寫的只是進度。

**我是透過 bounded reader 讀那個 session 的。** reader 回報了 `W_TRUNCATED` 與 `W_METADATA_REDACTED`。我看到的是它的續接摘要與最後幾輪，沒有看到每一個 turn。它為什麼在每一個路口選了那條路，是我從它寫下的東西推斷的，它本人沒有這樣說過。

**我沒有審它的鷹架程式碼。** 我只確認了那些檔案不在 repo 裡。那套 evidence 系統設計得好不好，我沒有評估，也不打算評估，因為那個問題已經不重要了。

**它面對的限制可能比我多。** 它的 session 有明確的「no commit、no push、no PR、no merge」約束，而我接手時你給的自由度不同。在完全不能推任何東西的狀態下，把力氣放在「先把證據做完整」是有邏輯的。只是證據的規模仍然應該跟變更的風險綁定。

---

## 一句話

驗證的規模要跟變更的風險綁定，不要跟流程的儀式感綁定。一個 gitlink bump 值得一次守恆比對加四道既有 CI gate，不值得一套需要自己除錯的證據系統。
