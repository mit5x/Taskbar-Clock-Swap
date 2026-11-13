// Taskbar Clock Swap
// Показывает в часах на панели задач Windows 10 время «Дополнительных часов 1» или «Дополнительных часов 2»
// (Панель управления → Дата и время → Дополнительные часы) вместо местного времени.
//
// Как это работает: системные часы на панели задач не умеют показывать другой часовой пояс,
// поэтому программа кладёт поверх них маленькое окно того же размера и цвета и рисует в нём
// время выбранных часов. Окно «прозрачно» для мыши: щелчок и наведение доходят до настоящих
// часов, поэтому календарь и всплывающая подсказка со всеми часами работают как обычно.
//
// Код написан на C# 5, чтобы его можно было собрать встроенным в Windows компилятором csc.exe
// (.NET Framework 4.x) без установки Visual Studio. См. build.bat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Taskbar Clock Swap")]
[assembly: AssemblyDescription("Показывает на панели задач время дополнительных часов Windows")]
[assembly: AssemblyProduct("Taskbar Clock Swap")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TaskbarClockSwap
{
    internal static class Program
    {
        internal const string AppName = "Taskbar Clock Swap";
        internal static int WM_ACTIVATE_REQUEST;

        [STAThread]
        private static void Main(string[] args)
        {
            WM_ACTIVATE_REQUEST = Native.RegisterWindowMessage("TaskbarClockSwap.ActivateRequest.{6E1C3F0A-91B2-4C8D-A1F7-3D2B9E5C7A10}");
            int requested = ParseClockArg(args);

            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\TaskbarClockSwap.{6E1C3F0A-91B2-4C8D-A1F7-3D2B9E5C7A10}", out createdNew))
            {
                if (!createdNew)
                {
                    // Программа уже запущена: просим её показать значок (и, если указано, переключить часы).
                    Native.PostMessage(Native.HWND_BROADCAST, WM_ACTIVATE_REQUEST, new IntPtr(requested + 1), IntPtr.Zero);
                    return;
                }

                if (requested >= 0) Settings.Clock = requested;

                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }

        // Аргументы командной строки: 1, 2, 0 / local, также /clock:1, -clock=2 и т. п.
        private static int ParseClockArg(string[] args)
        {
            foreach (string raw in args)
            {
                string a = raw.Trim().TrimStart('/', '-').ToLowerInvariant();
                if (a.StartsWith("clock")) a = a.Substring(5).TrimStart(':', '=');
                if (a == "1") return 1;
                if (a == "2") return 2;
                if (a == "0" || a == "local") return 0;
            }
            return -1;
        }
    }

    // ------------------------------------------------------------------ настройки

    internal static class Settings
    {
        private const string KeyPath = @"Software\TaskbarClockSwap";
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "TaskbarClockSwap";

        public static bool Exists
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k != null;
            }
        }

        private static int GetInt(string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }

        private static void SetInt(string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                    k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>0 — местное время (подмена выключена), 1 — часы 1, 2 — часы 2.</summary>
        public static int Clock
        {
            get { int v = GetInt("Clock", 1); return (v >= 0 && v <= 2) ? v : 1; }
            set { SetInt("Clock", value); }
        }

        public static bool ShowSeconds
        {
            get { return GetInt("ShowSeconds", 0) != 0; }
            set { SetInt("ShowSeconds", value ? 1 : 0); }
        }

        public static bool HideTrayIcon
        {
            get { return GetInt("HideTrayIcon", 0) != 0; }
            set { SetInt("HideTrayIcon", value ? 1 : 0); }
        }

        public static bool AutoStart
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                    {
                        if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                        else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                    }
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ дополнительные часы Windows

    internal sealed class AdditionalClock
    {
        public int Index;
        public bool Enabled;
        public string Name;
        public TimeZoneInfo Zone;

        public static AdditionalClock Read(int index)
        {
            AdditionalClock c = new AdditionalClock();
            c.Index = index;
            c.Name = "Часы " + index;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\TimeDate\AdditionalClocks\" + index))
                {
                    if (k == null) return c;
                    object en = k.GetValue("Enable");
                    c.Enabled = en is int && (int)en != 0;
                    string dn = k.GetValue("DisplayName") as string;
                    if (!string.IsNullOrEmpty(dn)) c.Name = dn;
                    string tzId = k.GetValue("TzRegKeyName") as string;
                    if (!string.IsNullOrEmpty(tzId)) c.Zone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                }
            }
            catch { }
            return c;
        }

        public string OffsetText()
        {
            if (Zone == null) return "";
            TimeSpan o = Zone.GetUtcOffset(DateTime.UtcNow);
            return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm");
        }
    }

    // ------------------------------------------------------------------ главное приложение (значок в трее)

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer timer;
        private readonly MessageWindow messageWindow;
        private readonly Dictionary<IntPtr, Overlay> overlays = new Dictionary<IntPtr, Overlay>();
        private readonly AdditionalClock[] clocks = new AdditionalClock[3];
        private readonly Native.WinEventDelegate winEventProc;
        private readonly IntPtr winEventHook;
        private Font font;
        private int selected;
        private bool showSeconds;
        private int ticks;
        private bool exiting;
        private bool inTick;

        private ToolStripMenuItem miClock1, miClock2, miLocal, miSeconds, miAutoStart, miHideIcon;

        public TrayApp()
        {
            bool firstRun = !Settings.Exists;
            selected = Settings.Clock;
            showSeconds = Settings.ShowSeconds;
            if (firstRun) Settings.Clock = selected;
            if (Settings.AutoStart) Settings.AutoStart = true; // обновить путь, если exe перенесли

            ReloadClocks();
            CreateFont();

            menu = BuildMenu();
            icon = new NotifyIcon();
            icon.Icon = MakeTrayIcon();
            icon.ContextMenuStrip = menu;
            icon.MouseUp += OnIconMouseUp;
            UpdateTrayText();
            icon.Visible = !Settings.HideTrayIcon;

            messageWindow = new MessageWindow(this);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            // Когда пользователь щёлкает по панели задач, она поднимается поверх нашего окна —
            // сразу возвращаем окно наверх, не дожидаясь таймера.
            winEventProc = OnWinEvent;
            winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.TimeChanged += OnTimeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            Tick();

            if (firstRun && icon.Visible)
            {
                icon.ShowBalloonTip(8000, Program.AppName,
                    "Программа запущена. Щёлкните по этому значку, чтобы выбрать, время каких часов показывать на панели задач.",
                    ToolTipIcon.Info);
            }
        }

        // ---------- основной цикл

        private void Tick()
        {
            if (exiting || inTick) return;
            inTick = true;
            try { TickCore(); }
            finally { inTick = false; }
        }

        private void TickCore()
        {
            ticks++;
            if (ticks % 10 == 0)
            {
                ReloadClocks(); // раз в 2 секунды подхватываем изменения в «Дата и время»
                UpdateTrayText();
            }

            List<ClockTarget> targets = FindTaskbarClocks();

            // Убираем окна для часов, которых больше нет (перезапуск Проводника, отключили монитор).
            List<IntPtr> stale = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, Overlay> kv in overlays)
            {
                bool found = false;
                foreach (ClockTarget t in targets) if (t.Clock == kv.Key) { found = true; break; }
                if (!found) stale.Add(kv.Key);
            }
            foreach (IntPtr h in stale) { overlays[h].Dispose(); overlays.Remove(h); }

            TimeZoneInfo zone = ActiveZone();
            if (zone == null)
            {
                foreach (Overlay o in overlays.Values) o.Hide();
                return;
            }

            DateTime now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

            foreach (ClockTarget t in targets)
            {
                Overlay ov;
                if (!overlays.TryGetValue(t.Clock, out ov))
                {
                    ov = new Overlay();
                    overlays[t.Clock] = ov;
                }

                Native.RECT rc;
                if (!Native.IsWindowVisible(t.Taskbar) || !Native.IsWindowVisible(t.Clock) ||
                    !Native.GetWindowRect(t.Clock, out rc) || rc.Right - rc.Left < 4 || rc.Bottom - rc.Top < 4)
                {
                    ov.Hide();
                    continue;
                }
                Rectangle r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);

                if (FullscreenAppActive(t.Taskbar, r))
                {
                    ov.Hide();
                    continue;
                }

                ov.Show(r, BuildText(now, r), font);
            }
        }

        private TimeZoneInfo ActiveZone()
        {
            if (selected == 1 || selected == 2)
            {
                AdditionalClock c = clocks[selected];
                if (c != null) return c.Zone;
            }
            return null;
        }

        private string BuildText(DateTime now, Rectangle r)
        {
            DateTimeFormatInfo f = CultureInfo.CurrentCulture.DateTimeFormat;
            string time = now.ToString(showSeconds ? f.LongTimePattern : f.ShortTimePattern, CultureInfo.CurrentCulture);
            // Как в Windows: при маленькой панели задач (мелкие значки) — только время.
            if (r.Height >= font.Height * 2 + 2)
                return time + "\n" + now.ToString(f.ShortDatePattern, CultureInfo.CurrentCulture);
            return time;
        }

        private struct ClockTarget
        {
            public IntPtr Taskbar;
            public IntPtr Clock;
        }

        private static List<ClockTarget> FindTaskbarClocks()
        {
            List<ClockTarget> list = new List<ClockTarget>();

            // Основная панель задач.
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                IntPtr clk = notify != IntPtr.Zero ? Native.FindWindowEx(notify, IntPtr.Zero, "TrayClockWClass", null) : IntPtr.Zero;
                if (clk != IntPtr.Zero)
                {
                    ClockTarget t; t.Taskbar = tray; t.Clock = clk; list.Add(t);
                }
            }

            // Панели задач на дополнительных мониторах (если там включены часы).
            IntPtr sec = IntPtr.Zero;
            while ((sec = Native.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                IntPtr clk = FindDescendant(sec, "ClockButton");
                if (clk != IntPtr.Zero)
                {
                    ClockTarget t; t.Taskbar = sec; t.Clock = clk; list.Add(t);
                }
            }
            return list;
        }

        private static IntPtr FindDescendant(IntPtr parent, string className)
        {
            IntPtr result = IntPtr.Zero;
            Native.EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
            {
                if (Native.GetClassNameString(h) == className) { result = h; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // Полноэкранное приложение (видео, игра, презентация): Проводник в этот момент снимает с панели
        // задач флаг «поверх всех окон». Повторяем это поведение, чтобы наши часы не висели поверх видео.
        private static bool FullscreenAppActive(IntPtr taskbar, Rectangle clockRect)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = Native.GetClassNameString(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;
            Native.RECT wr;
            if (!Native.GetWindowRect(fg, out wr)) return false;
            Rectangle mon = Screen.FromRectangle(clockRect).Bounds;
            if (wr.Left == mon.Left && wr.Top == mon.Top && wr.Right == mon.Right && wr.Bottom == mon.Bottom)
                return true; // окно ровно на весь экран
            bool coversMonitor = wr.Left <= mon.Left && wr.Top <= mon.Top && wr.Right >= mon.Right && wr.Bottom >= mon.Bottom;
            bool taskbarTopmost = (Native.GetWindowLong(taskbar, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            return coversMonitor && !taskbarTopmost;
        }

        private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            Tick();
        }

        // ---------- данные

        private void ReloadClocks()
        {
            clocks[1] = AdditionalClock.Read(1);
            clocks[2] = AdditionalClock.Read(2);
        }

        private void CreateFont()
        {
            float dpi = 96f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiY; } catch { }
            Font old = font;
            // Шрифт системных часов Windows 10 — Segoe UI 9 pt (12 px при 100 %).
            font = new Font("Segoe UI", 12f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
            if (old != null) old.Dispose();
            foreach (Overlay o in overlays.Values) o.Invalidate();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Пользователь мог поменять формат даты/времени в «Региональных стандартах».
            CultureInfo.CurrentCulture.ClearCachedData();
            foreach (Overlay o in overlays.Values) o.Invalidate();
        }

        private void OnTimeChanged(object sender, EventArgs e)
        {
            TimeZoneInfo.ClearCachedData();
            ReloadClocks();
            Tick();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            CreateFont();
            Tick();
        }

        // ---------- меню

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();

            ToolStripMenuItem header = new ToolStripMenuItem("Время на панели задач:");
            header.Enabled = false;
            m.Items.Add(header);

            miClock1 = new ToolStripMenuItem("", null, delegate { SetClock(1); });
            miClock2 = new ToolStripMenuItem("", null, delegate { SetClock(2); });
            miLocal = new ToolStripMenuItem("Местное время (как обычно)", null, delegate { SetClock(0); });
            m.Items.Add(miClock1);
            m.Items.Add(miClock2);
            m.Items.Add(miLocal);
            m.Items.Add(new ToolStripSeparator());

            miSeconds = new ToolStripMenuItem("Показывать секунды", null, delegate
            {
                showSeconds = !showSeconds;
                Settings.ShowSeconds = showSeconds;
                Tick();
            });
            miAutoStart = new ToolStripMenuItem("Запускать вместе с Windows", null, delegate
            {
                Settings.AutoStart = !Settings.AutoStart;
            });
            miHideIcon = new ToolStripMenuItem("Скрыть этот значок…", null, delegate { HideIcon(); });
            m.Items.Add(miSeconds);
            m.Items.Add(miAutoStart);
            m.Items.Add(miHideIcon);
            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem("Настроить дополнительные часы Windows…", null, delegate
            {
                try { Process.Start("control.exe", "timedate.cpl,,1"); } catch { }
            }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Выход", null, delegate { ExitApp(); }));

            m.Opening += delegate
            {
                ReloadClocks();
                FillClockItem(miClock1, clocks[1]);
                FillClockItem(miClock2, clocks[2]);
                miLocal.Checked = selected == 0;
                miSeconds.Checked = showSeconds;
                miAutoStart.Checked = Settings.AutoStart;
            };
            return m;
        }

        private void FillClockItem(ToolStripMenuItem item, AdditionalClock c)
        {
            if (c.Zone == null)
            {
                item.Text = "Часы " + c.Index + ": не настроены";
                item.Enabled = false;
            }
            else
            {
                item.Text = "Часы " + c.Index + ": " + c.Name + "  (" + c.OffsetText() + ")" +
                            (c.Enabled ? "" : " — выключены в Windows");
                item.Enabled = true;
            }
            item.Checked = selected == c.Index;
        }

        private void OnIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // Показать то же меню и по левому щелчку.
            MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(icon, null);
        }

        private void SetClock(int value)
        {
            selected = value;
            Settings.Clock = value;
            UpdateTrayText();
            Tick();
        }

        private void UpdateTrayText()
        {
            if (icon == null) return;
            string text;
            AdditionalClock c = (selected == 1 || selected == 2) ? clocks[selected] : null;
            if (c != null && c.Zone != null) text = "На панели: " + c.Name + " (" + c.OffsetText() + ")";
            else if (c != null) text = "Часы " + selected + " не настроены — показано местное время";
            else text = "На панели: местное время";
            if (text.Length > 63) text = text.Substring(0, 63);
            if (icon.Text != text) icon.Text = text;
        }

        private void HideIcon()
        {
            DialogResult r = MessageBox.Show(
                "Значок будет скрыт, а программа продолжит работать.\n\n" +
                "Чтобы вернуть значок, просто запустите TaskbarClockSwap.exe ещё раз.",
                Program.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r != DialogResult.OK) return;
            Settings.HideTrayIcon = true;
            icon.Visible = false;
        }

        /// <summary>Вызывается, когда программу запустили повторно.</summary>
        internal void OnActivateRequest(int requestedClock)
        {
            if (requestedClock >= 0 && requestedClock <= 2) SetClock(requestedClock);
            if (!icon.Visible)
            {
                Settings.HideTrayIcon = false;
                icon.Visible = true;
            }
            icon.ShowBalloonTip(4000, Program.AppName, "Программа уже запущена. Настройки — в меню этого значка.", ToolTipIcon.Info);
        }

        private void ExitApp()
        {
            exiting = true;
            timer.Stop();
            if (winEventHook != IntPtr.Zero) Native.UnhookWinEvent(winEventHook);
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            foreach (Overlay o in overlays.Values) o.Dispose();
            overlays.Clear();
            messageWindow.DestroyHandle();
            icon.Visible = false;
            icon.Dispose();
            ExitThread();
        }

        private static Icon MakeTrayIcon()
        {
            Size s = SystemInformation.SmallIconSize;
            int n = Math.Max(16, s.Width);
            using (Bitmap bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float pad = n / 16f;
                    RectangleF rc = new RectangleF(pad, pad, n - 2 * pad - 1, n - 2 * pad - 1);
                    using (Brush face = new SolidBrush(Color.FromArgb(255, 0, 120, 215)))
                        g.FillEllipse(face, rc);
                    using (Pen rim = new Pen(Color.White, Math.Max(1f, n / 16f)))
                        g.DrawEllipse(rim, rc);
                    PointF c = new PointF(rc.X + rc.Width / 2f, rc.Y + rc.Height / 2f);
                    using (Pen hand = new Pen(Color.White, Math.Max(1.5f, n / 10f)))
                    {
                        hand.StartCap = LineCap.Round; hand.EndCap = LineCap.Round;
                        g.DrawLine(hand, c, new PointF(c.X, rc.Y + rc.Height * 0.22f));
                        g.DrawLine(hand, c, new PointF(rc.X + rc.Width * 0.74f, c.Y));
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon result = (Icon)tmp.Clone();
                tmp.Dispose();
                Native.DestroyIcon(h);
                return result;
            }
        }
    }

    // ------------------------------------------------------------------ скрытое окно для связи с повторным запуском

    internal sealed class MessageWindow : NativeWindow
    {
        private readonly TrayApp app;

        public MessageWindow(TrayApp app)
        {
            this.app = app;
            CreateParams cp = new CreateParams();
            cp.Caption = "TaskbarClockSwap.MessageWindow";
            CreateHandle(cp); // невидимое окно верхнего уровня — получает широковещательные сообщения
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WM_ACTIVATE_REQUEST)
            {
                app.OnActivateRequest(m.WParam.ToInt32() - 1);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }

    // ------------------------------------------------------------------ окно, закрывающее системные часы

    internal sealed class Overlay : NativeWindow, IDisposable
    {
        private readonly bool excludedFromCapture;
        private bool visible;
        private bool dirty = true;
        private Rectangle lastRect;
        private string lastText;
        private Color lastBg = Color.Empty;
        private Bitmap capture;

        public Overlay()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "TaskbarClockSwap.Overlay";
            cp.Style = unchecked((int)Native.WS_POPUP);
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                         Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            CreateHandle(cp);

            // Windows 10 2004+: окно не попадает в снимки экрана. Благодаря этому мы можем «подсмотреть»
            // цвет настоящих часов под собой (в том числе подсветку при наведении мыши).
            try { excludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); }
            catch { excludedFromCapture = false; }
        }

        public void Invalidate() { dirty = true; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST) { m.Result = new IntPtr(Native.HTTRANSPARENT); return; }
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        public void Show(Rectangle r, string text, Font font)
        {
            Color bg = SampleBackground(r);
            if (dirty || r != lastRect || text != lastText || bg != lastBg)
            {
                Render(r, text, font, bg);
                lastRect = r; lastText = text; lastBg = bg; dirty = false;
            }
            if (!visible)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                visible = true;
            }
            // Держим окно над панелью задач.
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        public void Hide()
        {
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
        }

        // Цвет фона берём с экрана: у панели задач Windows 10 он зависит от темы, акцентного цвета и прозрачности.
        private Color SampleBackground(Rectangle r)
        {
            List<Point> pts = new List<Point>(4);
            if (excludedFromCapture || !visible)
            {
                // Углы самих системных часов (текст в них по центру).
                pts.Add(new Point(r.Left + 1, r.Top + 2));
                pts.Add(new Point(r.Left + 1, r.Bottom - 3));
                pts.Add(new Point(r.Right - 2, r.Top + 2));
                pts.Add(new Point(r.Right - 2, r.Bottom - 3));
            }
            else
            {
                // Старая Windows 10: наше окно видно в снимке экрана, поэтому смотрим на пиксели рядом с часами.
                pts.Add(new Point(r.Left - 1, r.Top + 1));
                pts.Add(new Point(r.Left - 1, r.Bottom - 2));
                pts.Add(new Point(r.Right, r.Top + 1));
                pts.Add(new Point(r.Right, r.Bottom - 2));
            }

            try
            {
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                foreach (Point p in pts)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (capture == null || capture.Width != w || capture.Height != h)
                {
                    if (capture != null) capture.Dispose();
                    capture = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                }
                using (Graphics g = Graphics.FromImage(capture))
                    g.CopyFromScreen(minX, minY, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

                int[] rs = new int[4], gs = new int[4], bs = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    Color c = capture.GetPixel(pts[i].X - minX, pts[i].Y - minY);
                    rs[i] = c.R; gs[i] = c.G; bs[i] = c.B;
                }
                // Медиана по каждому каналу: один «неудачный» пиксель не испортит цвет.
                return Color.FromArgb(255, Median4(rs), Median4(gs), Median4(bs));
            }
            catch
            {
                // Например, на экране блокировки или UAC — оставляем прежний цвет.
                return lastBg.IsEmpty ? Color.FromArgb(255, 16, 16, 16) : lastBg;
            }
        }

        private static int Median4(int[] v)
        {
            Array.Sort(v);
            return (v[1] + v[2]) / 2;
        }

        private void Render(Rectangle r, string text, Font font, Color bg)
        {
            int w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
            double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
            Color fg = lum > 140 ? Color.Black : Color.White;

            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (SolidBrush brush = new SolidBrush(fg))
                using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    g.Clear(bg); // фон непрозрачный, поэтому можно сглаживание ClearType, как у системы
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.None;
                    g.DrawString(text, font, brush, new RectangleF(0, 0, w, h + 1), sf);
                }

                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr old = Native.SelectObject(memDc, hBmp);
                try
                {
                    Native.SIZE size = new Native.SIZE(w, h);
                    Native.POINT src = new Native.POINT(0, 0);
                    Native.POINT dst = new Native.POINT(r.Left, r.Top);
                    Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                    blend.BlendOp = Native.AC_SRC_OVER;
                    blend.BlendFlags = 0;
                    blend.SourceConstantAlpha = 255;
                    blend.AlphaFormat = Native.AC_SRC_ALPHA;
                    Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                }
                finally
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBmp);
                    Native.DeleteDC(memDc);
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        public void Dispose()
        {
            if (capture != null) { capture.Dispose(); capture = null; }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ WinAPI

    internal static class Native
    {
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint WS_POPUP = 0x80000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;

        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int HTTRANSPARENT = -1;
        public const int MA_NOACTIVATE = 3;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;
        public const int ULW_ALPHA = 2;

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int CX, CY;
            public SIZE(int cx, int cy) { CX = cx; CY = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static string GetClassNameString(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }
}
