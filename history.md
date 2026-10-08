# AfkKeeper 開發紀錄 (history.md)

> 這份檔案記錄整個專案的來龍去脈、設計決策與重建方式。
> 重灌電腦後，把這份檔案連同原始碼貼給 Claude，就能快速還原脈絡、繼續開發。

最後更新：2026-06-22

---

## 1. 專案目的

做一個 Windows 小工具，**每隔自訂時間，對「指定的、已經開啟的視窗」送出一個按鍵（預設空白鍵）**。

**原始需求情境：**
- 使用者開著 Roblox 掛機，遊戲約每 15 分鐘需要在裡面「跳一下」（按空白鍵）才不會被判定 idle 踢出。
- 平常還要正常用電腦，所以**不能用全域連點程式**干擾操作。
- 需要評估這工具**不能被當成外掛**。

---

## 2. 關鍵技術評估（重要結論）

### 為什麼不能用「純背景送鍵」
- `PostMessage / SendMessage` 送給視窗 handle 雖然不需切到前景、完全不干擾，
  但 **Roblox 用 DirectInput / Raw Input 讀鍵盤，會忽略這種模擬訊息**，角色不會動。對多數遊戲無效。
- 真正能讓 Roblox 收到的只有 **`SendInput`（真實輸入）**，但它是送給「目前前景視窗」。

### 採用的折衷方案（使用者已選定）
**前景閃切 + 閒置偵測：**
1. 到了預定時間後，先進入「待送出」狀態。
2. 用 `GetLastInputInfo` 偵測使用者是否已閒置 N 秒（手沒在動）。
3. 一旦閒置足夠（或等太久超過上限），就：
   - 記住目前前景視窗 →
   - 用 `AttachThreadInput` 強制把目標視窗切到前景 →
   - 用 **scan code**（`SendInput` + `KEYEVENTF_SCANCODE`）送出按鍵 →
   - **立刻把焦點還給原本的視窗**。
4. 整個過程約 0.2 秒，且專挑使用者手停下的空檔，干擾極小。

### 反作弊（外掛）評估
- 本工具**完全在遊戲程序外部**，只用 Windows 標準 `SendInput` 模擬輸入，
  **不注入 DLL、不讀寫遊戲記憶體、不掛鉤**。
- Roblox 的反作弊 **Hyperion / Byfron 主要抓「程序內部入侵」**，對純外部輸入很難直接判定為外掛
  （OS 層面它和真鍵盤差異很小）。
- **真正風險是違反 Roblox 服務條款（掛機/反 AFK）+ 伺服器端行為偵測**。
  → 因此內建**時間隨機抖動**（± 隨機秒數），避免毫秒不差的規律被當機器人。
- 另：**VM / 虛擬機方案不可行**，因為 Hyperion 會偵測虛擬機並拒絕啟動 Roblox。

---

## 3. 技術選型

| 項目 | 選擇 | 原因 |
|---|---|---|
| 語言/框架 | **C# + WinForms（.NET Framework 4.x）** | 產出單一 exe、有正規 GUI、最像「軟體」 |
| 編譯器 | **Windows 內建的 `csc.exe`** | 機器上**沒裝 .NET SDK**；csc 是 Windows 內建，免安裝任何環境 |
| 執行需求 | 無 | .NET Framework 4.x 是 Windows 10/11 內建，雙擊即用 |

> 注意：內建 csc 只支援到 **C# 5**（不能用 `out _` 捨棄變數、字串內插 `$"..."` 等新語法）。

---

## 4. 檔案清單

| 檔案 | 用途 |
|---|---|
| `AfkKeeper.cs` | 全部原始碼（單檔） |
| `AfkKeeper.exe` | 編譯產物，雙擊即用（含自訂圖示） |
| `icon/Odavido123.webp` | 原始圖示圖檔 |
| `icon/app.ico` | 由 Odavido123.webp 轉出的多尺寸 Windows 圖示檔 |
| `app.manifest` | 一般權限執行 (asInvoker)、PerMonitorV2 高 DPI |
| `build.bat` | 重新編譯用（呼叫內建 csc，含圖示與清單） |
| `history.md` | 本檔 |

---

## 5. 如何重新編譯

雙擊 `build.bat`，或在終端機執行：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:AfkKeeper.exe /win32manifest:app.manifest /win32icon:icon\app.ico ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  AfkKeeper.cs
```

> 用 PowerShell 時把 `^` 換行去掉、寫成一行；用 Git Bash 會把 `/nologo` 誤判成路徑，**請改用 PowerShell 或 cmd**。

---

## 6. 功能與設定說明（目前版本）

GUI 欄位：
- **目標視窗**：下拉選擇已開啟的視窗（自動嘗試選中 Roblox）；「刷新」重新列舉。
- **送出按鍵**：ESC（預設）/ 空白鍵 / W / 上 / 下 / E / 數字0。
- **間隔 (秒)**：每隔幾秒送一次（預設 780 秒 = 13 分；最大 86400）。
- **± 隨機 (秒)**：在間隔上下隨機抖動的秒數（預設 120）。實際間隔 = 間隔 ± 隨機。
- **只在我閒置時送鍵**（勾選，推薦）：
  - **需閒置 (秒)**：手停下幾秒後才送（預設 3）。
  - **最久等 (秒)**：到期後若一直在操作，最多等這麼久就強制送一次，避免被踢（預設 90）。
- **開始 / 停止**、**狀態列**、**紀錄框**。
- 可最小化縮到**系統匣**，背景繼續執行。

### 送鍵原理摘要（程式內 `Native` 類別）
- `SendKeyScan(vk)`：用 `MapVirtualKey` 取得 scan code，`SendInput` 送 keydown→sleep 60ms→keyup。
- `ForceForeground(hWnd)`：用 `AttachThreadInput` 突破 `SetForegroundWindow` 限制。
- `GetIdleMilliseconds()`：用 `GetLastInputInfo` 算閒置時間。
- `ResolveTarget()`：送鍵前用「程序名」重新定位視窗 handle（遊戲重開後 handle 會變仍找得回）。

---

## 7. 開發過程踩過的坑

1. **Git Bash 會把 `/nologo`、`/optimize+` 當成檔案路徑** → 改用 PowerShell 編譯。
2. **內建 csc 只到 C# 5** → 把 `out _` 改成具名變數。
3. **GUI 文字被裁切**（多次調整）：
   - 標籤寬度寫死 → 改 `AutoSize = true`。
   - 全形括號太寬 → 改半形 `( )`。
   - 視窗太小 → 放大到 740×620、字級 10pt。
   - **ComboBox / NumericUpDown 寫死 `Height` 導致內容（箭頭、上下鈕）被壓掉** → 移除固定高度，讓控制項依字體自動算高。
   - 縮放模式 `AutoScaleMode.Dpi` → 改 **`AutoScaleMode.Font`**（WinForms 對文字最穩）。
4. **間隔單位**：原本「分」，使用者要求改成 **「秒」**（連同 ± 隨機也改秒，換算邏輯拿掉 ×60）。
5. **跨行程視窗擁有權限制**：Windows 不允許跨行程透過 `SetWindowLongPtr(GWLP_HWNDPARENT)` 設定擁有者，改採 `GetWindow(GW_HWNDPREV)` 配合動態 Z-Order 與 `SWP_NOACTIVATE`，精準在目標視窗上方貼合遮罩且不搶焦點、不干擾其他日常軟體操作。
6. **視窗清單行程名稱顯示 `[?]` 問題**：.NET 內建 `Process.GetProcessById(pid).ProcessName` 在受保護行程或效能計數器未就緒時易擲出例外導致全部變成 `[?]`，進而導致設定檔無法正確還原選取。改寫為 Win32 原生 `QueryFullProcessImageName`（`PROCESS_QUERY_LIMITED_INFORMATION` 權限要求最低），穩定抓取完整執行檔名稱，並同步支援依視窗標題或行程名自動選中 Roblox。

---

## 8. 目前狀態

- ✅ 可正常編譯、啟動、列舉視窗、自動選中 Roblox。
- ✅ 送鍵流程運作（紀錄出現「已送出…並還原焦點」）。
- ✅ GUI 版面已修到不裁切。
- ✅ 間隔已改為「秒」。
- ✅ **半透明防誤觸遮罩（OverlayForm）**：
  - 運行時精準覆蓋於目標視窗上方，攔截滑鼠誤操作。
  - **僅在目標視窗（如 Roblox）獲得焦點 (Focus) 時顯示遮罩**：切換至其他軟體操作時遮罩立即完全隱藏，絕不遮擋或干擾任何日常工作；後台定時送鍵期間亦自動抑制遮罩避免畫面閃爍。
  - 自動隨目標視窗移動、縮放、最小化/還原同步隱藏與顯示。
  - 遮罩具備兩大核心解鎖按鈕：
    1. **「⏸ 暫時關閉」**：提供暫停 1 分鐘（預設）、15/30/60 分鐘或自訂分鐘數，解除遮罩讓使用者正常操作視窗；倒數結束後自動恢復掛機與遮罩，主視窗亦提供「提前恢復」按鈕。
    2. **「⏰ 每日定時」**：可設定每天固定時段（如 01:00～07:00，支援跨夜）自動掛機與覆蓋遮罩，離峰時段自動解除遮罩供正常操作。
- ✅ **設定自動儲存與載入**：目標程序、按鍵、間隔、隨機抖動、閒置條件、遮罩開關及每日排程自動持久化至 `afkkeeper_config.ini`。
- ✅ **📊 每日暫停統計與視覺化 HTML 報表（PauseTracker）**：
  - **自動追蹤三種暫停情境**：
    1. **遮罩暫時解鎖/手動延長** (`TemporaryPause`)
    2. **手動停止掛機/待命** (`ManualStopped`)
    3. **每日定時排程離峰待命** (`ScheduleOff`)
  - **跨午夜精準切割演算法**：針對跨日的長時段暫停，自動精準拆分並歸納至各個日曆天中。
  - **資料持久化**：即時與歷史紀錄存於 `afkkeeper_pause_records.csv`（UTF-8 CSV 格式）。
  - **視覺化 HTML 報表 (`pause_report.html`)**：
    - 主視窗新增「📊 暫停統計報表」按鈕，系統匣右鍵選單亦提供快捷開啟。
    - 採用現代深色科技風 Dashboard，內建原生 Canvas 繪製堆疊長條圖（Daily Stacked Bar Chart）、類型圓環圖（Donut Chart）。
    - 提供 KPI 核心指標卡片（總時長、今日暫停、日均暫停、單次最長）。
    - 支援快捷篩選（今天、7天、14天、30天、全部歷史）、即時關鍵字搜尋與一鍵匯出 CSV / 列印為 PDF。
- ⏳ **尚未在 Roblox 內最終確認角色真的會跳**（待使用者回報；若沒跳，改用 VK+scan 並拉長按住時間）。

---

## 9. 待辦 / 可加功能

1. **開機自動啟動**：登入後自動於系統匣執行（捷徑放 Startup 資料夾或寫登錄機碼）。
2. （若 Roblox 收不到鍵）送鍵方式備援：同時送 Virtual-Key 與 scan code、或拉長按住時間。

---

## 10. 給未來的 Claude / AI 助理：如何還原記憶

重灌後請：
1. 讀這份 `history.md` 了解全部脈絡。
2. 讀 `AfkKeeper.cs` 看目前實作。
3. 確認 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 存在，用 `build.bat` 重新編譯驗證。
4. 從「第 9 節 待辦」接續開發。
