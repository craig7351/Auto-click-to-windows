// AfkKeeper —— 自訂時間，對指定的已開啟視窗送出按鍵（預設空白鍵）的小工具。
// 設計目標：
//   1) 真實輸入（SendInput，scan code），Roblox 等遊戲收得到。
//   2) 干擾最小：只在「你手沒在動」的空檔，短暫把目標視窗切到前景送鍵、再立刻把焦點還給你原本的視窗。
//   3) 反偵測友善：間隔可隨機抖動，避免毫秒不差的規律。純外部模擬輸入、不碰遊戲記憶體、不注入。
//
// 編譯：見 build.bat（用 Windows 內建的 csc.exe，不需安裝任何 SDK）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

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

    // ====== 主視窗 ======
    internal class MainForm : Form
    {
        // UI
        private ComboBox cboWindows;
        private Button btnRefresh;
        private ComboBox cboKey;
        private NumericUpDown numInterval;
        private NumericUpDown numJitter;
        private CheckBox chkIdleOnly;
        private NumericUpDown numIdleSec;
        private NumericUpDown numMaxWaitSec;
        private Button btnStart;
        private Button btnStop;
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

        // 按鍵清單：顯示名稱 -> Virtual-Key code
        private static readonly Dictionary<string, ushort> KeyMap = new Dictionary<string, ushort>
        {
            { "空白鍵 Space", 0x20 },
            { "W", 0x57 },
            { "上箭頭 Up", 0x26 },
            { "下箭頭 Down", 0x28 },
            { "E", 0x45 },
            { "數字 0", 0x30 },
        };

        public MainForm()
        {
            BuildUi();
            RefreshWindowList();

            tick.Interval = 1000;
            tick.Tick += Tick_Tick;
        }

        private void BuildUi()
        {
            Text = "AfkKeeper — 視窗定時送鍵";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 620);
            Font = new Font("Microsoft JhengHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;

            int x = 24, y = 24;     // 外邊距
            int ctrlX = 200;        // 控制項統一起點（與標籤拉開距離）
            int rowH = 46;          // 每列間距
            int numW = 90;          // 不設固定高度，讓控制項依字體自動算高，避免內容被裁切

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
            cboKey.SelectedIndex = 0;
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
            y += 40;

            AddLabel("需閒置 (秒)：", x, y + 4, 0);
            numIdleSec = new NumericUpDown { Left = ctrlX, Top = y, Width = numW, Minimum = 1, Maximum = 600, Value = 3 };
            Controls.Add(numIdleSec);
            AddLabel("最久等 (秒)：", ctrlX + 130, y + 4, 0);
            numMaxWaitSec = new NumericUpDown { Left = ctrlX + 300, Top = y, Width = numW, Minimum = 5, Maximum = 3600, Value = 90 };
            Controls.Add(numMaxWaitSec);
            y += rowH;

            var hint = new Label
            {
                Left = x, Top = y, AutoSize = true, MaximumSize = new Size(672, 0),
                ForeColor = Color.Gray,
                Text = "到期後會等你手停下來再送；超過「最久等」秒數仍會強制送出，避免被遊戲踢。"
            };
            Controls.Add(hint);
            y += 60;

            // 開始 / 停止
            btnStart = new Button { Left = x, Top = y, Width = 200, Height = 44, Text = "▶ 開始", Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold) };
            btnStart.Click += (s, e) => Start();
            Controls.Add(btnStart);
            btnStop = new Button { Left = x + 220, Top = y, Width = 200, Height = 44, Text = "■ 停止", Enabled = false, Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold) };
            btnStop.Click += (s, e) => Stop();
            Controls.Add(btnStop);
            y += 56;

            lblStatus = new Label { Left = x, Top = y, AutoSize = true, Text = "狀態：閒置中（未啟動）", ForeColor = Color.DarkBlue };
            Controls.Add(lblStatus);
            y += 32;

            txtLog = new TextBox
            {
                Left = x, Top = y, Width = 672, Height = 150,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White
            };
            Controls.Add(txtLog);

            // 系統匣
            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "AfkKeeper"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("顯示視窗", null, (s, e) => RestoreFromTray());
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
            FormClosing += (s, e) => { tray.Visible = false; };
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
                    proc = Process.GetProcessById((int)pid).ProcessName;
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
            if (prevSel != null)
            {
                for (int i = 0; i < cboWindows.Items.Count; i++)
                {
                    var w = (WindowItem)cboWindows.Items[i];
                    if (w.ProcessName == prevSel.ProcessName && w.Title == prevSel.Title)
                    {
                        cboWindows.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (cboWindows.SelectedIndex < 0 && cboWindows.Items.Count > 0)
            {
                // 預設嘗試選中 Roblox
                for (int i = 0; i < cboWindows.Items.Count; i++)
                {
                    var w = (WindowItem)cboWindows.Items[i];
                    if (w.ProcessName.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0)
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
                    string proc = Process.GetProcessById((int)pid).ProcessName;
                    if (proc == target.ProcessName)
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

        private void Start()
        {
            var target = cboWindows.SelectedItem as WindowItem;
            if (target == null)
            {
                MessageBox.Show("請先選擇一個目標視窗。", "提醒", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            running = true;
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            cboWindows.Enabled = false;
            btnRefresh.Enabled = false;
            cboKey.Enabled = false;
            numInterval.Enabled = false;
            numJitter.Enabled = false;

            Log("=== 啟動 ===  目標：" + target.ToString());
            ScheduleNext();
            tick.Start();
        }

        private void Stop()
        {
            running = false;
            pending = false;
            tick.Stop();
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
            }
            Log(string.Format("已送出 [{0}] 給「{1}」，並還原焦點。", cboKey.SelectedItem, target.Title));
        }
    }
}
