// AfkKeeper —— 自訂時間，對指定的已開啟視窗送出按鍵（預設上箭頭 Up）的小工具。
// 設計目標：
//   1) 真實輸入（SendInput，scan code），Roblox 等遊戲收得到。
//   2) 干擾最小：只在「你手沒在動」的空檔，短暫把目標視窗切到前景送鍵、再立刻把焦點還給你原本的視窗。
//   3) 反偵測友善：間隔可隨機抖動，避免毫秒不差的規律。純外部模擬輸入、不碰遊戲記憶體、不注入。
//   4) 防誤觸遮罩：執行時在目標視窗覆蓋半透明遮罩，提供「暫時關閉」與「每日定時」，按下一鍵解鎖正常操作。
//
// 編譯：見 build.bat（用 Windows 內建的 csc.exe，不需安裝任何 SDK）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AfkKeeper
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ====== Win32 互通 ======
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // 取得行程名稱（優先使用 Win32 QueryFullProcessImageName，最穩且不受權限或效能計數器限制）
        public static string GetProcessName(uint pid)
        {
            if (pid == 0) return "System";

            try
            {
                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                        {
                            string path = sb.ToString();
                            return Path.GetFileNameWithoutExtension(path);
                        }
                    }
                    finally
                    {
                        CloseHandle(hProcess);
                    }
                }
            }
            catch { }

            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch { }

            return "?";
        }

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const uint GW_HWNDPREV = 3;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        // 取得視窗精確的實體邊界（包含 DWM 陰影修剪）
        public static bool GetWindowBounds(IntPtr hWnd, out RECT rect)
        {
            rect = new RECT();
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT)));
            if (hr == 0 && rect.Width > 0 && rect.Height > 0) return true;
            return GetWindowRect(hWnd, out rect);
        }

        // --- 閒置偵測 ---
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // 取得使用者閒置毫秒數
        public static uint GetIdleMilliseconds()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return 0;
            return (uint)Environment.TickCount - lii.dwTime;
        }

        // --- SendInput ---
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public const uint MAPVK_VK_TO_VSC = 0;

        // 用 scan code 送出一個按鍵（按下→放開）。遊戲多半讀 scan code，相容性最好。
        public static void SendKeyScan(ushort vk)
        {
            ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);

            INPUT[] down = new INPUT[1];
            down[0].type = INPUT_KEYBOARD;
            down[0].U.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_SCANCODE, time = 0, dwExtraInfo = IntPtr.Zero };
            SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));

            System.Threading.Thread.Sleep(60); // 模擬真實按住的短暫時間

            INPUT[] up = new INPUT[1];
            up[0].type = INPUT_KEYBOARD;
            up[0].U.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero };
            SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
        }

        // 強制把視窗叫到前景（突破 SetForegroundWindow 的限制）。
        public static void ForceForeground(IntPtr hWnd)
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            IntPtr fore = GetForegroundWindow();
            uint forePid;
            uint foreThread = GetWindowThreadProcessId(fore, out forePid);
            uint thisThread = GetCurrentThreadId();

            if (foreThread != thisThread)
            {
                AttachThreadInput(foreThread, thisThread, true);
                BringWindowToTop(hWnd);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
                AttachThreadInput(foreThread, thisThread, false);
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
        }
    }

    // 代表一個可選的目標視窗
    internal class WindowItem
    {
        public IntPtr Handle;
        public string Title;
        public string ProcessName;
        public override string ToString()
        {
            return string.Format("[{0}] {1}", ProcessName, Title);
        }
    }

    // ====== 每日定時排程資料模型 ======
    internal class DailyScheduleConfig
    {
        public bool Enabled = false;
        public int StartHour = 1;
        public int StartMinute = 0;
        public int EndHour = 7;
        public int EndMinute = 0;

        // 判斷給定時間是否處於排程時段內（支援跨夜，如 23:00 ~ 07:00）
        public bool IsInSchedule(DateTime time)
        {
            TimeSpan current = time.TimeOfDay;
            TimeSpan start = new TimeSpan(StartHour, StartMinute, 0);
            TimeSpan end = new TimeSpan(EndHour, EndMinute, 0);

            if (start <= end)
            {
                return current >= start && current < end;
            }
            else
            {
                // 跨午夜
                return current >= start || current < end;
            }
        }
    }

    // ====== 暫停記錄與統計資料模型 ======
    internal enum TrackerState
    {
        Running,        // 掛機進行中
        TemporaryPause, // 遮罩暫時解鎖/延長
        ManualStopped,  // 手動停止/未啟動
        ScheduleOff     // 每日定時排程離峰待命
    }

    internal class PauseRecord
    {
        public int Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public string Category { get; set; } // Temporary, ManualStop, ScheduleOff
        public string TargetName { get; set; }
        public string Detail { get; set; }

        public string GetCategoryDisplayName()
        {
            if (Category == "Temporary") return "暫時關閉掛機";
            if (Category == "ManualStop") return "手動停止掛機";
            if (Category == "ScheduleOff") return "排程離峰待命";
            return Category ?? "其他";
        }
    }

    internal class DailyPauseSummary
    {
        public string DateString { get; set; } // yyyy-MM-dd
        public double TotalSeconds { get; set; }
        public double TemporarySeconds { get; set; }
        public double ManualStopSeconds { get; set; }
        public double ScheduleOffSeconds { get; set; }
        public int PauseCount { get; set; }
    }

    // ====== 暫停統計管理與 HTML 報表產出器 ======
    internal class PauseTracker
    {
        private readonly string csvPath;
        private readonly string htmlPath;
        private readonly List<PauseRecord> records = new List<PauseRecord>();
        private int nextId = 1;
        private PauseRecord activeRecord = null;
        private TrackerState currentState = TrackerState.ManualStopped;

        public TrackerState CurrentState
        {
            get { return currentState; }
        }

        public PauseTracker()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            csvPath = Path.Combine(baseDir, "afkkeeper_pause_records.csv");
            htmlPath = Path.Combine(baseDir, "pause_report.html");
            LoadFromCsv();
        }

        public void TransitionState(TrackerState newState, string targetName, string detail)
        {
            DateTime now = DateTime.Now;

            // 1. 若前一狀態為暫停類別，結算並存檔
            if (activeRecord != null)
            {
                activeRecord.EndTime = now;
                activeRecord.DurationSeconds = Math.Max(1, (activeRecord.EndTime - activeRecord.StartTime).TotalSeconds);
                if (activeRecord.DurationSeconds >= 1)
                {
                    records.Add(activeRecord);
                    AppendToCsv(activeRecord);
                }
                activeRecord = null;
            }

            currentState = newState;

            // 2. 若新狀態為暫停相關，建立新進行中記錄
            if (newState == TrackerState.TemporaryPause ||
                newState == TrackerState.ManualStopped ||
                newState == TrackerState.ScheduleOff)
            {
                string cat = "ManualStop";
                if (newState == TrackerState.TemporaryPause) cat = "Temporary";
                else if (newState == TrackerState.ScheduleOff) cat = "ScheduleOff";

                activeRecord = new PauseRecord
                {
                    Id = nextId++,
                    StartTime = now,
                    EndTime = now,
                    DurationSeconds = 0,
                    Category = cat,
                    TargetName = string.IsNullOrEmpty(targetName) ? "未指定視窗" : targetName,
                    Detail = detail ?? string.Empty
                };
            }
        }

        public void UpdateActiveDetail(string detail)
        {
            if (activeRecord != null)
            {
                activeRecord.Detail = detail;
            }
        }

        public void FlushActive()
        {
            if (activeRecord != null)
            {
                DateTime now = DateTime.Now;
                activeRecord.EndTime = now;
                activeRecord.DurationSeconds = Math.Max(1, (activeRecord.EndTime - activeRecord.StartTime).TotalSeconds);
                if (activeRecord.DurationSeconds >= 1)
                {
                    records.Add(activeRecord);
                    AppendToCsv(activeRecord);
                }
                activeRecord = null;
            }
        }

        private void LoadFromCsv()
        {
            records.Clear();
            nextId = 1;
            if (!File.Exists(csvPath)) return;

            try
            {
                string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("Id,")) continue;

                    string[] parts = line.Split(',');
                    if (parts.Length >= 7)
                    {
                        int id;
                        DateTime st, et;
                        double dur;
                        if (int.TryParse(parts[0], out id) &&
                            DateTime.TryParse(parts[1], out st) &&
                            DateTime.TryParse(parts[2], out et) &&
                            double.TryParse(parts[3], out dur))
                        {
                            records.Add(new PauseRecord
                            {
                                Id = id,
                                StartTime = st,
                                EndTime = et,
                                DurationSeconds = dur,
                                Category = parts[4],
                                TargetName = UnescapeCsv(parts[5]),
                                Detail = UnescapeCsv(parts[6])
                            });
                            if (id >= nextId) nextId = id + 1;
                        }
                    }
                }
            }
            catch { }
        }

        private void AppendToCsv(PauseRecord rec)
        {
            try
            {
                bool exists = File.Exists(csvPath);
                using (var sw = new StreamWriter(csvPath, true, Encoding.UTF8))
                {
                    if (!exists)
                    {
                        sw.WriteLine("Id,StartTime,EndTime,DurationSeconds,Category,Target,Detail");
                    }
                    sw.WriteLine(string.Format("{0},{1:yyyy-MM-dd HH:mm:ss},{2:yyyy-MM-dd HH:mm:ss},{3:F0},{4},{5},{6}",
                        rec.Id,
                        rec.StartTime,
                        rec.EndTime,
                        rec.DurationSeconds,
                        rec.Category,
                        EscapeCsv(rec.TargetName),
                        EscapeCsv(rec.Detail)));
                }
            }
            catch { }
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string UnescapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
            {
                s = s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            }
            return s;
        }

        // 計算每日統計數據（自動精準切割跨午夜時間區段）
        public List<DailyPauseSummary> ComputeDailySummaries(List<PauseRecord> allRecs)
        {
            var dict = new Dictionary<string, DailyPauseSummary>();

            foreach (var rec in allRecs)
            {
                DateTime cur = rec.StartTime;
                DateTime end = rec.EndTime;
                if (end <= cur) continue;

                while (cur < end)
                {
                    DateTime nextDay = cur.Date.AddDays(1);
                    DateTime sliceEnd = (end < nextDay) ? end : nextDay;
                    double sliceSec = (sliceEnd - cur).TotalSeconds;
                    string dateKey = cur.ToString("yyyy-MM-dd");

                    DailyPauseSummary summary;
                    if (!dict.TryGetValue(dateKey, out summary))
                    {
                        summary = new DailyPauseSummary { DateString = dateKey };
                        dict[dateKey] = summary;
                    }

                    summary.TotalSeconds += sliceSec;
                    summary.PauseCount += 1;
                    if (rec.Category == "Temporary") summary.TemporarySeconds += sliceSec;
                    else if (rec.Category == "ManualStop") summary.ManualStopSeconds += sliceSec;
                    else if (rec.Category == "ScheduleOff") summary.ScheduleOffSeconds += sliceSec;

                    cur = sliceEnd;
                }
            }

            var list = new List<DailyPauseSummary>(dict.Values);
            list.Sort((a, b) => string.Compare(a.DateString, b.DateString, StringComparison.Ordinal));
            return list;
        }

        public string GenerateHtmlReport(string currentTarget)
        {
            // 複製全部紀錄，若目前有進行中的暫停也即時加入計算
            var allRecs = new List<PauseRecord>(records);
            DateTime now = DateTime.Now;
            if (activeRecord != null)
            {
                var liveRec = new PauseRecord
                {
                    Id = activeRecord.Id,
                    StartTime = activeRecord.StartTime,
                    EndTime = now,
                    DurationSeconds = Math.Max(1, (now - activeRecord.StartTime).TotalSeconds),
                    Category = activeRecord.Category,
                    TargetName = activeRecord.TargetName,
                    Detail = activeRecord.Detail + " (進行中)"
                };
                allRecs.Add(liveRec);
            }

            var summaries = ComputeDailySummaries(allRecs);

            // 建立嵌入 JSON
            var sbRecordsJson = new StringBuilder();
            sbRecordsJson.Append("[");
            for (int i = 0; i < allRecs.Count; i++)
            {
                var r = allRecs[i];
                if (i > 0) sbRecordsJson.Append(",");
                sbRecordsJson.AppendFormat("{{\"id\":{0},\"st\":\"{1:yyyy-MM-dd HH:mm:ss}\",\"et\":\"{2:yyyy-MM-dd HH:mm:ss}\",\"dur\":{3:F0},\"cat\":\"{4}\",\"catName\":\"{5}\",\"target\":\"{6}\",\"detail\":\"{7}\"}}",
                    r.Id,
                    r.StartTime,
                    r.EndTime,
                    r.DurationSeconds,
                    r.Category,
                    EscapeJson(r.GetCategoryDisplayName()),
                    EscapeJson(r.TargetName),
                    EscapeJson(r.Detail));
            }
            sbRecordsJson.Append("]");

            var sbSummariesJson = new StringBuilder();
            sbSummariesJson.Append("[");
            for (int i = 0; i < summaries.Count; i++)
            {
                var s = summaries[i];
                if (i > 0) sbSummariesJson.Append(",");
                sbSummariesJson.AppendFormat("{{\"date\":\"{0}\",\"total\":{1:F0},\"temp\":{2:F0},\"stop\":{3:F0},\"sched\":{4:F0},\"count\":{5}}}",
                    s.DateString,
                    s.TotalSeconds,
                    s.TemporarySeconds,
                    s.ManualStopSeconds,
                    s.ScheduleOffSeconds,
                    s.PauseCount);
            }
            sbSummariesJson.Append("]");

            string statusText = "閒置停止中";
            if (currentState == TrackerState.Running) statusText = "掛機運行中";
            else if (currentState == TrackerState.TemporaryPause) statusText = "遮罩暫時關閉中";
            else if (currentState == TrackerState.ScheduleOff) statusText = "排程離峰待命中";

            string html = GetHtmlTemplate()
                .Replace("{{GENERATED_TIME}}", now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{{CURRENT_STATUS}}", statusText)
                .Replace("{{CURRENT_TARGET}}", EscapeJson(string.IsNullOrEmpty(currentTarget) ? "無" : currentTarget))
                .Replace("{{RECORDS_JSON}}", sbRecordsJson.ToString())
                .Replace("{{SUMMARIES_JSON}}", sbSummariesJson.ToString());

            File.WriteAllText(htmlPath, html, Encoding.UTF8);
            return htmlPath;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        }

        public void OpenReport(string currentTarget)
        {
            try
            {
                string path = GenerateHtmlReport(currentTarget);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("開啟報表失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string GetHtmlTemplate()
        {
            return @"<!DOCTYPE html>
<html lang=""zh-TW"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>AfkKeeper 暫停統計與掛機分析報表</title>
  <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
  <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
  <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Noto+Sans+TC:wght@400;500;700&display=swap"" rel=""stylesheet"">
  <style>
    :root {
      --bg-main: #0b0f19;
      --bg-card: #151d2e;
      --bg-card-hover: #1c263d;
      --border-color: #24324d;
      --text-primary: #f1f5f9;
      --text-secondary: #94a3b8;
      --text-muted: #64748b;
      --color-primary: #38bdf8;
      --color-temp: #f59e0b;     /* 暫時關閉: 琥珀金 */
      --color-stop: #818cf8;     /* 手動停止: 靛青紫 */
      --color-sched: #34d399;    /* 排程離峰: 翡翠綠 */
      --color-running: #10b981;  /* 運行中 */
      --shadow-card: 0 10px 25px -5px rgba(0, 0, 0, 0.45), 0 8px 10px -6px rgba(0, 0, 0, 0.4);
      --radius-lg: 16px;
      --radius-md: 10px;
      --radius-sm: 6px;
    }

    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: 'Inter', 'Noto Sans TC', -apple-system, BlinkMacSystemFont, sans-serif;
      background-color: var(--bg-main);
      color: var(--text-primary);
      line-height: 1.5;
      padding: 32px 24px 60px;
      min-height: 100vh;
    }

    .container {
      max-width: 1280px;
      margin: 0 auto;
    }

    /* Header */
    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 28px;
      padding-bottom: 20px;
      border-bottom: 1px solid var(--border-color);
      flex-wrap: wrap;
      gap: 16px;
    }
    .header-title {
      display: flex;
      align-items: center;
      gap: 14px;
    }
    .header-logo {
      width: 44px;
      height: 44px;
      background: linear-gradient(135deg, #0284c7, #6366f1);
      border-radius: var(--radius-md);
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 22px;
      box-shadow: 0 4px 12px rgba(56, 189, 248, 0.35);
    }
    .header-title h1 {
      font-size: 24px;
      font-weight: 700;
      letter-spacing: -0.5px;
      background: linear-gradient(90deg, #ffffff, #94a3b8);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
    }
    .header-meta {
      font-size: 13px;
      color: var(--text-secondary);
      margin-top: 2px;
    }
    .header-actions {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 6px 12px;
      border-radius: 999px;
      font-size: 13px;
      font-weight: 600;
      background: rgba(56, 189, 248, 0.12);
      color: var(--color-primary);
      border: 1px solid rgba(56, 189, 248, 0.3);
    }
    .badge-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--color-running);
      box-shadow: 0 0 8px var(--color-running);
    }
    .btn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 8px 16px;
      border-radius: var(--radius-sm);
      font-size: 13px;
      font-weight: 600;
      cursor: pointer;
      border: 1px solid var(--border-color);
      background: var(--bg-card);
      color: var(--text-primary);
      transition: all 0.2s ease;
      text-decoration: none;
    }
    .btn:hover {
      background: var(--bg-card-hover);
      border-color: #3b82f6;
      transform: translateY(-1px);
    }
    .btn-primary {
      background: #0284c7;
      border-color: #0284c7;
      color: #fff;
    }
    .btn-primary:hover {
      background: #0369a1;
      border-color: #0369a1;
    }

    /* Filter Bar */
    .filter-bar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      background: var(--bg-card);
      border: 1px solid var(--border-color);
      border-radius: var(--radius-md);
      padding: 12px 18px;
      margin-bottom: 24px;
      flex-wrap: wrap;
      gap: 12px;
    }
    .filter-tabs {
      display: flex;
      gap: 6px;
    }
    .filter-tab {
      padding: 6px 14px;
      border-radius: var(--radius-sm);
      font-size: 13px;
      font-weight: 500;
      color: var(--text-secondary);
      cursor: pointer;
      border: 1px solid transparent;
      transition: all 0.15s ease;
      background: transparent;
    }
    .filter-tab:hover {
      color: var(--text-primary);
      background: rgba(255, 255, 255, 0.05);
    }
    .filter-tab.active {
      color: #ffffff;
      background: #2563eb;
      font-weight: 600;
    }
    .filter-summary {
      font-size: 13px;
      color: var(--text-muted);
    }

    /* KPI Cards Grid */
    .kpi-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: 18px;
      margin-bottom: 28px;
    }
    .kpi-card {
      background: var(--bg-card);
      border: 1px solid var(--border-color);
      border-radius: var(--radius-lg);
      padding: 20px 22px;
      box-shadow: var(--shadow-card);
      position: relative;
      overflow: hidden;
      transition: border-color 0.2s;
    }
    .kpi-card:hover {
      border-color: #334155;
    }
    .kpi-card::before {
      content: '';
      position: absolute;
      top: 0; left: 0; right: 0; height: 3px;
      background: var(--card-accent, #3b82f6);
    }
    .kpi-label {
      font-size: 13px;
      font-weight: 500;
      color: var(--text-secondary);
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 8px;
    }
    .kpi-value {
      font-size: 26px;
      font-weight: 700;
      letter-spacing: -0.5px;
      color: #ffffff;
      margin-bottom: 4px;
    }
    .kpi-sub {
      font-size: 12px;
      color: var(--text-muted);
      display: flex;
      align-items: center;
      gap: 6px;
    }

    /* Charts Section */
    .charts-grid {
      display: grid;
      grid-template-columns: 2fr 1fr;
      gap: 20px;
      margin-bottom: 28px;
    }
    @media (max-width: 960px) {
      .charts-grid { grid-template-columns: 1fr; }
    }
    .chart-card {
      background: var(--bg-card);
      border: 1px solid var(--border-color);
      border-radius: var(--radius-lg);
      padding: 22px;
      box-shadow: var(--shadow-card);
      display: flex;
      flex-direction: column;
    }
    .chart-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 18px;
    }
    .chart-title {
      font-size: 16px;
      font-weight: 700;
      color: var(--text-primary);
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .chart-legend {
      display: flex;
      gap: 12px;
      font-size: 12px;
      color: var(--text-secondary);
    }
    .legend-item {
      display: flex;
      align-items: center;
      gap: 5px;
    }
    .legend-box {
      width: 10px;
      height: 10px;
      border-radius: 2px;
    }
    .canvas-container {
      position: relative;
      flex: 1;
      min-height: 280px;
      width: 100%;
    }
    canvas {
      display: block;
      width: 100%;
      height: 100%;
    }

    /* Table Sections */
    .table-section {
      background: var(--bg-card);
      border: 1px solid var(--border-color);
      border-radius: var(--radius-lg);
      padding: 22px;
      box-shadow: var(--shadow-card);
      margin-bottom: 28px;
    }
    .section-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 16px;
      flex-wrap: wrap;
      gap: 12px;
    }
    .section-title {
      font-size: 16px;
      font-weight: 700;
      color: var(--text-primary);
    }
    .search-box {
      background: var(--bg-main);
      border: 1px solid var(--border-color);
      border-radius: var(--radius-sm);
      padding: 6px 12px;
      color: var(--text-primary);
      font-size: 13px;
      outline: none;
      width: 220px;
      transition: border-color 0.2s;
    }
    .search-box:focus {
      border-color: #3b82f6;
    }
    .table-responsive {
      overflow-x: auto;
      border-radius: var(--radius-md);
      border: 1px solid rgba(255, 255, 255, 0.05);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
      text-align: left;
    }
    th {
      background: #111827;
      color: var(--text-secondary);
      font-weight: 600;
      padding: 12px 14px;
      border-bottom: 1px solid var(--border-color);
      white-space: nowrap;
    }
    td {
      padding: 12px 14px;
      border-bottom: 1px solid rgba(255, 255, 255, 0.04);
      color: var(--text-primary);
    }
    tr:last-child td {
      border-bottom: none;
    }
    tr:hover td {
      background: rgba(255, 255, 255, 0.02);
    }
    .tag {
      display: inline-block;
      padding: 3px 8px;
      border-radius: 4px;
      font-size: 11px;
      font-weight: 600;
    }
    .tag-temp { background: rgba(245, 158, 11, 0.15); color: #fbbf24; border: 1px solid rgba(245, 158, 11, 0.3); }
    .tag-stop { background: rgba(129, 140, 248, 0.15); color: #a5b4fc; border: 1px solid rgba(129, 140, 248, 0.3); }
    .tag-sched { background: rgba(52, 211, 153, 0.15); color: #6ee7b7; border: 1px solid rgba(52, 211, 153, 0.3); }

    .empty-state {
      padding: 40px 20px;
      text-align: center;
      color: var(--text-muted);
      font-size: 14px;
    }

    /* Print styling */
    @media print {
      body { background: #fff; color: #000; padding: 10px; }
      .filter-bar, .header-actions, .search-box { display: none; }
      .kpi-card, .chart-card, .table-section { box-shadow: none; border: 1px solid #ddd; background: #fff; color: #000; }
      .kpi-value, .chart-title, .section-title { color: #000; }
      th { background: #f1f5f9; color: #333; }
      td { color: #333; }
    }
  </style>
</head>
<body>
  <div class=""container"">
    <header>
      <div class=""header-title"">
        <div class=""header-logo"">⏸</div>
        <div>
          <h1>AfkKeeper 暫停統計報表</h1>
          <div class=""header-meta"">報表產生時間：{{GENERATED_TIME}} ｜ 目標視窗：{{CURRENT_TARGET}}</div>
        </div>
      </div>
      <div class=""header-actions"">
        <div class=""badge""><span class=""badge-dot""></span>{{CURRENT_STATUS}}</div>
        <button class=""btn"" onclick=""window.print()"">🖨️ 列印報表</button>
        <button class=""btn btn-primary"" onclick=""exportCsv()"">📥 匯出 CSV</button>
      </div>
    </header>

    <!-- Filter Bar -->
    <div class=""filter-bar"">
      <div class=""filter-tabs"">
        <button class=""filter-tab active"" onclick=""setFilter('all', this)"">全部歷史</button>
        <button class=""filter-tab"" onclick=""setFilter('today', this)"">今天</button>
        <button class=""filter-tab"" onclick=""setFilter('7d', this)"">最近 7 天</button>
        <button class=""filter-tab"" onclick=""setFilter('14d', this)"">最近 14 天</button>
        <button class=""filter-tab"" onclick=""setFilter('30d', this)"">最近 30 天</button>
      </div>
      <div class=""filter-summary"" id=""filterSummaryText"">正在顯示全部資料</div>
    </div>

    <!-- KPI Grid -->
    <div class=""kpi-grid"">
      <div class=""kpi-card"" style=""--card-accent: #38bdf8;"">
        <div class=""kpi-label""><span>⏳ 區間總暫停時長</span><span>TOTAL</span></div>
        <div class=""kpi-value"" id=""kpiTotalDuration"">0 秒</div>
        <div class=""kpi-sub"" id=""kpiTotalCount"">共 0 次暫停事件</div>
      </div>
      <div class=""kpi-card"" style=""--card-accent: #f59e0b;"">
        <div class=""kpi-label""><span>📅 今日暫停時長</span><span>TODAY</span></div>
        <div class=""kpi-value"" id=""kpiTodayDuration"">0 秒</div>
        <div class=""kpi-sub"" id=""kpiTodayCount"">今日 0 次暫停</div>
      </div>
      <div class=""kpi-card"" style=""--card-accent: #818cf8;"">
        <div class=""kpi-label""><span>📊 平均每日暫停時長</span><span>AVG DAILY</span></div>
        <div class=""kpi-value"" id=""kpiAvgDailyDuration"">0 秒</div>
        <div class=""kpi-sub"" id=""kpiAvgDailyDays"">統計天數：0 天</div>
      </div>
      <div class=""kpi-card"" style=""--card-accent: #34d399;"">
        <div class=""kpi-label""><span>🏆 最長單次暫停</span><span>MAX RECORD</span></div>
        <div class=""kpi-value"" id=""kpiMaxDuration"">0 秒</div>
        <div class=""kpi-sub"" id=""kpiMaxDetail"">無紀錄</div>
      </div>
    </div>

    <!-- Charts -->
    <div class=""charts-grid"">
      <div class=""chart-card"">
        <div class=""chart-header"">
          <div class=""chart-title"">📈 每日暫停時長趨勢（堆疊長條圖）</div>
          <div class=""chart-legend"">
            <div class=""legend-item""><div class=""legend-box"" style=""background: var(--color-temp);""></div>暫時關閉</div>
            <div class=""legend-item""><div class=""legend-box"" style=""background: var(--color-stop);""></div>手動停止</div>
            <div class=""legend-item""><div class=""legend-box"" style=""background: var(--color-sched);""></div>排程待命</div>
          </div>
        </div>
        <div class=""canvas-container"">
          <canvas id=""barCanvas""></canvas>
        </div>
      </div>

      <div class=""chart-card"">
        <div class=""chart-header"">
          <div class=""chart-title"">🍩 暫停類型時長佔比</div>
        </div>
        <div class=""canvas-container"">
          <canvas id=""donutCanvas""></canvas>
        </div>
      </div>
    </div>

    <!-- Daily Summary Table -->
    <div class=""table-section"">
      <div class=""section-header"">
        <div class=""section-title"">📅 每日暫停彙總統計表</div>
      </div>
      <div class=""table-responsive"">
        <table id=""dailyTable"">
          <thead>
            <tr>
              <th>日期</th>
              <th>暫停總時長</th>
              <th>暫停次數</th>
              <th>暫時關閉時長</th>
              <th>手動停止時長</th>
              <th>排程離峰待命時長</th>
            </tr>
          </thead>
          <tbody id=""dailyTableBody"">
            <!-- 動態生成 -->
          </tbody>
        </table>
      </div>
    </div>

    <!-- Detail Records Table -->
    <div class=""table-section"">
      <div class=""section-header"">
        <div class=""section-title"">📝 暫停事件明細記錄清單</div>
        <input type=""text"" id=""searchInput"" class=""search-box"" placeholder=""🔍 搜尋目標視窗或備註..."" oninput=""renderTables()"">
      </div>
      <div class=""table-responsive"">
        <table id=""recordsTable"">
          <thead>
            <tr>
              <th>#</th>
              <th>暫停類型</th>
              <th>開始時間</th>
              <th>結束時間</th>
              <th>持續時長</th>
              <th>目標視窗 / 程序</th>
              <th>說明 / 備註</th>
            </tr>
          </thead>
          <tbody id=""recordsTableBody"">
            <!-- 動態生成 -->
          </tbody>
        </table>
      </div>
    </div>
  </div>

  <script>
    const rawRecords = {{RECORDS_JSON}};
    const rawSummaries = {{SUMMARIES_JSON}};
    let currentFilter = 'all';

    function formatDuration(sec) {
      sec = Math.round(sec);
      if (sec < 60) return sec + ' 秒';
      const m = Math.floor(sec / 60);
      const s = sec % 60;
      if (m < 60) return m + ' 分 ' + (s > 0 ? s + ' 秒' : '');
      const h = Math.floor(m / 60);
      const remM = m % 60;
      return h + ' 小時 ' + remM + ' 分';
    }

    function getFilteredData() {
      const now = new Date();
      const todayStr = now.toISOString().slice(0, 10);

      let filteredRecs = rawRecords;
      let filteredSums = rawSummaries;

      if (currentFilter === 'today') {
        filteredRecs = rawRecords.filter(r => r.st.startsWith(todayStr));
        filteredSums = rawSummaries.filter(s => s.date === todayStr);
      } else if (currentFilter === '7d' || currentFilter === '14d' || currentFilter === '30d') {
        const days = currentFilter === '7d' ? 7 : (currentFilter === '14d' ? 14 : 30);
        const cutoff = new Date(now.getTime() - days * 24 * 3600 * 1000);
        const cutoffStr = cutoff.toISOString().slice(0, 10);
        filteredRecs = rawRecords.filter(r => r.st.slice(0, 10) >= cutoffStr);
        filteredSums = rawSummaries.filter(s => s.date >= cutoffStr);
      }

      return { recs: filteredRecs, sums: filteredSums };
    }

    function setFilter(filter, elem) {
      currentFilter = filter;
      document.querySelectorAll('.filter-tab').forEach(el => el.classList.remove('active'));
      elem.classList.add('active');

      const filterNames = { all: '全部歷史', today: '今天', '7d': '最近 7 天', '14d': '最近 14 天', '30d': '最近 30 天' };
      document.getElementById('filterSummaryText').innerText = '正在顯示「' + filterNames[filter] + '」資料';

      renderAll();
    }

    function renderKPIs(recs, sums) {
      let totalSec = 0;
      recs.forEach(r => totalSec += r.dur);
      document.getElementById('kpiTotalDuration').innerText = formatDuration(totalSec);
      document.getElementById('kpiTotalCount').innerText = '共 ' + recs.length + ' 次暫停事件';

      const todayStr = new Date().toISOString().slice(0, 10);
      const todaySum = rawSummaries.find(s => s.date === todayStr);
      if (todaySum) {
        document.getElementById('kpiTodayDuration').innerText = formatDuration(todaySum.total);
        document.getElementById('kpiTodayCount').innerText = '今日 ' + todaySum.count + ' 次暫停';
      } else {
        document.getElementById('kpiTodayDuration').innerText = '0 秒';
        document.getElementById('kpiTodayCount').innerText = '今日 0 次暫停';
      }

      const dayCount = sums.length || 1;
      const avgDaily = totalSec / dayCount;
      document.getElementById('kpiAvgDailyDuration').innerText = formatDuration(avgDaily);
      document.getElementById('kpiAvgDailyDays').innerText = '統計有效天數：' + sums.length + ' 天';

      let maxRec = null;
      recs.forEach(r => {
        if (!maxRec || r.dur > maxRec.dur) maxRec = r;
      });
      if (maxRec) {
        document.getElementById('kpiMaxDuration').innerText = formatDuration(maxRec.dur);
        document.getElementById('kpiMaxDetail').innerText = maxRec.catName + ' (' + maxRec.st.slice(5, 16) + ')';
      } else {
        document.getElementById('kpiMaxDuration').innerText = '0 秒';
        document.getElementById('kpiMaxDetail').innerText = '無紀錄';
      }
    }

    function renderTables() {
      const { recs, sums } = getFilteredData();
      const q = (document.getElementById('searchInput').value || '').trim().toLowerCase();

      // 1. Daily table
      const dailyBody = document.getElementById('dailyTableBody');
      if (sums.length === 0) {
        dailyBody.innerHTML = '<tr><td colspan=""6"" class=""empty-state"">尚無每日彙總資料</td></tr>';
      } else {
        const sortedSums = [...sums].reverse();
        dailyBody.innerHTML = sortedSums.map(s => `
          <tr>
            <td><strong>${s.date}</strong></td>
            <td><span style=""font-weight: 700; color: #38bdf8;"">${formatDuration(s.total)}</span></td>
            <td>${s.count} 次</td>
            <td><span style=""color: var(--color-temp);"">${formatDuration(s.temp)}</span></td>
            <td><span style=""color: var(--color-stop);"">${formatDuration(s.stop)}</span></td>
            <td><span style=""color: var(--color-sched);"">${formatDuration(s.sched)}</span></td>
          </tr>
        `).join('');
      }

      // 2. Records table
      const recordsBody = document.getElementById('recordsTableBody');
      const filteredRecs = recs.filter(r => {
        if (!q) return true;
        return (r.target && r.target.toLowerCase().includes(q)) ||
               (r.detail && r.detail.toLowerCase().includes(q)) ||
               (r.catName && r.catName.toLowerCase().includes(q));
      });

      if (filteredRecs.length === 0) {
        recordsBody.innerHTML = '<tr><td colspan=""7"" class=""empty-state"">查無符合條件的暫停紀錄</td></tr>';
      } else {
        const sortedRecs = [...filteredRecs].reverse();
        recordsBody.innerHTML = sortedRecs.map(r => {
          let tagClass = 'tag-stop';
          if (r.cat === 'Temporary') tagClass = 'tag-temp';
          else if (r.cat === 'ScheduleOff') tagClass = 'tag-sched';

          return `
            <tr>
              <td>${r.id}</td>
              <td><span class=""tag ${tagClass}"">${r.catName}</span></td>
              <td>${r.st}</td>
              <td>${r.et}</td>
              <td><strong>${formatDuration(r.dur)}</strong></td>
              <td>${r.target || '-'}</td>
              <td>${r.detail || '-'}</td>
            </tr>
          `;
        }).join('');
      }
    }

    function renderBarChart(sums) {
      const canvas = document.getElementById('barCanvas');
      const ctx = canvas.getContext('2d');
      const dpr = window.devicePixelRatio || 1;
      const rect = canvas.getBoundingClientRect();
      canvas.width = rect.width * dpr;
      canvas.height = rect.height * dpr;
      ctx.scale(dpr, dpr);

      const w = rect.width;
      const h = rect.height;
      ctx.clearRect(0, 0, w, h);

      if (sums.length === 0) {
        ctx.fillStyle = '#64748b';
        ctx.font = '14px Inter, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('區間內無圖表數據', w / 2, h / 2);
        return;
      }

      // 顯示最多最近 14 筆日期
      const displaySums = sums.slice(-14);
      let maxVal = 1;
      displaySums.forEach(s => { if (s.total > maxVal) maxVal = s.total; });

      const padL = 60, padR = 20, padT = 30, padB = 40;
      const chartW = w - padL - padR;
      const chartH = h - padT - padB;

      // 繪製 Y 軸刻度線
      ctx.strokeStyle = '#1e293b';
      ctx.lineWidth = 1;
      ctx.fillStyle = '#64748b';
      ctx.font = '11px Inter, sans-serif';
      ctx.textAlign = 'right';

      const steps = 4;
      for (let i = 0; i <= steps; i++) {
        const yVal = (maxVal / steps) * i;
        const y = padT + chartH - (chartH / steps) * i;
        ctx.beginPath();
        ctx.moveTo(padL, y);
        ctx.lineTo(w - padR, y);
        ctx.stroke();

        ctx.fillText(formatDuration(yVal), padL - 8, y + 4);
      }

      // 繪製堆疊柱
      const barCount = displaySums.length;
      const barSpacing = chartW / barCount;
      const barW = Math.min(36, barSpacing * 0.65);

      displaySums.forEach((s, idx) => {
        const centerX = padL + idx * barSpacing + barSpacing / 2;
        const x = centerX - barW / 2;

        const hTemp = (s.temp / maxVal) * chartH;
        const hStop = (s.stop / maxVal) * chartH;
        const hSched = (s.sched / maxVal) * chartH;

        let curY = padT + chartH;

        // 1. 排程離峰
        if (hSched > 0) {
          ctx.fillStyle = '#34d399';
          curY -= hSched;
          ctx.fillRect(x, curY, barW, hSched);
        }
        // 2. 手動停止
        if (hStop > 0) {
          ctx.fillStyle = '#818cf8';
          curY -= hStop;
          ctx.fillRect(x, curY, barW, hStop);
        }
        // 3. 暫時關閉
        if (hTemp > 0) {
          ctx.fillStyle = '#f59e0b';
          curY -= hTemp;
          ctx.fillRect(x, curY, barW, hTemp);
        }

        // X 軸標籤 (日期)
        ctx.fillStyle = '#94a3b8';
        ctx.textAlign = 'center';
        ctx.font = '11px Inter, sans-serif';
        const label = s.date.slice(5); // MM-dd
        ctx.fillText(label, centerX, h - padB + 18);
      });
    }

    function renderDonutChart(recs) {
      const canvas = document.getElementById('donutCanvas');
      const ctx = canvas.getContext('2d');
      const dpr = window.devicePixelRatio || 1;
      const rect = canvas.getBoundingClientRect();
      canvas.width = rect.width * dpr;
      canvas.height = rect.height * dpr;
      ctx.scale(dpr, dpr);

      const w = rect.width;
      const h = rect.height;
      ctx.clearRect(0, 0, w, h);

      let totalTemp = 0, totalStop = 0, totalSched = 0;
      recs.forEach(r => {
        if (r.cat === 'Temporary') totalTemp += r.dur;
        else if (r.cat === 'ManualStop') totalStop += r.dur;
        else if (r.cat === 'ScheduleOff') totalSched += r.dur;
      });
      const grandTotal = totalTemp + totalStop + totalSched;

      if (grandTotal === 0) {
        ctx.fillStyle = '#64748b';
        ctx.font = '14px Inter, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('尚無暫停紀錄', w / 2, h / 2);
        return;
      }

      const centerX = w / 2;
      const centerY = h / 2 - 12;
      const radius = Math.min(centerX, centerY) - 25;
      const innerRadius = radius * 0.62;

      const slices = [
        { label: '暫時關閉', val: totalTemp, color: '#f59e0b' },
        { label: '手動停止', val: totalStop, color: '#818cf8' },
        { label: '排程待命', val: totalSched, color: '#34d399' }
      ];

      let startAngle = -Math.PI / 2;
      slices.forEach(slice => {
        if (slice.val <= 0) return;
        const angle = (slice.val / grandTotal) * 2 * Math.PI;
        ctx.beginPath();
        ctx.arc(centerX, centerY, radius, startAngle, startAngle + angle);
        ctx.arc(centerX, centerY, innerRadius, startAngle + angle, startAngle, true);
        ctx.closePath();
        ctx.fillStyle = slice.color;
        ctx.fill();
        startAngle += angle;
      });

      // 中心文字
      ctx.fillStyle = '#ffffff';
      ctx.font = 'bold 15px Inter, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(formatDuration(grandTotal), centerX, centerY + 5);

      // 下方圖例與佔比
      const legY = h - 20;
      ctx.font = '11px Inter, sans-serif';
      let legX = 20;
      slices.forEach(s => {
        const pct = ((s.val / grandTotal) * 100).toFixed(1) + '%';
        ctx.fillStyle = s.color;
        ctx.fillRect(legX, legY - 8, 8, 8);
        ctx.fillStyle = '#94a3b8';
        ctx.textAlign = 'left';
        ctx.fillText(s.label + ' ' + pct, legX + 12, legY);
        legX += w / 3;
      });
    }

    function renderAll() {
      const { recs, sums } = getFilteredData();
      renderKPIs(recs, sums);
      renderTables();
      renderBarChart(sums);
      renderDonutChart(recs);
    }

    function exportCsv() {
      let csv = '\uFEFFId,開始時間,結束時間,持續秒數,暫停類型,目標視窗,說明備註\n';
      rawRecords.forEach(r => {
        csv += `${r.id},""${r.st}"",""${r.et}"",${r.dur},""${r.catName}"",""${(r.target||'').replace(/""/g, '""""')}"",""${(r.detail||'').replace(/""/g, '""""')}""\n`;
      });
      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.setAttribute('download', `afkkeeper_pause_export_${new Date().toISOString().slice(0,10)}.csv`);
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
    }

    window.addEventListener('resize', () => {
      const { sums, recs } = getFilteredData();
      renderBarChart(sums);
      renderDonutChart(recs);
    });

    // 初始載入
    window.addEventListener('DOMContentLoaded', () => {
      renderAll();
    });
  </script>
</body>
</html>";
        }
    }

    // ====== 半透明防誤觸遮罩視窗 ======
    internal class OverlayForm : Form
    {
        private IntPtr targetHwnd = IntPtr.Zero;
        private readonly Timer trackTimer = new Timer();
        private Panel pnlCard;
        private Label lblTitle;
        private Label lblSubtitle;
        private Button btnPause;
        private Button btnSchedule;
        private Label lblNote;

        public bool SuppressOverlay { get; set; }

        public event EventHandler PauseRequested;
        public event EventHandler ScheduleRequested;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0.68;
            BackColor = Color.FromArgb(18, 22, 30);
            ForeColor = Color.White;
            Font = new Font("Microsoft JhengHei UI", 10F);
            DoubleBuffered = true;

            BuildUi();

            trackTimer.Interval = 50;
            trackTimer.Tick += TrackTimer_Tick;
        }

        private void BuildUi()
        {
            pnlCard = new Panel
            {
                Size = new Size(380, 230),
                BackColor = Color.FromArgb(28, 34, 46)
            };
            pnlCard.Paint += (s, e) =>
            {
                // 繪製卡片外框線
                using (var pen = new Pen(Color.FromArgb(65, 80, 108), 1.5f))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, pnlCard.Width - 1, pnlCard.Height - 1);
                }
            };

            lblTitle = new Label
            {
                Text = "🛡️ AfkKeeper 防誤觸鎖定中",
                Font = new Font("Microsoft JhengHei UI", 12.5F, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Left = 10,
                Top = 18,
                Width = 360,
                Height = 30
            };
            pnlCard.Controls.Add(lblTitle);

            lblSubtitle = new Label
            {
                Text = "目標視窗保護中 · 已防止滑鼠誤操作此遊戲\n請點擊下方任一按鈕以解鎖操作：",
                Font = new Font("Microsoft JhengHei UI", 9.5F),
                ForeColor = Color.FromArgb(200, 210, 225),
                TextAlign = ContentAlignment.MiddleCenter,
                Left = 10,
                Top = 52,
                Width = 360,
                Height = 44
            };
            pnlCard.Controls.Add(lblSubtitle);

            btnPause = new Button
            {
                Text = "⏸ 暫時關閉",
                Left = 32,
                Top = 115,
                Width = 146,
                Height = 44,
                BackColor = Color.FromArgb(217, 83, 79),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnPause.FlatAppearance.BorderSize = 0;
            btnPause.Click += (s, e) =>
            {
                if (PauseRequested != null) PauseRequested(this, EventArgs.Empty);
            };
            pnlCard.Controls.Add(btnPause);

            btnSchedule = new Button
            {
                Text = "⏰ 每日定時",
                Left = 202,
                Top = 115,
                Width = 146,
                Height = 44,
                BackColor = Color.FromArgb(41, 128, 185),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSchedule.FlatAppearance.BorderSize = 0;
            btnSchedule.Click += (s, e) =>
            {
                if (ScheduleRequested != null) ScheduleRequested(this, EventArgs.Empty);
            };
            pnlCard.Controls.Add(btnSchedule);

            lblNote = new Label
            {
                Text = "（按下任一按鈕後即可正常操作視窗）",
                Font = new Font("Microsoft JhengHei UI", 8.5F),
                ForeColor = Color.FromArgb(145, 155, 175),
                TextAlign = ContentAlignment.MiddleCenter,
                Left = 10,
                Top = 180,
                Width = 360,
                Height = 25
            };
            pnlCard.Controls.Add(lblNote);

            Controls.Add(pnlCard);

            Resize += (s, e) => CenterCard();
        }

        private void CenterCard()
        {
            if (pnlCard == null) return;
            pnlCard.Left = Math.Max(10, (ClientSize.Width - pnlCard.Width) / 2);
            pnlCard.Top = Math.Max(10, (ClientSize.Height - pnlCard.Height) / 2);
        }

        public void SetTarget(IntPtr hWnd)
        {
            targetHwnd = hWnd;
            if (targetHwnd != IntPtr.Zero && Native.IsWindow(targetHwnd))
            {
                UpdatePositionAndZOrder();
                trackTimer.Start();
            }
            else
            {
                trackTimer.Stop();
                Hide();
            }
        }

        private void TrackTimer_Tick(object sender, EventArgs e)
        {
            UpdatePositionAndZOrder();
        }

        private void UpdatePositionAndZOrder()
        {
            if (targetHwnd == IntPtr.Zero || !Native.IsWindow(targetHwnd))
            {
                if (Visible) Hide();
                return;
            }

            // 視窗最小化或隱藏時，遮罩隱藏
            if (Native.IsIconic(targetHwnd) || !Native.IsWindowVisible(targetHwnd))
            {
                if (Visible) Hide();
                return;
            }

            // 若後台送鍵中，抑制遮罩避免閃爍
            if (SuppressOverlay)
            {
                if (Visible) Hide();
                return;
            }

            // 焦點檢查：只有目標視窗（例如 Roblox）或遮罩自身取得焦點 (Focus) 時，才顯示遮罩！
            // 當操作其他視窗時，遮罩立刻隱藏，完全不影響其他工作與操作。
            IntPtr fore = Native.GetForegroundWindow();
            uint forePid = 0;
            Native.GetWindowThreadProcessId(fore, out forePid);
            uint targetPid = 0;
            Native.GetWindowThreadProcessId(targetHwnd, out targetPid);

            bool isFocused = (fore == targetHwnd || fore == Handle || (targetPid != 0 && forePid == targetPid));
            if (!isFocused)
            {
                if (Visible) Hide();
                return;
            }

            Native.RECT rect;
            if (!Native.GetWindowBounds(targetHwnd, out rect) || rect.Width < 20 || rect.Height < 20)
            {
                if (Visible) Hide();
                return;
            }

            // 更新大小與位置
            if (Left != rect.Left || Top != rect.Top || Width != rect.Width || Height != rect.Height)
            {
                Bounds = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
                CenterCard();
            }

            // 目標視窗取得焦點時，將遮罩置頂覆蓋於目標視窗上
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            if (!Visible) Show();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            trackTimer.Stop();
            base.OnFormClosing(e);
        }
    }

    // ====== 暫停即將到期時在指定視窗右上角的浮動延長按鈕 ======
    internal class ExtendFloatForm : Form
    {
        private IntPtr targetHwnd = IntPtr.Zero;
        private DateTime pauseUntil = DateTime.MinValue;
        private readonly Timer trackTimer = new Timer();
        private Button btnExtend;

        public event EventHandler ExtendRequested;

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public ExtendFloatForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(185, 40);
            Padding = new Padding(1);
            BackColor = Color.FromArgb(40, 48, 64);
            ForeColor = Color.White;
            Font = new Font("Microsoft JhengHei UI", 9.5F, FontStyle.Bold);
            DoubleBuffered = true;

            btnExtend = new Button
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(235, 140, 25),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Text = "⏳ ＋延長 1 分鐘"
            };
            btnExtend.FlatAppearance.BorderSize = 0;
            btnExtend.FlatAppearance.MouseOverBackColor = Color.FromArgb(250, 160, 45);
            btnExtend.FlatAppearance.MouseDownBackColor = Color.FromArgb(210, 120, 15);
            btnExtend.Click += (s, e) =>
            {
                if (ExtendRequested != null) ExtendRequested(this, EventArgs.Empty);
                if (targetHwnd != IntPtr.Zero && Native.IsWindow(targetHwnd))
                {
                    Native.ForceForeground(targetHwnd);
                }
            };
            Controls.Add(btnExtend);

            trackTimer.Interval = 50;
            trackTimer.Tick += TrackTimer_Tick;
        }

        public void SetTarget(IntPtr hWnd, DateTime until)
        {
            targetHwnd = hWnd;
            pauseUntil = until;
            if (targetHwnd != IntPtr.Zero && Native.IsWindow(targetHwnd) && pauseUntil > DateTime.Now)
            {
                UpdatePositionAndState();
                trackTimer.Start();
            }
            else
            {
                trackTimer.Stop();
                Hide();
            }
        }

        public void UpdatePauseUntil(DateTime until)
        {
            pauseUntil = until;
            UpdatePositionAndState();
        }

        private void TrackTimer_Tick(object sender, EventArgs e)
        {
            UpdatePositionAndState();
        }

        private void UpdatePositionAndState()
        {
            if (targetHwnd == IntPtr.Zero || !Native.IsWindow(targetHwnd))
            {
                if (Visible) Hide();
                return;
            }

            DateTime now = DateTime.Now;
            double remainSec = (pauseUntil - now).TotalSeconds;

            // 只有剩餘時間小於等於 60 秒且大於 0 秒時才顯示
            if (remainSec <= 0 || remainSec > 60.5)
            {
                if (Visible) Hide();
                return;
            }

            // 視窗最小化或隱藏時，隱藏浮動按鈕
            if (Native.IsIconic(targetHwnd) || !Native.IsWindowVisible(targetHwnd))
            {
                if (Visible) Hide();
                return;
            }

            // 焦點檢查：只有目標視窗獲取焦點或按鈕自身獲取焦點時才顯示，操作其他視窗時完全隱藏
            IntPtr fore = Native.GetForegroundWindow();
            uint forePid = 0;
            Native.GetWindowThreadProcessId(fore, out forePid);
            uint targetPid = 0;
            Native.GetWindowThreadProcessId(targetHwnd, out targetPid);

            bool isFocused = (fore == targetHwnd || fore == Handle || (targetPid != 0 && forePid == targetPid));
            if (!isFocused)
            {
                if (Visible) Hide();
                return;
            }

            Native.RECT rect;
            if (!Native.GetWindowBounds(targetHwnd, out rect) || rect.Width < 50 || rect.Height < 50)
            {
                if (Visible) Hide();
                return;
            }

            // 計算目標視窗右上角座標（避開視窗原生關閉按鈕，貼合於內容區右上角）
            int posX = rect.Right - Width - 16;
            int posY = rect.Top + 40;

            if (Left != posX || Top != posY)
            {
                Location = new Point(posX, posY);
            }

            // 更新倒數秒數文字
            int sec = (int)Math.Ceiling(remainSec);
            string newText = string.Format("⏳ ＋延長 1 分鐘 ({0}s)", sec);
            if (btnExtend.Text != newText)
            {
                btnExtend.Text = newText;
            }

            // 置頂顯示
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, posX, posY, Width, Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            if (!Visible) Show();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            trackTimer.Stop();
            base.OnFormClosing(e);
        }
    }

    // ====== 暫時關閉對話框 ======
    internal class PauseDialog : Form
    {
        private RadioButton rb1;
        private RadioButton rb15;
        private RadioButton rb30;
        private RadioButton rb60;
        private RadioButton rbCustom;
        private NumericUpDown numCustom;

        public int SelectedMinutes
        {
            get
            {
                if (rb1.Checked) return 1;
                if (rb15.Checked) return 15;
                if (rb30.Checked) return 30;
                if (rb60.Checked) return 60;
                return (int)numCustom.Value;
            }
        }

        public PauseDialog()
        {
            Text = "暫時關閉掛機";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(390, 300);
            Font = new Font("Microsoft JhengHei UI", 10F);

            var lblPrompt = new Label
            {
                Text = "選擇暫停時間。暫停期間將解除遮罩，讓您可以正常操作此視窗：",
                Left = 24,
                Top = 18,
                Width = 342,
                Height = 44
            };
            Controls.Add(lblPrompt);

            rb1 = new RadioButton { Text = "暫停 1 分鐘（推薦）", Left = 36, Top = 68, AutoSize = true, Checked = true };
            rb15 = new RadioButton { Text = "暫停 15 分鐘", Left = 36, Top = 98, AutoSize = true };
            rb30 = new RadioButton { Text = "暫停 30 分鐘", Left = 36, Top = 128, AutoSize = true };
            rb60 = new RadioButton { Text = "暫停 60 分鐘（1 小時）", Left = 36, Top = 158, AutoSize = true };
            rbCustom = new RadioButton { Text = "自訂暫停：", Left = 36, Top = 188, AutoSize = true };

            numCustom = new NumericUpDown { Left = 138, Top = 186, Width = 70, Minimum = 1, Maximum = 720, Value = 45, Enabled = false };
            var lblUnit = new Label { Text = "分鐘", Left = 214, Top = 189, AutoSize = true };

            rbCustom.CheckedChanged += (s, e) => { numCustom.Enabled = rbCustom.Checked; };

            Controls.Add(rb1);
            Controls.Add(rb15);
            Controls.Add(rb30);
            Controls.Add(rb60);
            Controls.Add(rbCustom);
            Controls.Add(numCustom);
            Controls.Add(lblUnit);

            var btnOk = new Button
            {
                Text = "確認解除遮罩",
                DialogResult = DialogResult.OK,
                Left = 140,
                Top = 242,
                Width = 130,
                Height = 36,
                Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold)
            };
            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Left = 280,
                Top = 242,
                Width = 86,
                Height = 36
            };

            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }

    // ====== 每日定時排程對話框 ======
    internal class ScheduleDialog : Form
    {
        private CheckBox chkEnable;
        private NumericUpDown numStartH;
        private NumericUpDown numStartM;
        private NumericUpDown numEndH;
        private NumericUpDown numEndM;
        private Label lblPreview;

        public DailyScheduleConfig Config { get; private set; }

        public ScheduleDialog(DailyScheduleConfig current)
        {
            Config = new DailyScheduleConfig
            {
                Enabled = current.Enabled,
                StartHour = current.StartHour,
                StartMinute = current.StartMinute,
                EndHour = current.EndHour,
                EndMinute = current.EndMinute
            };

            Text = "每日定時排程設定";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 360);
            Font = new Font("Microsoft JhengHei UI", 10F);

            chkEnable = new CheckBox
            {
                Text = "啟用每日定時自動掛機",
                Left = 24,
                Top = 20,
                AutoSize = true,
                Checked = Config.Enabled,
                Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold)
            };
            Controls.Add(chkEnable);

            var pnlGroup = new Panel { Left = 24, Top = 56, Width = 412, Height = 220 };

            var lblStart = new Label { Text = "每日掛機開始時間：", Left = 10, Top = 12, AutoSize = true };
            numStartH = new NumericUpDown { Left = 160, Top = 8, Width = 60, Minimum = 0, Maximum = 23, Value = Config.StartHour };
            var lblCol1 = new Label { Text = "點", Left = 225, Top = 12, AutoSize = true };
            numStartM = new NumericUpDown { Left = 255, Top = 8, Width = 60, Minimum = 0, Maximum = 59, Value = Config.StartMinute };
            var lblMin1 = new Label { Text = "分", Left = 320, Top = 12, AutoSize = true };

            var lblEnd = new Label { Text = "每日掛機結束時間：", Left = 10, Top = 52, AutoSize = true };
            numEndH = new NumericUpDown { Left = 160, Top = 48, Width = 60, Minimum = 0, Maximum = 23, Value = Config.EndHour };
            var lblCol2 = new Label { Text = "點", Left = 225, Top = 52, AutoSize = true };
            numEndM = new NumericUpDown { Left = 255, Top = 48, Width = 60, Minimum = 0, Maximum = 59, Value = Config.EndMinute };
            var lblMin2 = new Label { Text = "分", Left = 320, Top = 52, AutoSize = true };

            var lblDesc = new Label
            {
                Text = "運作說明：\n• 在設定時段內：自動啟用掛機、覆蓋半透明遮罩並定時送鍵。\n• 在設定時段外：自動解除遮罩，恢復正常操作視窗。\n• 支援跨夜設定（例如 23:00 至 07:00）。",
                Left = 10,
                Top = 92,
                Width = 392,
                Height = 72,
                ForeColor = Color.DimGray
            };

            lblPreview = new Label
            {
                Text = "",
                Left = 10,
                Top = 175,
                Width = 392,
                Height = 30,
                ForeColor = Color.DarkBlue,
                Font = new Font("Microsoft JhengHei UI", 9.5F, FontStyle.Bold)
            };

            pnlGroup.Controls.Add(lblStart);
            pnlGroup.Controls.Add(numStartH);
            pnlGroup.Controls.Add(lblCol1);
            pnlGroup.Controls.Add(numStartM);
            pnlGroup.Controls.Add(lblMin1);

            pnlGroup.Controls.Add(lblEnd);
            pnlGroup.Controls.Add(numEndH);
            pnlGroup.Controls.Add(lblCol2);
            pnlGroup.Controls.Add(numEndM);
            pnlGroup.Controls.Add(lblMin2);

            pnlGroup.Controls.Add(lblDesc);
            pnlGroup.Controls.Add(lblPreview);

            Controls.Add(pnlGroup);

            chkEnable.CheckedChanged += (s, e) =>
            {
                pnlGroup.Enabled = chkEnable.Checked;
                UpdatePreview();
            };
            numStartH.ValueChanged += (s, e) => UpdatePreview();
            numStartM.ValueChanged += (s, e) => UpdatePreview();
            numEndH.ValueChanged += (s, e) => UpdatePreview();
            numEndM.ValueChanged += (s, e) => UpdatePreview();

            pnlGroup.Enabled = chkEnable.Checked;
            UpdatePreview();

            var btnSave = new Button
            {
                Text = "儲存並套用",
                DialogResult = DialogResult.OK,
                Left = 200,
                Top = 296,
                Width = 130,
                Height = 36,
                Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold)
            };
            btnSave.Click += (s, e) =>
            {
                Config.Enabled = chkEnable.Checked;
                Config.StartHour = (int)numStartH.Value;
                Config.StartMinute = (int)numStartM.Value;
                Config.EndHour = (int)numEndH.Value;
                Config.EndMinute = (int)numEndM.Value;
            };

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Left = 345,
                Top = 296,
                Width = 90,
                Height = 36
            };

            Controls.Add(btnSave);
            Controls.Add(btnCancel);
            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        private void UpdatePreview()
        {
            if (!chkEnable.Checked)
            {
                lblPreview.Text = "目前狀態：未啟用每日排程（手動開始/停止模式）";
                lblPreview.ForeColor = Color.Gray;
                return;
            }

            var temp = new DailyScheduleConfig
            {
                StartHour = (int)numStartH.Value,
                StartMinute = (int)numStartM.Value,
                EndHour = (int)numEndH.Value,
                EndMinute = (int)numEndM.Value
            };
            bool inSched = temp.IsInSchedule(DateTime.Now);
            if (inSched)
            {
                lblPreview.Text = string.Format("目前時間：{0:HH:mm:ss}（在排程內：將覆蓋遮罩並掛機）", DateTime.Now);
                lblPreview.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblPreview.Text = string.Format("目前時間：{0:HH:mm:ss}（在排程外：將解鎖視窗正常操作）", DateTime.Now);
                lblPreview.ForeColor = Color.DarkOrange;
            }
        }
    }

    // ====== 主視窗 ======
    internal class MainForm : Form
    {
        // UI 控制項
        private ComboBox cboWindows;
        private Button btnRefresh;
        private ComboBox cboKey;
        private NumericUpDown numInterval;
        private NumericUpDown numJitter;
        private CheckBox chkIdleOnly;
        private NumericUpDown numIdleSec;
        private NumericUpDown numMaxWaitSec;
        private CheckBox chkEnableOverlay;
        private Button btnDailySchedule;
        private Button btnStart;
        private Button btnStop;
        private Button btnResume;
        private Button btnReport;
        private Label lblStatus;
        private TextBox txtLog;
        private NotifyIcon tray;

        // 邏輯
        private readonly Timer tick = new Timer();   // 每秒跑一次的主迴圈
        private readonly Random rng = new Random();
        private bool running = false;
        private DateTime nextDue;          // 下次預定送鍵時間
        private DateTime pendingSince;     // 進入「待送出」狀態的時間（等你閒置）
        private bool pending = false;      // 已到期、正在等你手停下來

        // 遮罩與定時
        private OverlayForm overlay = null;
        private ExtendFloatForm extendForm = null;
        private readonly DailyScheduleConfig scheduleConfig = new DailyScheduleConfig();
        private bool isPaused = false;
        private DateTime pauseUntil = DateTime.MinValue;
        private string configPath;

        // 暫停統計追蹤器
        private readonly PauseTracker tracker = new PauseTracker();

        // 按鍵清單：顯示名稱 -> Virtual-Key code
        private static readonly Dictionary<string, ushort> KeyMap = new Dictionary<string, ushort>
        {
            { "上箭頭 Up", 0x26 },
            { "ESC", 0x1B },
            { "空白鍵 Space", 0x20 },
            { "W", 0x57 },
            { "下箭頭 Down", 0x28 },
            { "E", 0x45 },
            { "數字 0", 0x30 },
        };

        public MainForm()
        {
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "afkkeeper_config.ini");
            BuildUi();
            RefreshWindowList();
            LoadConfig();

            tracker.TransitionState(TrackerState.ManualStopped, GetSelectedTargetName(), "軟體就緒待命中");

            tick.Interval = 1000;
            tick.Tick += Tick_Tick;
        }

        private string GetSelectedTargetName()
        {
            var sel = cboWindows != null ? cboWindows.SelectedItem as WindowItem : null;
            if (sel != null) return sel.ToString();
            return "未指定視窗";
        }

        private void BuildUi()
        {
            Text = "AfkKeeper — 視窗定時送鍵（含防誤觸遮罩）";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 680);
            Font = new Font("Microsoft JhengHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;

            int x = 24, y = 20;     // 外邊距
            int ctrlX = 200;        // 控制項統一起點（與標籤拉開距離）
            int rowH = 44;          // 每列間距
            int numW = 90;

            // 目標視窗
            AddLabel("目標視窗：", x, y + 4, 0);
            cboWindows = new ComboBox { Left = ctrlX, Top = y, Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
            Controls.Add(cboWindows);
            btnRefresh = new Button { Left = ctrlX + 392, Top = y - 2, Width = 90, Height = 34, Text = "刷新" };
            btnRefresh.Click += (s, e) => RefreshWindowList();
            Controls.Add(btnRefresh);
            y += rowH;

            // 送出按鍵
            AddLabel("送出按鍵：", x, y + 4, 0);
            cboKey = new ComboBox { Left = ctrlX, Top = y, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var k in KeyMap.Keys) cboKey.Items.Add(k);
            cboKey.SelectedItem = "上箭頭 Up";
            if (cboKey.SelectedIndex < 0) cboKey.SelectedIndex = 0;
            Controls.Add(cboKey);
            y += rowH;

            // 間隔
            AddLabel("間隔 (秒)：", x, y + 4, 0);
            numInterval = new NumericUpDown { Left = ctrlX, Top = y, Width = numW, Minimum = 1, Maximum = 86400, Value = 780 };
            Controls.Add(numInterval);
            AddLabel("± 隨機 (秒)：", ctrlX + 130, y + 4, 0);
            numJitter = new NumericUpDown { Left = ctrlX + 300, Top = y, Width = numW, Minimum = 0, Maximum = 3600, Value = 120 };
            Controls.Add(numJitter);
            y += rowH;

            // 閒置條件
            chkIdleOnly = new CheckBox { Left = x, Top = y, AutoSize = true, Checked = true, Text = "只在我閒置時送鍵（最不干擾，推薦）" };
            Controls.Add(chkIdleOnly);
            y += 36;

            AddLabel("需閒置 (秒)：", x, y + 4, 0);
            numIdleSec = new NumericUpDown { Left = ctrlX, Top = y, Width = numW, Minimum = 1, Maximum = 600, Value = 3 };
            Controls.Add(numIdleSec);
            AddLabel("最久等 (秒)：", ctrlX + 130, y + 4, 0);
            numMaxWaitSec = new NumericUpDown { Left = ctrlX + 300, Top = y, Width = numW, Minimum = 5, Maximum = 3600, Value = 90 };
            Controls.Add(numMaxWaitSec);
            y += 38;

            var hint = new Label
            {
                Left = x, Top = y, AutoSize = true, MaximumSize = new Size(672, 0),
                ForeColor = Color.Gray,
                Text = "到期後會等你手停下來再送；超過「最久等」秒數仍會強制送出，避免被遊戲踢。"
            };
            Controls.Add(hint);
            y += 42;

            // 遮罩與定時設定
            chkEnableOverlay = new CheckBox
            {
                Left = x, Top = y + 4, AutoSize = true, Checked = true,
                Text = "掛機時在目標視窗覆蓋半透明遮罩（防誤觸鎖定，支援一鍵解鎖）",
                Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.DarkSlateGray
            };
            Controls.Add(chkEnableOverlay);

            btnDailySchedule = new Button
            {
                Left = ctrlX + 340, Top = y, Width = 142, Height = 32,
                Text = "⏰ 每日定時設定...",
                Font = new Font("Microsoft JhengHei UI", 9.5F)
            };
            btnDailySchedule.Click += (s, e) => OpenDailyScheduleDialog();
            Controls.Add(btnDailySchedule);
            y += 46;

            // 開始 / 停止 / 提前恢復 / 暫停報表
            btnStart = new Button { Left = x, Top = y, Width = 140, Height = 44, Text = "▶ 開始", Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold) };
            btnStart.Click += (s, e) => Start();
            Controls.Add(btnStart);

            btnStop = new Button { Left = x + 148, Top = y, Width = 140, Height = 44, Text = "■ 停止", Enabled = false, Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold) };
            btnStop.Click += (s, e) => Stop();
            Controls.Add(btnStop);

            btnResume = new Button
            {
                Left = x + 296, Top = y, Width = 150, Height = 44,
                Text = "▶ 提前恢復掛機",
                Visible = false,
                Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold),
                BackColor = Color.FromArgb(240, 173, 78),
                ForeColor = Color.Black
            };
            btnResume.Click += (s, e) => ResumeFromPause();
            Controls.Add(btnResume);

            btnReport = new Button
            {
                Left = x + 454, Top = y, Width = 218, Height = 44,
                Text = "📊 暫停統計報表",
                Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(14, 116, 144),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnReport.Click += (s, e) => tracker.OpenReport(GetSelectedTargetName());
            Controls.Add(btnReport);
            y += 56;

            lblStatus = new Label { Left = x, Top = y, AutoSize = true, Text = "狀態：閒置中（未啟動）", ForeColor = Color.DarkBlue, Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold) };
            Controls.Add(lblStatus);
            y += 32;

            txtLog = new TextBox
            {
                Left = x, Top = y, Width = 672, Height = 145,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White
            };
            Controls.Add(txtLog);

            // 嘗試載入執行檔圖示（由 /win32icon 嵌入）
            Icon appIcon = null;
            try
            {
                appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
            if (appIcon != null)
            {
                Icon = appIcon;
            }

            // 系統匣
            tray = new NotifyIcon
            {
                Icon = (appIcon != null) ? appIcon : SystemIcons.Application,
                Visible = true,
                Text = "AfkKeeper"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("顯示視窗", null, (s, e) => RestoreFromTray());
            menu.Items.Add("📊 查看暫停統計報表", null, (s, e) => tracker.OpenReport(GetSelectedTargetName()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("結束", null, (s, e) => { tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => RestoreFromTray();

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    tray.ShowBalloonTip(1500, "AfkKeeper", "已縮到系統匣，仍在背景執行中。", ToolTipIcon.Info);
                }
            };
            FormClosing += (s, e) =>
            {
                tracker.FlushActive();
                SaveConfig();
                HideOverlay();
                HideExtendForm();
                tray.Visible = false;
            };
        }

        private void AddLabel(string text, int left, int top, int width)
        {
            Controls.Add(new Label { Left = left, Top = top, AutoSize = true, Text = text });
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void Log(string msg)
        {
            string line = string.Format("{0:HH:mm:ss}  {1}\r\n", DateTime.Now, msg);
            txtLog.AppendText(line);
        }

        private void RefreshWindowList()
        {
            var prevSel = cboWindows.SelectedItem as WindowItem;
            var list = new List<WindowItem>();

            Native.EnumWindows((hWnd, lParam) =>
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                int len = Native.GetWindowTextLength(hWnd);
                if (len == 0) return true;

                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                string proc = "?";
                try
                {
                    uint pid;
                    Native.GetWindowThreadProcessId(hWnd, out pid);
                    proc = Native.GetProcessName(pid);
                }
                catch { }

                // 過濾掉自己
                if (proc.Equals("AfkKeeper", StringComparison.OrdinalIgnoreCase)) return true;

                list.Add(new WindowItem { Handle = hWnd, Title = title, ProcessName = proc });
                return true;
            }, IntPtr.Zero);

            cboWindows.BeginUpdate();
            cboWindows.Items.Clear();
            foreach (var w in list) cboWindows.Items.Add(w);
            cboWindows.EndUpdate();

            // 盡量還原先前選擇（依程序名+標題比對）
            if (prevSel != null && prevSel.ProcessName != "?")
            {
                for (int i = 0; i < cboWindows.Items.Count; i++)
                {
                    var w = (WindowItem)cboWindows.Items[i];
                    if (w.ProcessName.Equals(prevSel.ProcessName, StringComparison.OrdinalIgnoreCase) && w.Title == prevSel.Title)
                    {
                        cboWindows.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (cboWindows.SelectedIndex < 0 && cboWindows.Items.Count > 0)
            {
                // 預設嘗試選中 Roblox（比對程序名或視窗標題）
                for (int i = 0; i < cboWindows.Items.Count; i++)
                {
                    var w = (WindowItem)cboWindows.Items[i];
                    if (w.ProcessName.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        w.Title.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cboWindows.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        // 依「記住的程序名/標題」重新解析目前的視窗 handle（遊戲重開後 handle 會變）。
        private IntPtr ResolveTarget(WindowItem target)
        {
            if (target == null) return IntPtr.Zero;
            if (Native.IsWindow(target.Handle)) return target.Handle;

            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((hWnd, lParam) =>
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                try
                {
                    uint pid;
                    Native.GetWindowThreadProcessId(hWnd, out pid);
                    string proc = Native.GetProcessName(pid);
                    if (proc != "?" && proc.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false; // 找到就停
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private int ComputeNextSeconds()
        {
            int baseSec = (int)numInterval.Value;
            int span = (int)numJitter.Value;
            if (span <= 0) return baseSec;
            return baseSec + rng.Next(-span, span + 1);
        }

        private void ScheduleNext()
        {
            int sec = ComputeNextSeconds();
            if (sec < 5) sec = 5;
            nextDue = DateTime.Now.AddSeconds(sec);
            pending = false;
            Log(string.Format("已排程：下次約 {0:HH:mm:ss}（{1} 秒後）", nextDue, sec));
        }

        // ====== 遮罩控制 ======
        private void EnsureOverlay()
        {
            if (!chkEnableOverlay.Checked)
            {
                HideOverlay();
                return;
            }

            var target = cboWindows.SelectedItem as WindowItem;
            IntPtr hWnd = ResolveTarget(target);
            if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd))
            {
                HideOverlay();
                return;
            }

            if (overlay == null || overlay.IsDisposed)
            {
                overlay = new OverlayForm();
                overlay.PauseRequested += Overlay_PauseRequested;
                overlay.ScheduleRequested += Overlay_ScheduleRequested;
            }

            overlay.SetTarget(hWnd);
        }

        private void HideOverlay()
        {
            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.SetTarget(IntPtr.Zero);
                overlay.Hide();
            }
        }

        // ====== 浮動延長按鈕控制 ======
        private void EnsureExtendForm()
        {
            if (!isPaused || pauseUntil <= DateTime.Now)
            {
                HideExtendForm();
                return;
            }

            var target = cboWindows.SelectedItem as WindowItem;
            IntPtr hWnd = ResolveTarget(target);
            if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd))
            {
                HideExtendForm();
                return;
            }

            if (extendForm == null || extendForm.IsDisposed)
            {
                extendForm = new ExtendFloatForm();
                extendForm.ExtendRequested += ExtendForm_ExtendRequested;
            }

            extendForm.SetTarget(hWnd, pauseUntil);
        }

        private void HideExtendForm()
        {
            if (extendForm != null && !extendForm.IsDisposed)
            {
                extendForm.SetTarget(IntPtr.Zero, DateTime.MinValue);
                extendForm.Hide();
            }
        }

        private void ExtendForm_ExtendRequested(object sender, EventArgs e)
        {
            if (!isPaused) return;

            // 每次點選再延長 1 分鐘
            pauseUntil = pauseUntil.AddMinutes(1);
            TimeSpan remain = pauseUntil - DateTime.Now;

            if (remain.TotalSeconds > 60)
            {
                HideExtendForm();
            }
            else if (extendForm != null && !extendForm.IsDisposed)
            {
                extendForm.UpdatePauseUntil(pauseUntil);
            }

            Log(string.Format("【延長暫停】手動延長暫停 1 分鐘，視窗繼續解鎖。預計 {0:HH:mm:ss} 自動恢復。", pauseUntil));
            tracker.UpdateActiveDetail(string.Format("手動延長暫停（預計 {0:HH:mm:ss} 自動恢復）", pauseUntil));
            lblStatus.Text = string.Format("狀態：暫停中（剩餘 {0} 分 {1} 秒後恢復掛機，視窗已解鎖）",
                (int)remain.TotalMinutes, remain.Seconds);
        }

        private void Overlay_PauseRequested(object sender, EventArgs e)
        {
            HideOverlay();
            using (var dlg = new PauseDialog())
            {
                dlg.TopMost = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    int mins = dlg.SelectedMinutes;
                    isPaused = true;
                    pauseUntil = DateTime.Now.AddMinutes(mins);
                    btnResume.Visible = true;
                    tracker.TransitionState(TrackerState.TemporaryPause, GetSelectedTargetName(), string.Format("暫時關閉掛機 {0} 分鐘", mins));
                    Log(string.Format("【暫時關閉】已暫停掛機 {0} 分鐘，視窗已解鎖。預計 {1:HH:mm:ss} 自動恢復。", mins, pauseUntil));
                    lblStatus.Text = string.Format("狀態：暫停中（剩餘 {0} 分鐘後恢復掛機與遮罩，視窗已解鎖）", mins);

                    if (mins <= 1)
                    {
                        EnsureExtendForm();
                    }
                    else
                    {
                        HideExtendForm();
                    }
                }
                else
                {
                    // 取消時若仍在掛機狀態，恢復遮罩保護
                    if (running && !isPaused && chkEnableOverlay.Checked)
                    {
                        EnsureOverlay();
                    }
                }
            }
        }

        private void Overlay_ScheduleRequested(object sender, EventArgs e)
        {
            HideOverlay();
            OpenDailyScheduleDialog();

            // 如果目前不在暫停且排程條件允許，恢復遮罩
            if (running && !isPaused && chkEnableOverlay.Checked)
            {
                DateTime now = DateTime.Now;
                if (!scheduleConfig.Enabled || scheduleConfig.IsInSchedule(now))
                {
                    EnsureOverlay();
                }
            }
        }

        private void OpenDailyScheduleDialog()
        {
            using (var dlg = new ScheduleDialog(scheduleConfig))
            {
                dlg.TopMost = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    scheduleConfig.Enabled = dlg.Config.Enabled;
                    scheduleConfig.StartHour = dlg.Config.StartHour;
                    scheduleConfig.StartMinute = dlg.Config.StartMinute;
                    scheduleConfig.EndHour = dlg.Config.EndHour;
                    scheduleConfig.EndMinute = dlg.Config.EndMinute;
                    SaveConfig();

                    Log(string.Format("已更新每日定時設定：{0}（{1:D2}:{2:D2} ～ {3:D2}:{4:D2}）",
                        scheduleConfig.Enabled ? "已啟用" : "未啟用",
                        scheduleConfig.StartHour, scheduleConfig.StartMinute,
                        scheduleConfig.EndHour, scheduleConfig.EndMinute));

                    if (running && scheduleConfig.Enabled)
                    {
                        if (!scheduleConfig.IsInSchedule(DateTime.Now))
                        {
                            HideOverlay();
                            tracker.TransitionState(TrackerState.ScheduleOff, GetSelectedTargetName(), string.Format("每日排程離峰待命（下次啟動：{0:D2}:{1:D2}）",
                                scheduleConfig.StartHour, scheduleConfig.StartMinute));
                            Log("目前在每日定時時段外，視窗保持解鎖正常操作狀態。");
                            lblStatus.Text = string.Format("狀態：每日定時待命中（下次啟動：{0:D2}:{1:D2}，視窗已解鎖）",
                                scheduleConfig.StartHour, scheduleConfig.StartMinute);
                        }
                    }
                }
            }
        }

        private void ResumeFromPause()
        {
            if (!isPaused) return;
            isPaused = false;
            HideExtendForm();
            btnResume.Visible = false;
            Log("手動提前結束暫停，恢復正常掛機與遮罩。");

            DateTime now = DateTime.Now;
            if (scheduleConfig.Enabled && !scheduleConfig.IsInSchedule(now))
            {
                tracker.TransitionState(TrackerState.ScheduleOff, GetSelectedTargetName(), "提前結束暫停，處於排程外待命");
            }
            else
            {
                tracker.TransitionState(TrackerState.Running, GetSelectedTargetName(), "提前結束暫停，恢復掛機");
                ScheduleNext();
                if (chkEnableOverlay.Checked) EnsureOverlay();
            }
        }

        private void Start()
        {
            var target = cboWindows.SelectedItem as WindowItem;
            if (target == null)
            {
                MessageBox.Show("請先選擇一個目標視窗。", "提醒", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            running = true;
            isPaused = false;
            HideExtendForm();
            btnResume.Visible = false;

            btnStart.Enabled = false;
            btnStop.Enabled = true;
            cboWindows.Enabled = false;
            btnRefresh.Enabled = false;
            cboKey.Enabled = false;
            numInterval.Enabled = false;
            numJitter.Enabled = false;

            Log("=== 啟動 ===  目標：" + target.ToString());

            // 檢查每日定時
            DateTime now = DateTime.Now;
            if (scheduleConfig.Enabled && !scheduleConfig.IsInSchedule(now))
            {
                tracker.TransitionState(TrackerState.ScheduleOff, target.ToString(), string.Format("排程時段外待命中（下次啟動：{0:D2}:{1:D2}）",
                    scheduleConfig.StartHour, scheduleConfig.StartMinute));
                Log(string.Format("目前處於排程時段外，視窗保持解鎖待命中（將在 {0:D2}:{1:D2} 自動啟用）。",
                    scheduleConfig.StartHour, scheduleConfig.StartMinute));
                lblStatus.Text = string.Format("狀態：每日定時待命中（下次啟動：{0:D2}:{1:D2}）",
                    scheduleConfig.StartHour, scheduleConfig.StartMinute);
            }
            else
            {
                tracker.TransitionState(TrackerState.Running, target.ToString(), "掛機中");
                if (chkEnableOverlay.Checked) EnsureOverlay();
                ScheduleNext();
            }

            tick.Start();
        }

        private void Stop()
        {
            running = false;
            pending = false;
            isPaused = false;
            HideExtendForm();
            btnResume.Visible = false;

            tick.Stop();
            HideOverlay();

            tracker.TransitionState(TrackerState.ManualStopped, GetSelectedTargetName(), "手動停止掛機");

            btnStart.Enabled = true;
            btnStop.Enabled = false;
            cboWindows.Enabled = true;
            btnRefresh.Enabled = true;
            cboKey.Enabled = true;
            numInterval.Enabled = true;
            numJitter.Enabled = true;
            lblStatus.Text = "狀態：已停止";
            Log("=== 停止 ===");
        }

        private void Tick_Tick(object sender, EventArgs e)
        {
            if (!running) return;
            DateTime now = DateTime.Now;

            // 1. 每日排程時段檢查
            if (scheduleConfig.Enabled)
            {
                bool inSched = scheduleConfig.IsInSchedule(now);
                if (!inSched)
                {
                    // 在排程外：解除遮罩，停止送鍵，解鎖正常操作
                    if (tracker.CurrentState != TrackerState.ScheduleOff && tracker.CurrentState != TrackerState.TemporaryPause)
                    {
                        tracker.TransitionState(TrackerState.ScheduleOff, GetSelectedTargetName(), string.Format("每日排程離峰待命（下次啟動：{0:D2}:{1:D2}）",
                            scheduleConfig.StartHour, scheduleConfig.StartMinute));
                    }
                    HideOverlay();
                    HideExtendForm();
                    pending = false;
                    lblStatus.Text = string.Format("狀態：每日定時待命中（下次啟動：{0:D2}:{1:D2}，視窗已解鎖）",
                        scheduleConfig.StartHour, scheduleConfig.StartMinute);
                    return;
                }
                else if (tracker.CurrentState == TrackerState.ScheduleOff)
                {
                    // 剛進入排程時段
                    tracker.TransitionState(TrackerState.Running, GetSelectedTargetName(), "進入排程時段，自動開始掛機");
                    if (chkEnableOverlay.Checked) EnsureOverlay();
                    ScheduleNext();
                }
            }

            // 2. 暫時關閉檢查
            if (isPaused)
            {
                if (now < pauseUntil)
                {
                    HideOverlay();
                    TimeSpan remain = pauseUntil - now;
                    int remMin = (int)remain.TotalMinutes;
                    int remSec = remain.Seconds;
                    lblStatus.Text = string.Format("狀態：暫停中（剩餘 {0} 分 {1} 秒後恢復掛機，視窗已解鎖）", remMin, remSec);

                    // 當剩餘暫停時間小於等於 1 分鐘（60秒）時，在指定視窗右上角顯示延長 1 分鐘按鈕
                    if (remain.TotalSeconds <= 60 && remain.TotalSeconds > 0)
                    {
                        EnsureExtendForm();
                    }
                    else
                    {
                        HideExtendForm();
                    }
                    return;
                }
                else
                {
                    // 暫停時間到
                    HideExtendForm();
                    isPaused = false;
                    btnResume.Visible = false;
                    Log("暫停時間已到，自動恢復掛機並重新覆蓋遮罩。");

                    if (scheduleConfig.Enabled && !scheduleConfig.IsInSchedule(now))
                    {
                        tracker.TransitionState(TrackerState.ScheduleOff, GetSelectedTargetName(), "暫停到期，處於排程外待命");
                    }
                    else
                    {
                        tracker.TransitionState(TrackerState.Running, GetSelectedTargetName(), "暫停到期，自動恢復掛機");
                        ScheduleNext();
                        if (chkEnableOverlay.Checked) EnsureOverlay();
                    }
                }
            }

            // 3. 確保遮罩就緒
            if (chkEnableOverlay.Checked)
            {
                EnsureOverlay();
            }
            else
            {
                HideOverlay();
            }

            // 4. 定時送鍵邏輯
            if (!pending)
            {
                int remain = (int)(nextDue - now).TotalSeconds;
                if (remain > 0)
                {
                    lblStatus.Text = string.Format("狀態：執行中，{0} 分 {1} 秒後送鍵", remain / 60, remain % 60);
                    return;
                }
                // 到期 → 進入待送出
                pending = true;
                pendingSince = now;
            }

            // pending 狀態：判斷是否該真的送出
            bool fire;
            if (!chkIdleOnly.Checked)
            {
                fire = true;
            }
            else
            {
                uint idleMs = Native.GetIdleMilliseconds();
                bool idleEnough = idleMs >= (uint)numIdleSec.Value * 1000;
                bool waitedTooLong = (now - pendingSince).TotalSeconds >= (double)numMaxWaitSec.Value;
                fire = idleEnough || waitedTooLong;

                if (!fire)
                {
                    lblStatus.Text = string.Format("狀態：已到期，等你手停下（目前閒置 {0:0.0} 秒）", idleMs / 1000.0);
                    return;
                }
            }

            DoSend();
            ScheduleNext();
        }

        private void DoSend()
        {
            var target = cboWindows.SelectedItem as WindowItem;
            IntPtr hWnd = ResolveTarget(target);
            if (hWnd == IntPtr.Zero)
            {
                Log("找不到目標視窗（可能已關閉）。略過這次。");
                lblStatus.Text = "狀態：找不到目標視窗";
                return;
            }

            ushort vk = KeyMap[(string)cboKey.SelectedItem];

            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.SuppressOverlay = true;
            }

            IntPtr prevFore = Native.GetForegroundWindow();
            try
            {
                Native.ForceForeground(hWnd);
                System.Threading.Thread.Sleep(120); // 等視窗真的取得焦點
                Native.SendKeyScan(vk);
                System.Threading.Thread.Sleep(40);
            }
            finally
            {
                // 把焦點還給你原本的視窗
                if (prevFore != IntPtr.Zero && prevFore != hWnd && Native.IsWindow(prevFore))
                {
                    Native.ForceForeground(prevFore);
                }
                if (overlay != null && !overlay.IsDisposed)
                {
                    overlay.SuppressOverlay = false;
                }
            }
            Log(string.Format("已送出 [{0}] 給「{1}」，並還原焦點。", cboKey.SelectedItem, target.Title));
        }

        // ====== 設定存取 ======
        private void SaveConfig()
        {
            try
            {
                var sb = new StringBuilder();
                var sel = cboWindows.SelectedItem as WindowItem;
                if (sel != null && !string.IsNullOrEmpty(sel.ProcessName) && sel.ProcessName != "?")
                {
                    sb.AppendLine("TargetProc=" + sel.ProcessName);
                }
                if (cboKey.SelectedItem != null) sb.AppendLine("Key=" + cboKey.SelectedItem.ToString());
                sb.AppendLine("Interval=" + numInterval.Value);
                sb.AppendLine("Jitter=" + numJitter.Value);
                sb.AppendLine("IdleOnly=" + chkIdleOnly.Checked);
                sb.AppendLine("IdleSec=" + numIdleSec.Value);
                sb.AppendLine("MaxWaitSec=" + numMaxWaitSec.Value);
                sb.AppendLine("EnableOverlay=" + chkEnableOverlay.Checked);
                sb.AppendLine("ScheduleEnabled=" + scheduleConfig.Enabled);
                sb.AppendLine("StartHour=" + scheduleConfig.StartHour);
                sb.AppendLine("StartMinute=" + scheduleConfig.StartMinute);
                sb.AppendLine("EndHour=" + scheduleConfig.EndHour);
                sb.AppendLine("EndMinute=" + scheduleConfig.EndMinute);

                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void LoadConfig()
        {
            if (!File.Exists(configPath)) return;
            try
            {
                string savedProc = null;
                string savedKey = null;

                foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    decimal dec;
                    bool b;
                    int n;

                    if (key == "TargetProc") savedProc = val;
                    else if (key == "Key") savedKey = val;
                    else if (key == "Interval" && decimal.TryParse(val, out dec) && dec >= numInterval.Minimum && dec <= numInterval.Maximum) numInterval.Value = dec;
                    else if (key == "Jitter" && decimal.TryParse(val, out dec) && dec >= numJitter.Minimum && dec <= numJitter.Maximum) numJitter.Value = dec;
                    else if (key == "IdleOnly" && bool.TryParse(val, out b)) chkIdleOnly.Checked = b;
                    else if (key == "IdleSec" && decimal.TryParse(val, out dec) && dec >= numIdleSec.Minimum && dec <= numIdleSec.Maximum) numIdleSec.Value = dec;
                    else if (key == "MaxWaitSec" && decimal.TryParse(val, out dec) && dec >= numMaxWaitSec.Minimum && dec <= numMaxWaitSec.Maximum) numMaxWaitSec.Value = dec;
                    else if (key == "EnableOverlay" && bool.TryParse(val, out b)) chkEnableOverlay.Checked = b;
                    else if (key == "ScheduleEnabled" && bool.TryParse(val, out b)) scheduleConfig.Enabled = b;
                    else if (key == "StartHour" && int.TryParse(val, out n)) scheduleConfig.StartHour = Math.Max(0, Math.Min(23, n));
                    else if (key == "StartMinute" && int.TryParse(val, out n)) scheduleConfig.StartMinute = Math.Max(0, Math.Min(59, n));
                    else if (key == "EndHour" && int.TryParse(val, out n)) scheduleConfig.EndHour = Math.Max(0, Math.Min(23, n));
                    else if (key == "EndMinute" && int.TryParse(val, out n)) scheduleConfig.EndMinute = Math.Max(0, Math.Min(59, n));
                }

                if (!string.IsNullOrEmpty(savedKey) && cboKey.Items.Contains(savedKey))
                {
                    cboKey.SelectedItem = savedKey;
                }

                if (!string.IsNullOrEmpty(savedProc) && savedProc != "?")
                {
                    for (int i = 0; i < cboWindows.Items.Count; i++)
                    {
                        var w = (WindowItem)cboWindows.Items[i];
                        if (w.ProcessName.Equals(savedProc, StringComparison.OrdinalIgnoreCase))
                        {
                            cboWindows.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (cboWindows.SelectedIndex < 0 && cboWindows.Items.Count > 0)
                {
                    // 預設嘗試選中 Roblox（比對程序名或視窗標題）
                    for (int i = 0; i < cboWindows.Items.Count; i++)
                    {
                        var w = (WindowItem)cboWindows.Items[i];
                        if (w.ProcessName.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            w.Title.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cboWindows.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }
}
