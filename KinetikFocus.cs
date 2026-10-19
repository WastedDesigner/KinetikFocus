// Kinetik Focus: a focus timer desktop widget in the style of Kinetik's widget and Kinetik Calendar.
// Work in timed sessions with breaks between (Pomodoro style) on a task picked from your Kinetik Notes folder, tick the
// task off in its Markdown file when it's done, and keep Kinetik's alerts quiet while you focus.
// Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Focus")]
[assembly: AssemblyProduct("Kinetik Focus")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

static class Program
{
    public const string Version = "1.1.0";
    // --demo: a made-up session, nothing read from or saved to this PC. "--theme Name" picks its theme.
    // "--shot file.png" draws the demo card into a PNG and quits (for the README screenshots).
    public static bool Demo;
    public static string DemoTheme, ShotPath;
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        Demo = args.Contains("--demo");
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) DemoTheme = args[ti + 1];
        int si = Array.IndexOf(args, "--shot");
        if (si >= 0 && si + 1 < args.Length) { ShotPath = Path.GetFullPath(args[si + 1]); Demo = true; }
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikFocusDemo" + (ShotPath != null ? Guid.NewGuid().ToString("N") : "") : @"Local\KinetikFocus", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkMenu();
            Backup.KeyName = "KinetikFocus"; Backup.AppTitle = "Kinetik Focus";
            if (!Demo) Backup.OfferRestore();
            var app = new FocusForm();
            using (new KinetikSuite.Shutdown("KinetikFocus" + (Demo ? "Demo" : ""), () => { app.Shutdown(); Application.Exit(); }))
                Application.Run(app);
        }
        if (!Demo) Backup.AfterExit();
    }
}

// ======================================================================= Settings
class Settings
{
    public const string KeyPath = @"Software\KinetikFocus";
    public int X = int.MinValue, Y = int.MinValue, Width = 300, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Layer = 0;
    public bool Locked, Snap = true, ClickThrough;
    public string ThemeName = "Midnight", FontName = "";
    public int FocusMin = 25, ShortMin = 5, LongMin = 15, LongEvery = 4;
    public bool AutoBreaks = true, AutoFocus, Sound = true, QuietAlerts = true, Hotkey = true;
    public string TasksFolder = "";            // "" = Kinetik Notes' folder
    public string TaskText = "", TaskFile = "", TaskRevision = "";
    public int TaskScanLimit = 100; // the task being worked on; TaskFile "" for one typed in
    public int TaskLine = -1;                  // where it was in the file, as a hint
    // the timer itself, so it carries on through a restart
    public int Phase = 0;                      // 0 focus, 1 short break, 2 long break
    public string RunningUntil = "";           // UTC end time while running
    public int Remaining = 0;                  // seconds left while paused; 0 = not started
    public int Cycle = 0;                      // focus sessions finished since the last long break

    static IEnumerable<FieldInfo> Fields
    {
        get
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }
    }

    public static bool Unreadable;

    public static Settings Load()
    {
        if (Program.Demo)
            return new Settings
            {
                Layer = 2, ThemeName = Program.DemoTheme ?? "Midnight", X = 240, Y = 160, TaskText = "Draft the project summary", TaskFile = "Work.md",
                Phase = 0, Remaining = 14 * 60 + 32, Cycle = 2, RunningUntil = ""
            };
        Settings s = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);
            bool ok;
            s = TryLoad(out ok);
            if (ok) { Unreadable = false; s.Opacity = CommonMenu.Migrate(s.Opacity); return s; }
        }
        Unreadable = true;
        MessageBox.Show("Kinetik Focus couldn't read its saved settings, so it's running on the defaults for now.\n\n" +
            "Your settings haven't been changed, and nothing will be saved over them while this is showing. " +
            "Restart Kinetik Focus to try again" + (Backup.Exists ? ", or use right-click → Backup → Restore settings…" : "."),
            "Kinetik Focus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return s;
    }

    static Settings TryLoad(out bool ok)
    {
        ok = true;
        var s = new Settings();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                if (k != null)
                    foreach (var f in Fields)
                    {
                        var v = k.GetValue(f.Name);
                        if (v == null) continue;
                        try
                        {
                            if (f.FieldType == typeof(string)) f.SetValue(s, v.ToString());
                            else if (f.FieldType == typeof(int)) f.SetValue(s, Convert.ToInt32(v));
                            else f.SetValue(s, Convert.ToInt32(v) != 0);
                        }
                        catch { ok = false; }
                    }
        }
        catch { ok = false; }
        KinetikSuite.SettingsValidation.Sanitize(s); return s;
    }

    public void Save()
    {
        if (Program.Demo || Unreadable) return;
        KinetikSuite.SettingsValidation.Sanitize(this);
        if (KinetikSuite.RegistryFiles.CannotSave) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var f in Fields)
                {
                    var v = f.GetValue(this);
                    if (f.FieldType == typeof(bool)) k.SetValue(f.Name, (bool)v ? 1 : 0);
                    else if (f.FieldType == typeof(int)) k.SetValue(f.Name, (int)v);
                    else k.SetValue(f.Name, (string)v ?? "");
                }
        }
        catch { }
    }
}

// ======================================================================= Tasks from a folder of Markdown notes (Kinetik Notes' folder)
class TaskItem { public string Text, File, Note, Revision; public int Line; }

static class Tasks
{
    static readonly Regex OpenRx = new Regex(@"^\s*[-*+]\s+\[ \]\s+(.*\S)\s*$");
    static readonly Regex AnyRx = new Regex(@"^(\s*[-*+]\s+\[)([ xX])(\].*)$");
    static readonly Regex HeadingRx = new Regex(@"^#\s+(.+)$");
    static readonly Regex TitleRx = new Regex(@"^title:\s*(.+)$");

    // Kinetik Notes' folder (and whether it includes subfolders), unless Focus has its own.
    public static string Folder(Settings c, out bool sub)
    {
        sub = false;
        if (c.TasksFolder != "") { sub = true; return c.TasksFolder; }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\KinetikNotes"))
                if (k != null)
                {
                    sub = Convert.ToInt32(k.GetValue("Subfolders", 0)) != 0;
                    return k.GetValue("Folder") as string ?? "";
                }
        }
        catch { }
        return "";
    }

    // The way a task reads on the widget: links and emphasis without their Markdown.
    public static string Clean(string s)
    {
        s = Regex.Replace(s, @"\[\[([^\]|]+)\|([^\]]+)\]\]", "$2");
        s = Regex.Replace(s, @"\[\[([^\]]+)\]\]", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"(\*\*|__|~~|`)", "");
        return s.Trim();
    }

    // Open tasks by note, most recently changed notes first.
    public static List<KeyValuePair<string, List<TaskItem>>> Load(Settings c)
    {
        var result = new List<KeyValuePair<string, List<TaskItem>>>();
        bool sub;
        var dir = Folder(c, out sub);
        if (dir == "" || !Directory.Exists(dir)) return result;
        IEnumerable<string> files;
        try { files = KinetikSuite.Markdown.Files(dir, sub).ToList(); }
        catch { return result; }
        foreach (var f in files.OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } }).Take(c.TaskScanLimit))
        {
            try
            {
                var doc = KinetikSuite.TextDocument.Read(f);
                var lines = doc.Text.Split('\n');
                char fence = '\0'; int fenceLength = 0;
                string title = null;
                var list = new List<TaskItem>();
                bool front = lines.Length > 0 && lines[0].Trim() == "---";
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (front) { if (i > 0 && l.Trim() == "---") { front = false; continue; } var tm = TitleRx.Match(l.Trim()); if (tm.Success) title = tm.Groups[1].Value.Trim().Trim('"', '\''); continue; }
                    if (KinetikSuite.Markdown.Fence(l, ref fence, ref fenceLength) || fence != '\0') continue;
                    Match m;
                    if (title == null && (m = HeadingRx.Match(l)).Success) title = Clean(m.Groups[1].Value);
                    if ((m = OpenRx.Match(l)).Success)
                    {
                        var t = Clean(m.Groups[1].Value);
                        if (t != "") list.Add(new TaskItem { Text = t, File = f, Line = i, Revision = doc.Revision });
                    }
                }
                if (list.Count == 0) continue;
                title = title ?? Path.GetFileNameWithoutExtension(f);
                foreach (var t in list) t.Note = title;
                result.Add(new KeyValuePair<string, List<TaskItem>>(title, list));
            }
            catch { }
        }
        return result;
    }

    // Ticks the task in its file: "- [ ] …" becomes "- [x] …" on the line (the remembered one, else the first open task
    // with the same text). Everything else in the file is left byte for byte. Returns false if it's no longer there.
    public static bool Tick(string file, int hint, string text, string revision)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
        if (string.IsNullOrEmpty(revision)) throw new IOException("Select this task again before editing it.");
        KinetikSuite.SafeEdit.Update(file, revision, doc =>
        {
            var lines = doc.Text.Split('\n');
            if (hint < 0 || hint >= lines.Length) throw new IOException("The task moved. Select it again.");
            var m = OpenRx.Match(lines[hint].TrimEnd('\r'));
            if (!m.Success || Clean(m.Groups[1].Value) != text) throw new IOException("The task changed. Select it again.");
            var match = AnyRx.Match(lines[hint]);
            lines[hint] = match.Groups[1].Value + "x" + match.Groups[3].Value;
            return string.Join("\n", lines);
        });
        return true;
    }

}

// ======================================================================= The log of focus sessions (a CSV anyone can open)
static class Log
{
    public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikFocus", "focus-log.csv"); } }

    static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }

    public static void Add(DateTime startLocal, int minutes, bool finished, string task, string note)
    {
        if (Program.Demo || minutes < 1) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "date,start,minutes,finished,task,note\r\n", new UTF8Encoding(true));
            File.AppendAllText(FilePath, startLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "," + startLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + "," +
                minutes + "," + (finished ? 1 : 0) + "," + Q(task) + "," + Q(note) + "\r\n");
        }
        catch { }
    }

    // Today's finished sessions and focused minutes.
    public static void Today(out int sessions, out int minutes)
    {
        sessions = minutes = 0;
        if (Program.Demo) { sessions = 3; minutes = 82; return; }
        try
        {
            if (!File.Exists(FilePath)) return;
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",";
            foreach (var l in File.ReadLines(FilePath))
            {
                if (!l.StartsWith(today)) continue;
                var p = l.Split(',');
                int m;
                if (p.Length > 3 && int.TryParse(p[2], out m)) { minutes += m; if (p[3] == "1") sessions++; }
            }
        }
        catch { }
    }
}

// A soft two-note chime, made in memory so the app needs no sound files.
static class Chime
{
    public static void Play(bool up)
    {
        try
        {
            var data = Make(up);
            ThreadPool.QueueUserWorkItem(_ => { try { using (var ms = new MemoryStream(data)) using (var p = new System.Media.SoundPlayer(ms)) p.PlaySync(); } catch { } });
        }
        catch { }
    }

    static byte[] Make(bool up)
    {
        const int rate = 44100;
        double[] notes = up ? new[] { 659.25, 880.0 } : new[] { 880.0, 659.25 };
        int each = (int)(rate * 0.42), n = each * notes.Length + rate / 4;
        var samples = new short[n];
        for (int k = 0; k < notes.Length; k++)
            for (int i = 0; i < each + rate / 4 && k * each + i < n; i++)
            {
                double t = i / (double)rate, env = Math.Exp(-t * 5.5) * Math.Min(1, t * 200);
                double v = Math.Sin(2 * Math.PI * notes[k] * t) * 0.8 + Math.Sin(4 * Math.PI * notes[k] * t) * 0.15;
                int idx = k * each + i;
                samples[idx] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, samples[idx] + v * env * 9000));
            }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }
    }
}

// ======================================================================= The widget
class FocusForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    readonly Settings c = Settings.Load();
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip()), taskMenu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    int layer, winW, winH, margin;
    float u = 1;
    bool dragging, moved;
    Point grab, downAt;
    ThemePicker picker;
    string hover;
    readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
    DateTime runStart; // local time the current focus run began, for the log

    static readonly string[] PhaseNames = { "Focus", "Short break", "Long break" };
    static readonly Color BreakCol = Color.FromArgb(108, 201, 124);

    public FocusForm()
    {
        layer = c.Layer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Kinetik Focus";
        menu.Opening += (s, e) => BuildMenu();
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
        tray.Text = Program.Demo ? "Kinetik Focus (demo)" : "Kinetik Focus";
        Backup.BeforeExit = () => { tray.Visible = false; Quiet(false); };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) StartPause(); };
        tray.Visible = Program.ShotPath == null;
        tick.Tick += (s, e) => OnTick();
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        if (Running) runStart = DateTime.Now.AddSeconds(-(Duration(c.Phase) - SecondsLeft));
    }

    void OnSession(object s, SessionSwitchEventArgs e)
    {
        try { BeginInvoke((Action)(() => { if (e.Reason == SessionSwitchReason.SessionUnlock) OnTick(); })); } catch { }
    }

    void OnPower(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) try { BeginInvoke((Action)OnTick); } catch { }
    }

    public void Shutdown() { c.Save(); Quiet(false); Close(); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (layer == 2) cp.ExStyle |= 0x8;          // TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        if (m.Msg == 0x312 && m.WParam.ToInt32() == 1) { StartPause(); return; } // WM_HOTKEY
        if (m.Msg == 0x46 && layer == 0)
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = new IntPtr(1); Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetHotkey();
        OnTick();
        if (Program.ShotPath != null) BeginInvoke((Action)(() => { try { surface.Save(Program.ShotPath); } catch { } tray.Visible = false; Application.Exit(); }));
    }

    void SetHotkey()
    {
        UnregisterHotKey(Handle, 1);
        if (c.Hotkey && !Program.Demo) RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4000, (uint)Keys.F); // Ctrl+Alt+F, no repeat
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false);
        if (value) BeginInvoke((Action)Render);
    }

    protected override void Dispose(bool disposing)
    {
        surface.Dispose();
        if (disposing)
        {
            if (IsHandleCreated) UnregisterHotKey(Handle, 1);
            SystemEvents.SessionSwitch -= OnSession; SystemEvents.PowerModeChanged -= OnPower;
            tick.Dispose(); tray.Dispose(); menu.Dispose(); taskMenu.Dispose(); DisposeFonts(); shadow.Dispose(); if (textLayer != null) textLayer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- the timer
    int Duration(int phase) { return 60 * (phase == 0 ? c.FocusMin : phase == 1 ? c.ShortMin : c.LongMin); }

    bool Running { get { return c.RunningUntil != ""; } }

    DateTime Until
    {
        get { DateTime d; return DateTime.TryParse(c.RunningUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.UtcNow; }
    }

    double SecondsLeft
    {
        get
        {
            if (Running) return Math.Max(0, (Until - DateTime.UtcNow).TotalSeconds);
            return c.Remaining > 0 ? c.Remaining : Duration(c.Phase);
        }
    }

    bool Started { get { return Running || c.Remaining > 0; } }

    void OnTick()
    {
        if (Running && SecondsLeft <= 0) { Complete(false); return; }
        Render();
        // every second while running (just after it turns over), otherwise once a minute
        if (Running) { var ms = (int)((SecondsLeft % 1) * 1000); tick.Interval = Math.Max(50, ms + 15); }
        else tick.Interval = 60000;
        tick.Start();
    }

    public void StartPause()
    {
        if (Running)
        {
            c.Remaining = Math.Max(1, (int)Math.Ceiling(SecondsLeft));
            c.RunningUntil = "";
            Quiet(false);
        }
        else
        {
            if (!Started && c.Phase == 0) runStart = DateTime.Now;
            else if (c.Phase == 0 && runStart == DateTime.MinValue) runStart = DateTime.Now.AddSeconds(-(Duration(0) - SecondsLeft));
            c.RunningUntil = DateTime.UtcNow.AddSeconds(SecondsLeft).ToString("o", CultureInfo.InvariantCulture);
            c.Remaining = 0;
            Quiet(c.Phase == 0);
        }
        c.Save();
        OnTick();
    }

    // Back to the start of this phase. A focus run that's thrown away still counts its minutes in the log.
    void Reset()
    {
        LogPartial();
        c.RunningUntil = ""; c.Remaining = 0;
        Quiet(false);
        c.Save();
        OnTick();
    }

    void Skip() { Complete(true); }

    void LogPartial()
    {
        if (c.Phase != 0 || !Started) return;
        int mins = (int)((Duration(0) - SecondsLeft) / 60);
        Log.Add(runStart == DateTime.MinValue ? DateTime.Now.AddMinutes(-mins) : runStart, mins, false, c.TaskText, NoteName);
        runStart = DateTime.MinValue;
    }

    // A phase ended (or was skipped): note it, chime, and move on to the next one.
    void Complete(bool skipped)
    {
        int was = c.Phase;
        if (was == 0)
        {
            if (skipped) LogPartial();
            else
            {
                Log.Add(runStart == DateTime.MinValue ? DateTime.Now.AddSeconds(-Duration(0)) : runStart, c.FocusMin, true, c.TaskText, NoteName);
                runStart = DateTime.MinValue;
                c.Cycle++;
            }
            c.Phase = c.Cycle >= c.LongEvery ? 2 : 1;
        }
        else
        {
            if (was == 2) c.Cycle = 0;
            c.Phase = 0;
        }
        c.Remaining = 0;
        bool auto = c.Phase == 0 ? c.AutoFocus : c.AutoBreaks;
        c.RunningUntil = auto ? DateTime.UtcNow.AddSeconds(Duration(c.Phase)).ToString("o", CultureInfo.InvariantCulture) : "";
        if (auto && c.Phase == 0) runStart = DateTime.Now;
        Quiet(auto && c.Phase == 0);
        c.Save();
        if (!skipped)
        {
            if (c.Sound) Chime.Play(c.Phase == 0);
            string title = was == 0 ? "Focus session done" : "Break's over";
            string body = was == 0
                ? (c.Phase == 2 ? "Time for a longer break: " + c.LongMin + " minutes." : "Take a " + c.ShortMin + "-minute break.") + (auto ? "" : " Click to start it.")
                : "Ready for the next " + c.FocusMin + " minutes?" + (c.TaskText != "" ? " " + c.TaskText : "") + (auto ? "" : " Click to start.");
            try { tray.ShowBalloonTip(6000, title, body, ToolTipIcon.None); } catch { }
            tray.BalloonTipClicked -= OnBalloon; tray.BalloonTipClicked += OnBalloon;
        }
        OnTick();
    }

    void OnBalloon(object s, EventArgs e) { tray.BalloonTipClicked -= OnBalloon; if (!Running) StartPause(); }

    // While a focus run is going, Kinetik (the taskbar bar) holds back its alerts: it reads this end time before each one.
    void Quiet(bool on)
    {
        if (Program.Demo) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Settings.KeyPath))
                if (on && c.QuietAlerts && Running) k.SetValue("QuietUntil", c.RunningUntil);
                else k.DeleteValue("QuietUntil", false);
        }
        catch { }
    }

    string NoteName
    {
        get { return c.TaskFile == "" ? "" : Path.GetFileNameWithoutExtension(c.TaskFile); }
    }

    // ---- the task
    void SetTask(TaskItem t)
    {
        c.TaskText = t == null ? "" : t.Text;
        c.TaskFile = t == null ? "" : t.File ?? "";
        c.TaskLine = t == null ? -1 : t.Line;
        c.TaskRevision = t == null ? "" : t.Revision ?? "";
        c.Save();
        Render();
    }

    void TickTask()
    {
        if (c.TaskText == "") return;
        if (c.TaskFile != "")
        {
            bool ok = false;
            try { ok = Tasks.Tick(c.TaskFile, c.TaskLine, c.TaskText, c.TaskRevision); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Kinetik Focus"); return; }
            if (!ok)
            {
                MessageBox.Show("Couldn't find \"" + c.TaskText + "\" as an open task in " + Path.GetFileName(c.TaskFile) + " any more, so nothing was changed.",
                    "Kinetik Focus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        try { tray.ShowBalloonTip(4000, "Done: " + c.TaskText, c.TaskFile != "" ? "Ticked off in " + Path.GetFileName(c.TaskFile) + "." : "Nice work.", ToolTipIcon.None); } catch { }
        SetTask(null);
        ShowTaskMenu(true);
    }

    bool taskLoading;
    void ShowTaskMenu(bool afterDone)
    {
        if (taskLoading) { taskMenu.Show(Cursor.Position); return; }
        if (Program.Demo)
        {
            PopulateTaskMenu(afterDone, "", new List<KeyValuePair<string, List<TaskItem>>>());
            taskMenu.Show(Cursor.Position); return;
        }
        taskLoading = true;
        foreach (var item in taskMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        taskMenu.Items.Clear();
        taskMenu.Items.Add(new ToolStripMenuItem("Loading tasks…") { Enabled = false });
        taskMenu.Show(Cursor.Position);
        var snapshot = new Settings { TasksFolder = c.TasksFolder, TaskScanLimit = c.TaskScanLimit };
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string dir = "";
            var notes = new List<KeyValuePair<string, List<TaskItem>>>();
            try { bool sub; dir = Tasks.Folder(snapshot, out sub); notes = Tasks.Load(snapshot); } catch { }
            KinetikSuite.Ui.Post(this, () =>
            {
                taskLoading = false;
                if (!taskMenu.IsDisposed && taskMenu.Visible) PopulateTaskMenu(afterDone, dir, notes);
            });
        });
    }

    void PopulateTaskMenu(bool afterDone, string dir, List<KeyValuePair<string, List<TaskItem>>> notes)
    {
        var old = taskMenu.Items.Cast<ToolStripItem>().ToList();
        taskMenu.Items.Clear();
        foreach (var it in old) it.Dispose();
        if (afterDone) taskMenu.Items.Add(new ToolStripMenuItem("What's next?") { Enabled = false });
        if (dir == "") taskMenu.Items.Add(new ToolStripMenuItem("No notes folder: set one in Kinetik Notes, or below") { Enabled = false });
        else if (notes.Count == 0) taskMenu.Items.Add(new ToolStripMenuItem("No open tasks in " + Path.GetFileName(dir.TrimEnd('\\'))) { Enabled = false });
        foreach (var n in notes)
        {
            var noteItem = new ToolStripMenuItem(n.Key + "   (" + n.Value.Count + ")");
            foreach (var t in n.Value.Take(30))
            {
                var task = t;
                var it = new ToolStripMenuItem(t.Text.Length > 70 ? t.Text.Substring(0, 68) + "…" : t.Text) { Checked = t.Text == c.TaskText && t.File == c.TaskFile && t.Line == c.TaskLine };
                it.Click += (s, e) => SetTask(task);
                noteItem.DropDownItems.Add(it);
            }
            taskMenu.Items.Add(noteItem);
        }
        taskMenu.Items.Add(new ToolStripSeparator());
        var typed = new ToolStripMenuItem("Type a task…");
        typed.Click += (s, e) => { var t = Prompt("Task", "What are you working on?", c.TaskFile == "" ? c.TaskText : ""); if (!string.IsNullOrWhiteSpace(t)) SetTask(new TaskItem { Text = t.Trim(), File = "" }); };
        taskMenu.Items.Add(typed);
        if (c.TaskText != "") { var none = new ToolStripMenuItem("No task"); none.Click += (s, e) => SetTask(null); taskMenu.Items.Add(none); }
        var folder = new ToolStripMenuItem("Tasks folder…");
        folder.Click += (s, e) => ChooseFolder();
        taskMenu.Items.Add(folder);
    }

    void ChooseFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "Pick the folder of Markdown notes to take tasks from. Cancel to use Kinetik Notes' folder.", ShowNewFolderButton = false })
        {
            bool sub;
            var cur = Tasks.Folder(c, out sub);
            if (cur != "" && Directory.Exists(cur)) dlg.SelectedPath = cur;
            c.TasksFolder = dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : "";
            c.Save();
        }
    }

    // ---- mouse: buttons on the card, drag to move, right-click for the menu
    string HitAt(Point client)
    {
        foreach (var h in hits) if (h.Key.Contains(client)) return h.Value;
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            c.X = p.X; c.Y = p.Y;
            SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10);
            Cursor = Cursors.SizeAll;
        }
        else if (!dragging)
        {
            var h = HitAt(e.Location);
            if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Render(); }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover != null) { hover = null; Render(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            bool wasMove = dragging && moved;
            if (wasMove) c.Save();
            dragging = false;
            Cursor = Cursors.Default;
            if (!wasMove)
                switch (HitAt(e.Location))
                {
                    case "play": StartPause(); break;
                    case "reset": Reset(); break;
                    case "skip": Skip(); break;
                    case "done": TickTask(); break;
                    case "task": ShowTaskMenu(false); break;
                }
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int gap = (int)(12 * u), reach = (int)(18 * u);
        int left = p.X + margin, top = p.Y + margin, right = p.X + winW - margin, bottom = p.Y + winH - margin;
        if (Math.Abs(left - (wa.Left + gap)) < reach) p.X = wa.Left + gap - margin;
        else if (Math.Abs(right - (wa.Right - gap)) < reach) p.X = wa.Right - gap - winW + margin;
        if (Math.Abs(top - (wa.Top + gap)) < reach) p.Y = wa.Top + gap - margin;
        else if (Math.Abs(bottom - (wa.Bottom - gap)) < reach) p.Y = wa.Bottom - gap - winH + margin;
        return p;
    }

    void EnsurePosition()
    {
        if (c.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(c.X, c.Y, winW, winH))))
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * u);
            c.X = wa.Right - gap - winW + margin;
            c.Y = wa.Bottom - gap - winH + margin;
        }
    }

    // ---- painting
    const float HeaderH = 22, BodyH = 104, FootH = 22;

    static string Clock(double secs)
    {
        int s = (int)Math.Ceiling(secs);
        return s >= 3600 ? (s / 3600) + ":" + (s % 3600 / 60).ToString("00") + ":" + (s % 60).ToString("00") : (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    public void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        u = DeviceDpi / 96f * c.Scale / 100f;
        margin = (int)(14 * u);
        float pad = 14 * u;
        int cardW = (int)(c.Width * u), cardH = (int)(pad * 2 + (HeaderH + BodyH + FootH) * u);
        winW = cardW + margin * 2; winH = cardH + margin * 2;

        bool wantThrough = c.Locked && c.ClickThrough;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, wantThrough ? ex | 0x20 : ex & ~0x20);

        EnsurePosition();
        var bmp = surface.Canvas(winW, winH);
        using (var g = Graphics.FromImage(bmp)) PaintCard(g, cardW, cardH, pad);
        surface.Push(Handle, c.X, c.Y);

        var left = Clock(SecondsLeft);
        var tip = (Running ? PhaseNames[c.Phase] + " · " + left + " left" : Started ? PhaseNames[c.Phase] + " · paused at " + left : "Kinetik Focus · ready") + (c.TaskText != "" ? " · " + c.TaskText : "");
        tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        if (Program.ShotPath != null) return;
        ShowWindow(Handle, 8);
        if (layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        else if (layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    Bitmap textLayer;
    readonly ShadowCache shadow = new ShadowCache();
    string Fam { get { return c.FontName != "" && Draw.HasFont(c.FontName) ? c.FontName : Theme.IsTerm(c.ThemeName) ? Theme.Mono : Draw.Family; } }
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    string fontKey;

    Font F(float size, FontStyle style = FontStyle.Regular)
    {
        var key = Fam + "|" + u + "|" + DeviceDpi;
        if (key != fontKey) { DisposeFonts(); fontKey = key; }
        var k = size + "/" + (int)style;
        Font f;
        if (!fonts.TryGetValue(k, out f)) fonts[k] = f = new Font(Fam, size * u / (DeviceDpi / 96f), style, GraphicsUnit.Point);
        return f;
    }

    void DisposeFonts() { KinetikSuite.Resources.DisposeValues(fonts); fontKey = null; }

    static readonly StringFormat fmtL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtWrap = new StringFormat { Trimming = StringTrimming.EllipsisWord };

    void PaintCard(Graphics target, int cardW, int cardH, float pad)
    {
        var th = Theme.Get(c.ThemeName);
        target.Clear(Color.Transparent);
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var card = new RectangleF(margin, margin, cardW, cardH);
        float rad = 16 * u;
        shadow.Paint(target, winW, winH, card + "|" + u + "|" + c.Opacity, sg =>
        {
            for (int i = 1; i <= 6; i++)
                using (var p = Draw.Round(RectangleF.Inflate(card, i * 1.6f * u, i * 1.6f * u), rad + i * 1.6f * u))
                using (var b = new SolidBrush(Color.FromArgb((int)(14 * c.Opacity / 100f / i), 0, 0, 0))) sg.FillPath(b, p);
        });
        int alpha = 255 * c.Opacity / 100;
        bool crisp = alpha >= 150;
        var g = target;
        if (crisp)
        {
            if (textLayer == null || textLayer.Width != winW || textLayer.Height != winH) { if (textLayer != null) textLayer.Dispose(); textLayer = new Bitmap(winW, winH, PixelFormat.Format32bppPArgb); }
            g = Graphics.FromImage(textLayer);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 2;
        }
        else g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        try { PaintContent(g, th, card, rad, crisp ? 255 : alpha, pad); }
        finally
        {
            if (crisp)
            {
                g.Dispose();
                if (alpha >= 255) target.DrawImageUnscaled(textLayer, 0, 0);
                else
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                        target.DrawImage(textLayer, new Rectangle(0, 0, textLayer.Width, textLayer.Height), 0, 0, textLayer.Width, textLayer.Height, GraphicsUnit.Pixel, ia);
                    }
            }
        }
    }

    void PaintContent(Graphics g, Theme th, RectangleF card, float rad, int alpha, float pad)
    {
        hits.Clear();
        using (var p = Draw.Round(card, rad))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, th.Bg))) g.FillPath(b, p);
            using (var pen = new Pen(th.Line, 1)) g.DrawPath(pen, p);
        }
        Color col = c.Phase == 0 ? th.Accent : BreakCol;
        float x = card.X + pad, y = card.Y + pad, w = card.Width - pad * 2;

        using (var text = new SolidBrush(th.Text))
        using (var sub = new SolidBrush(th.Sub))
        {
            // header: what this phase is, and the sessions done in this round
            float d = 8 * u;
            using (var b = new SolidBrush(col)) g.FillEllipse(b, x, y + (HeaderH * u - d) / 2, d, d);
            string state = Running ? "" : Started ? "  ·  paused" : "  ·  ready";
            g.DrawString(PhaseNames[c.Phase] + state, F(10, FontStyle.Bold), text, new RectangleF(x + d + 7 * u, y, w * 0.7f, HeaderH * u), fmtL);
            float dot = 7 * u, gapD = 5 * u, dx = x + w - c.LongEvery * (dot + gapD) + gapD;
            for (int i = 0; i < c.LongEvery; i++)
            {
                var r = new RectangleF(dx + i * (dot + gapD), y + (HeaderH * u - dot) / 2, dot, dot);
                if (i < c.Cycle) using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, r);
                else using (var pen = new Pen(Color.FromArgb(120, th.Sub), 1.2f * u)) g.DrawEllipse(pen, r);
            }
            y += HeaderH * u;

            // the ring and time left
            float ring = 90 * u, ry = y + (BodyH * u - ring) / 2, stroke = 6 * u;
            var rr = new RectangleF(x + stroke / 2, ry + stroke / 2, ring - stroke, ring - stroke);
            using (var pen = new Pen(Color.FromArgb(th.Light ? 26 : 30, th.Text), stroke)) g.DrawEllipse(pen, rr);
            double total = Duration(c.Phase), frac = Math.Max(0, Math.Min(1, SecondsLeft / total));
            if (frac > 0.001)
                using (var pen = new Pen(col, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rr, -90, (float)(360 * frac));
            g.DrawString(Clock(SecondsLeft), F(17, FontStyle.Bold), text, new RectangleF(x, ry, ring, ring - 4 * u), fmtC);
            g.DrawString(Running ? (DateTime.Now.AddSeconds(SecondsLeft).ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H") ? "H:mm" : "h:mm tt")) : Started ? "paused" : (Duration(c.Phase) / 60) + " min",
                F(7.5f), sub, new RectangleF(x, ry + ring * 0.6f, ring, 16 * u), fmtC);

            // the task, then the buttons
            float cx = x + ring + 16 * u, cw = x + w - cx;
            var taskRect = new RectangleF(cx - 6 * u, y + 2 * u, cw + 6 * u, 54 * u);
            if (hover == "task") using (var p = Draw.Round(taskRect, 8 * u)) using (var b = new SolidBrush(Color.FromArgb(th.Light ? 14 : 18, th.Text))) g.FillPath(b, p);
            hits.Add(new KeyValuePair<RectangleF, string>(taskRect, "task"));
            g.DrawString(c.TaskText != "" ? (c.TaskFile != "" ? NoteName.ToUpperInvariant() : "TASK") : "TASK", F(7, FontStyle.Bold), sub, new RectangleF(cx, y + 6 * u, cw, 14 * u), fmtL);
            if (c.TaskText != "")
                g.DrawString(c.TaskText, F(9.5f), text, new RectangleF(cx, y + 20 * u, cw, 34 * u), fmtWrap);
            else
                using (var b = new SolidBrush(Color.FromArgb(170, th.Sub))) g.DrawString("Pick a task…", F(9.5f), b, new RectangleF(cx, y + 20 * u, cw, 18 * u), fmtL);

            float bs = 30 * u, by = y + BodyH * u - bs - 8 * u, bx = cx;
            Button(g, th, new RectangleF(bx, by, bs, bs), "play", col, true);
            bx += bs + 8 * u;
            Button(g, th, new RectangleF(bx, by, bs, bs), "reset", col, false);
            bx += bs + 8 * u;
            Button(g, th, new RectangleF(bx, by, bs, bs), "skip", col, false);
            if (c.TaskText != "") Button(g, th, new RectangleF(x + w - bs, by, bs, bs), "done", BreakCol, false);
            y += BodyH * u;

            // footer: today so far
            using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, x, y, x + w, y);
            int sessions, minutes;
            Log.Today(out sessions, out minutes);
            string today = sessions == 0 && minutes == 0 ? "Nothing logged yet today" : "Today: " + sessions + (sessions == 1 ? " session" : " sessions") + "  ·  " + (minutes >= 60 ? minutes / 60 + " h " + minutes % 60 + " min" : minutes + " min") + " focused";
            g.DrawString(today, F(8.5f), sub, new RectangleF(x, y + 2 * u, w, FootH * u), fmtL);
        }
    }

    // A round button with a drawn symbol; filled in the phase colour for the main one.
    void Button(Graphics g, Theme th, RectangleF r, string id, Color col, bool primary)
    {
        hits.Add(new KeyValuePair<RectangleF, string>(r, id));
        bool hot = hover == id;
        Color fill = primary ? (hot ? Draw.Mix(col, Color.White, 0.15f) : col) : Color.FromArgb(hot ? (th.Light ? 34 : 40) : (th.Light ? 18 : 22), th.Text);
        if (id == "done") fill = hot ? Color.FromArgb(70, col) : Color.FromArgb(36, col);
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
        Color ink = primary ? (th.Light || col.GetBrightness() > 0.6f ? Color.FromArgb(20, 22, 30) : Color.White) : id == "done" ? col : th.Text;
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width;
        using (var b = new SolidBrush(ink))
        using (var pen = new Pen(ink, 1.8f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (id == "play" && Running)
            {
                g.FillRectangle(b, cx - s * 0.17f, cy - s * 0.19f, s * 0.11f, s * 0.38f);
                g.FillRectangle(b, cx + s * 0.06f, cy - s * 0.19f, s * 0.11f, s * 0.38f);
            }
            else if (id == "play")
                g.FillPolygon(b, new[] { new PointF(cx - s * 0.12f, cy - s * 0.2f), new PointF(cx + s * 0.2f, cy), new PointF(cx - s * 0.12f, cy + s * 0.2f) });
            else if (id == "reset")
            {
                var a = new RectangleF(cx - s * 0.2f, cy - s * 0.2f, s * 0.4f, s * 0.4f);
                g.DrawArc(pen, a, -60, 290);
                float ax = cx + (float)Math.Cos(-60 * Math.PI / 180) * s * 0.2f, ay = cy + (float)Math.Sin(-60 * Math.PI / 180) * s * 0.2f;
                g.FillPolygon(b, new[] { new PointF(ax + s * 0.1f, ay - s * 0.02f), new PointF(ax - s * 0.06f, ay - s * 0.1f), new PointF(ax - s * 0.02f, ay + s * 0.09f) });
            }
            else if (id == "skip")
            {
                g.FillPolygon(b, new[] { new PointF(cx - s * 0.18f, cy - s * 0.16f), new PointF(cx + s * 0.06f, cy), new PointF(cx - s * 0.18f, cy + s * 0.16f) });
                g.FillRectangle(b, cx + s * 0.08f, cy - s * 0.16f, s * 0.08f, s * 0.32f);
            }
            else if (id == "done")
                g.DrawLines(pen, new[] { new PointF(cx - s * 0.16f, cy + s * 0.01f), new PointF(cx - s * 0.04f, cy + s * 0.13f), new PointF(cx + s * 0.18f, cy - s * 0.12f) });
        }
    }

    // ---- menu
    ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    void Changed() { c.Save(); OnTick(); }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Focus", Version = Program.Version,
            OpenTheme = () =>
            {
                if (picker != null && !picker.IsDisposed) { picker.Activate(); return; }
                picker = new ThemePicker(c.ThemeName, n => { c.ThemeName = n; Changed(); });
                picker.Show();
            },
            Scale = () => c.Scale, SetScale = v => { c.Scale = v; Changed(); },
            Widths = new[] { 270, 300, 360 }, CardWidth = () => c.Width, SetCardWidth = v => { c.Width = v; Changed(); },
            FontName = () => c.FontName, SetFontName = v => { c.FontName = v; Changed(); },
            Opacity = () => c.Opacity, SetOpacity = v => { c.Opacity = v; Changed(); },
            Layer = () => c.Layer, SetLayer = SetLayer,
            Locked = () => c.Locked, SetLocked = v => { c.Locked = v; Changed(); },
            ClickThrough = () => c.ClickThrough, SetClickThrough = v => { c.ClickThrough = v; Changed(); },
            Snap = () => c.Snap, SetSnap = v => { c.Snap = v; Changed(); },
            RunAtStartup = () => Startup.IsOn, SetRunAtStartup = Startup.Set,
            BackUp = () => { c.Save(); Backup.SaveNow(); }, Restore = Backup.RestoreNow, LastBackup = () => Backup.SavedText,
            Exit = () => { tray.Visible = false; Quiet(false); Application.Exit(); },
        };
    }

    void BuildMenu()
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var it in old) it.Dispose();
        menu.Items.Add(Item(Running ? "Pause" : Started ? "Resume" : "Start " + PhaseNames[c.Phase].ToLowerInvariant(), false, StartPause));
        menu.Items.Add(Item("Reset", false, Reset));
        menu.Items.Add(Item("Skip to " + (c.Phase == 0 ? (c.Cycle + 1 >= c.LongEvery ? "long break" : "short break") : "focus"), false, Skip));
        menu.Items.Add(Item("Pick a task…", false, () => ShowTaskMenu(false)));
        if (c.TaskText != "") menu.Items.Add(Item("Mark \"" + (c.TaskText.Length > 40 ? c.TaskText.Substring(0, 38) + "…" : c.TaskText) + "\" done", false, TickTask));
        menu.Items.Add(new ToolStripSeparator());

        var times = new ToolStripMenuItem("Timer");
        var fm = new ToolStripMenuItem("Focus");
        foreach (var m in new[] { 15, 20, 25, 30, 45, 50, 60, 90 }) { int v = m; fm.DropDownItems.Add(Item(v + " minutes", c.FocusMin == v, () => { SetLength(0, v); })); }
        var sm = new ToolStripMenuItem("Short break");
        foreach (var m in new[] { 3, 5, 10 }) { int v = m; sm.DropDownItems.Add(Item(v + " minutes", c.ShortMin == v, () => { SetLength(1, v); })); }
        var lm = new ToolStripMenuItem("Long break");
        foreach (var m in new[] { 10, 15, 20, 30 }) { int v = m; lm.DropDownItems.Add(Item(v + " minutes", c.LongMin == v, () => { SetLength(2, v); })); }
        var le = new ToolStripMenuItem("Long break after");
        foreach (var n in new[] { 2, 3, 4, 5, 6 }) { int v = n; le.DropDownItems.Add(Item(v + " sessions", c.LongEvery == v, () => { c.LongEvery = v; c.Cycle = Math.Min(c.Cycle, v); Changed(); })); }
        times.DropDownItems.AddRange(new ToolStripItem[] { fm, sm, lm, le, new ToolStripSeparator() });
        times.DropDownItems.Add(Item("Start breaks by themselves", c.AutoBreaks, () => { c.AutoBreaks = !c.AutoBreaks; Changed(); }));
        times.DropDownItems.Add(Item("Start focus by itself after a break", c.AutoFocus, () => { c.AutoFocus = !c.AutoFocus; Changed(); }));
        times.DropDownItems.Add(Item("Chime when a session ends", c.Sound, () => { c.Sound = !c.Sound; Changed(); }));
        times.DropDownItems.Add(Item("Hold back Kinetik's alerts while focusing", c.QuietAlerts, () => { c.QuietAlerts = !c.QuietAlerts; Quiet(c.Phase == 0 && Running); Changed(); }));
        times.DropDownItems.Add(Item("Ctrl+Alt+F starts and pauses", c.Hotkey, () => { c.Hotkey = !c.Hotkey; SetHotkey(); Changed(); }));
        times.DropDownItems.Add(new ToolStripSeparator());
        times.DropDownItems.Add(Item("Start a new round (clear the dots)", false, () => { c.Cycle = 0; Changed(); }));
        times.DropDownItems.Add(Item("Open the focus log", false, () => { try { if (File.Exists(Log.FilePath)) System.Diagnostics.Process.Start(Log.FilePath); else MessageBox.Show("Nothing logged yet. Sessions are logged to\n" + Log.FilePath, "Kinetik Focus"); } catch { } }));
        var scan = new ToolStripMenuItem("Task scan limit");
        foreach (var n in new[] { 100, 1000, 10000 }) { int limit = n; scan.DropDownItems.Add(Item(limit + " notes", c.TaskScanLimit == limit, () => { c.TaskScanLimit = limit; Changed(); })); }
        times.DropDownItems.Add(scan);
        menu.Items.Add(times);

        Common().AddAll(menu.Items);
    }

    // A new length takes effect at once if that phase hasn't started yet.
    void SetLength(int phase, int minutes)
    {
        if (phase == 0) c.FocusMin = minutes; else if (phase == 1) c.ShortMin = minutes; else c.LongMin = minutes;
        Changed();
    }

    void SetLayer(int l)
    {
        c.Layer = layer = l; c.Save();
        RecreateHandle();
        SetHotkey();
        Render();
    }

    static string Prompt(string title, string label, string value)
    {
        using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(380, 110), BackColor = Color.FromArgb(27, 31, 44), ForeColor = Color.FromArgb(232, 236, 244), Font = new Font("Segoe UI", 9.5f), TopMost = true })
        {
            var lb = new Label { Text = label, Left = 12, Top = 12, AutoSize = true };
            var tb = new TextBox { Text = value, Left = 12, Top = 36, Width = 356, BackColor = Color.FromArgb(40, 47, 66), ForeColor = f.ForeColor, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 212, Top = 72, Width = 75, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 293, Top = 72, Width = 75, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { lb, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
        }
    }
}

// ======================================================================= Shared with the other Kinetik apps
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Name { get { return Backup.AppTitle; } }
    public static string Args = ""; // added after the exe path, e.g. " --startup"

    public static bool IsOn
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"" + Args);
                else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

// ======================================================================= Themes (same set as Kinetik and Kinetik Calendar)
class Theme
{
    public readonly string Name;
    public readonly Color Bg, Text, Sub, Accent;
    public readonly bool Light;

    public Theme(string name, Color bg, Color text, Color sub, Color accent, bool light)
    {
        Name = name; Bg = bg; Text = text; Sub = sub; Accent = accent; Light = light;
    }

    public Color Line { get { return Term ? Color.FromArgb(110, Sub) : Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    // The terminal themes (the same green, amber and yellow as Kinetik's dashboard and Kinetik Screen) draw in a
    // monospace font unless a font is picked, and their divider lines glow in the theme's colour.
    public bool Term { get { return IsTerm(Name); } }
    public static bool IsTerm(string name) { return name == "Terminal" || name == "Amber terminal" || name == "Yellow terminal"; }
    static string mono;
    public static string Mono
    {
        get
        {
            if (mono == null)
            {
                mono = "Consolas";
                try { using (var f = new Font("Cascadia Mono", 10f)) if (f.Name == "Cascadia Mono") mono = "Cascadia Mono"; } catch { }
            }
            return mono;
        }
    }

    public static readonly Theme[] All =
    {
        new Theme("Midnight", C(22, 22, 26), C(242, 242, 246), C(168, 168, 180), C(240, 88, 88), false),
        new Theme("Navy", C(22, 27, 40), C(236, 240, 248), C(138, 150, 176), C(77, 166, 255), false),
        new Theme("Graphite", C(40, 40, 44), C(236, 236, 236), C(160, 160, 166), C(255, 159, 10), false),
        new Theme("Nord", C(46, 52, 64), C(236, 239, 244), C(152, 162, 182), C(136, 192, 208), false),
        new Theme("Dracula", C(40, 42, 54), C(248, 248, 242), C(150, 156, 192), C(255, 121, 198), false),
        new Theme("Solarized", C(0, 43, 54), C(238, 232, 213), C(131, 148, 150), C(203, 75, 22), false),
        new Theme("Forest", C(22, 33, 27), C(228, 240, 231), C(138, 166, 148), C(108, 201, 124), false),
        new Theme("Ocean", C(12, 29, 45), C(228, 242, 250), C(128, 160, 186), C(56, 198, 232), false),
        new Theme("Sunset", C(40, 23, 35), C(250, 236, 240), C(192, 150, 172), C(255, 138, 92), false),
        new Theme("Mono", C(10, 10, 10), C(245, 245, 245), C(140, 140, 140), C(245, 245, 245), false),
        new Theme("OLED black", C(0, 0, 0), C(242, 242, 245), C(136, 136, 146), C(76, 194, 255), false),
        new Theme("Mocha", C(30, 30, 46), C(205, 214, 244), C(147, 153, 178), C(203, 166, 247), false),
        new Theme("Terminal", C(3, 10, 5), C(90, 255, 120), C(46, 170, 80), C(205, 255, 215), false),
        new Theme("Amber terminal", C(12, 7, 0), C(255, 182, 40), C(185, 118, 22), C(255, 232, 180), false),
        new Theme("Yellow terminal", C(0, 0, 0), C(255, 226, 0), C(200, 178, 0), C(255, 255, 190), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        // "App" follows the theme and accent colour every Kinetik app shares.
        if (name == "App") { var l = KinetikSuite.Look.Current; return new Theme("App", l.Card, l.Text, l.Sub, l.Accent, l.Light); }
        if (name == "Auto")
        {
            bool light = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { }
            return Get(light ? "Light" : "Midnight");
        }
        foreach (var t in All) if (t.Name == name) return t;
        return All[0];
    }
}

// ======================================================================= Drawing helpers
static class Draw
{
    public static string Family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly string[] FontChoices = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    public static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// A premultiplied-alpha bitmap backed by a DIB, pushed to a layered window with UpdateLayeredWindow (as in Kinetik).
sealed class DibSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr memDC, dib, oldObj;
    Bitmap bmp;
    int w, h;

    public Bitmap Canvas(int width, int height)
    {
        if (dib != IntPtr.Zero && width == w && height == h) return bmp;
        Free();
        memDC = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bits;
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        oldObj = SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        w = width; h = height;
        return bmp;
    }

    public void Push(IntPtr hwnd, int x, int y)
    {
        var size = new SIZE { CX = w, CY = h };
        var src = new POINT();
        var pos = new POINT { X = x, Y = y };
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }

    void Free()
    {
        if (bmp != null) { bmp.Dispose(); bmp = null; }
        if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    // The last frame, and the same as a PNG (for --shot screenshots).
    public Bitmap Current { get { return bmp; } }
    public void Save(string path) { if (bmp != null) bmp.Save(path, ImageFormat.Png); }

    public void Dispose() { Free(); }
}

// A card's soft shadow: stacked, slightly larger translucent shapes. Those anti-aliased fills over the whole card cost
// more than the rest of a frame put together, so they're drawn once into a bitmap and copied while nothing about them changes.
sealed class ShadowCache : IDisposable
{
    Bitmap bmp; string key;

    // Lays the shadow into the (cleared) w × h target. look sums up everything draw depends on.
    public void Paint(Graphics target, int w, int h, string look, Action<Graphics> draw)
    {
        var k = w + "x" + h + "|" + look + "|" + (int)target.SmoothingMode + (int)target.PixelOffsetMode;
        if (k != key || bmp == null)
        {
            if (bmp == null || bmp.Width != w || bmp.Height != h) { if (bmp != null) bmp.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = target.SmoothingMode; g.PixelOffsetMode = target.PixelOffsetMode;
                draw(g);
            }
            key = k;
        }
        bmp.SetResolution(target.DpiX, target.DpiY); // so it's copied 1:1
        var mode = target.CompositingMode;
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImageUnscaled(bmp, 0, 0);
        target.CompositingMode = mode;
    }

    public void Dispose() { if (bmp != null) bmp.Dispose(); bmp = null; key = null; }
}

class DarkColors : ProfessionalColorTable
{
    static Color Bg { get { return KinetikSuite.Look.Current.Card; } }
    static Color Hi { get { return KinetikSuite.Look.Current.Hover; } }
    static Color Edge { get { return KinetikSuite.Look.Current.Border; } }
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemPressedGradientBegin { get { return Hi; } }
    public override Color MenuItemPressedGradientEnd { get { return Hi; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Edge; } }
    public override Color CheckBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckSelectedBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckPressedBackground { get { return KinetikSuite.Look.Current.Hover; } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? KinetikSuite.Look.Current.Text : KinetikSuite.Look.Current.Sub;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = KinetikSuite.Look.Current.Sub;
        base.OnRenderArrow(e);
    }
}

// A grid of theme tiles, the same picker as the other Kinetik apps.
class ThemePicker : Form
{
    public static Color Page { get { return KinetikSuite.Look.Current.Bg; } }
    public static Color Card { get { return KinetikSuite.Look.Current.Card; } }
    public static Color CardHi { get { return KinetikSuite.Look.Current.Hover; } }
    public static Color Accent { get { return KinetikSuite.Look.Current.Accent; } }
    public static Color TextCol { get { return KinetikSuite.Look.Current.Text; } }
    public static Color SubCol { get { return KinetikSuite.Look.Current.Sub; } }

    public ThemePicker(string current, Action<string> chosen)
    {

        HandleCreated += (s, e) => KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
        Text = Backup.AppTitle + " theme";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Page;
        float k = DeviceDpi / 96f;
        var flow = new FlowLayoutPanel { Left = (int)(16 * k), Top = (int)(16 * k), Width = (int)(3 * 118 * k), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size((int)(3 * 118 * k), 0), WrapContents = true, BackColor = Page };
        foreach (var name in new[] { "Auto", "App" }.Concat(Theme.All.Select(t => t.Name)))
        {
            var tile = new ThemeTile(name, name == current, k);
            tile.Click += (s, e) =>
            {
                foreach (ThemeTile o in flow.Controls) o.SetSelected(o == tile);
                chosen(tile.ThemeName);
            };
            flow.Controls.Add(tile);
        }
        Controls.Add(flow);
        ClientSize = new Size(flow.Right + (int)(8 * k), flow.Bottom + (int)(8 * k));
    }
}

class ThemeTile : Control
{
    public readonly string ThemeName;
    bool selected, hot;
    float sel;
    readonly float k;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };
    static readonly Color[] Bars = { Color.FromArgb(77, 166, 255), Color.FromArgb(56, 198, 180), Color.FromArgb(240, 88, 88) };

    public ThemeTile(string name, bool isSelected, float scale)
    {
        ThemeName = name; selected = isSelected; sel = isSelected ? 1 : 0; k = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(I(110), I(84));
        Margin = new Padding(0, 0, I(8), I(8));
        Cursor = Cursors.Hand;
        BackColor = ThemePicker.Page;
        t.Tick += (s, e) =>
        {
            float target = selected ? 1 : 0;
            sel += (target - sel) * 0.3f;
            if (Math.Abs(target - sel) < 0.02f) { sel = target; t.Stop(); }
            Invalidate();
        };
    }

    int I(float v) { return (int)Math.Round(v * k); }
    public void SetSelected(bool v) { if (selected == v) return; selected = v; t.Start(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    static void Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = Draw.Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        Fill(g, outer, I(10), hot ? ThemePicker.CardHi : ThemePicker.Card);
        if (sel > 0) using (var p = Draw.Round(outer, I(10))) using (var pen = new Pen(Color.FromArgb((int)(255 * sel), ThemePicker.Accent), 2)) g.DrawPath(pen, p);
        var inner = new RectangleF(I(8), I(8), Width - I(16), Height - I(34));
        if (ThemeName == "Auto")
        {
            Mini(g, inner, Theme.Get("Midnight"));
            var st = g.Save();
            g.SetClip(new RectangleF(inner.X + inner.Width / 2, inner.Y, inner.Width, inner.Height));
            Mini(g, inner, Theme.Get("Light"));
            g.Restore(st);
        }
        else Mini(g, inner, Theme.Get(ThemeName));
        using (var f = new Font(Draw.Family, 9f))
            TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName == "App" ? "Match app theme" : ThemeName, f, new Rectangle(0, Height - I(26), Width, I(20)), selected ? ThemePicker.TextCol : ThemePicker.SubCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void Mini(Graphics g, RectangleF r, Theme th)
    {
        Fill(g, r, I(6), th.Bg);
        float x = r.X + I(7), w = r.Width - I(14), y = r.Y + I(7);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, x, y, I(7), I(7));
        Fill(g, new RectangleF(x + I(11), y + I(1), w * 0.45f, I(4)), I(2), th.Text);
        for (int i = 0; i < 3; i++)
        {
            float yy = y + I(13) + i * I(9);
            Fill(g, new RectangleF(x, yy, I(3), I(6)), 1.5f * k, Bars[i]);
            Fill(g, new RectangleF(x + I(7), yy + I(1), w * (0.62f - i * 0.12f), I(4)), I(2), Color.FromArgb(150, th.Sub));
            Fill(g, new RectangleF(x + w - I(14), yy + I(1), I(14), I(4)), I(2), Color.FromArgb(200, th.Text));
        }
    }
}

// ======================================================================= Settings backup
// Copies the app's settings (HKCU\Software\<KeyName>) to "<KeyName>-settings.reg" next to the exe, so they survive a
// Windows reinstall when the app folder is kept (on OneDrive, a USB stick…). It's a normal .reg export, so
// double-clicking the file restores the settings as well. A restore runs after the app has closed, so nothing the app
// saves on the way out can overwrite it, and then the app starts again with the restored settings.
// Same code in every Kinetik app.
static class Backup
{
    public static string KeyName, AppTitle;
    public static Action BeforeExit; // e.g. hide the tray icon, as the app's own Exit does
    static bool restoreAfterExit;

    public static string FilePath
    {
        get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), KeyName + "-settings.reg"); }
    }
    public static bool Exists { get { return System.IO.File.Exists(FilePath); } }
    public static string SavedText
    {
        get { return Exists ? System.IO.File.GetLastWriteTime(FilePath).ToString("d MMM yyyy, h:mm tt") : "never"; }
    }

    static bool HasSettings
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + KeyName)) return k != null; }
            catch { return true; }
        }
    }

    static bool Reg(string args) { return KinetikSuite.RegistryFiles.Run(args); }

    static bool FileIsOurs()
    {
        try
        {
            string own = "[HKEY_CURRENT_USER\\Software\\" + KeyName;
            bool any = false;
            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var l = line.Trim();
                if (!l.StartsWith("[")) continue;
                if (!l.StartsWith(own + "]", StringComparison.OrdinalIgnoreCase) && !l.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool Import()
    {
        try { return FileIsOurs() && KinetikSuite.RegistryFiles.Replace(FilePath, new[] { KeyName }); }
        catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); return false; }
    }

    static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, icon); }

    // At launch: no settings in the registry (a fresh Windows) but a backup beside the exe, so offer it.
    public static void OfferRestore()
    {
        if (HasSettings || !Exists) return;
        if (MessageBox.Show("There are saved " + AppTitle + " settings in this folder, from " + SavedText + ".\n\nRestore them?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
    }

    public static void SaveNow()
    {
        if (Reg("export \"HKCU\\Software\\" + KeyName + "\" \"" + FilePath + "\" /y"))
            Say("Settings backed up to\n" + FilePath + "\n\nKeep this file with the app. After reinstalling Windows, " + AppTitle + " offers to restore it the first time it starts.", MessageBoxIcon.Information);
        else
            Say("Couldn't save the backup to\n" + FilePath + "\n\nCheck that the folder isn't read-only.", MessageBoxIcon.Warning);
    }

    public static void RestoreNow()
    {
        if (!Exists) { Say("There's no backup yet. Use \"Back up settings\" first.\n\nIt would be saved as\n" + FilePath, MessageBoxIcon.Information); return; }
        if (!FileIsOurs()) { Say("The backup file isn't a " + AppTitle + " settings file, so it wasn't used.", MessageBoxIcon.Warning); return; }
        if (MessageBox.Show("Replace the current settings with the backup from " + SavedText + "?\n\n" + AppTitle + " will restart.",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        restoreAfterExit = true;
        if (BeforeExit != null) try { BeforeExit(); } catch { }
        Application.Exit();
    }

    // Called from Main once the app has closed and released its single-instance lock.
    public static void AfterExit()
    {
        if (!restoreAfterExit) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
    }
}

// ======================================================================= The right-click menu every Kinetik widget shares
// The look, placement, startup and backup options, in the same order and the same words in every Kinetik app, so they
// feel like one set. Each app adds its own items above. Same code in every Kinetik app.
#pragma warning disable 0649 // the hooks an app has no use for stay null
class CommonMenu
{
    public static readonly int[] Scales = { 80, 90, 100, 115, 125, 150 };
    public static readonly int[] Opacities = { 100, 95, 80, 65, 50, 30, 0 };
    public const int DefaultOpacity = 95;
    public static readonly string[] Fonts = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    // Settings saved before the shared default changed from 92% to 95% move with it; any other choice is kept.
    public static int Migrate(int opacity) { return opacity == 92 ? DefaultOpacity : opacity; }

    public string Title, Version;
    public Action OpenTheme;                                        // opens the theme picker (or the page with it)
    public Action LookChanged;                                      // optional: after the App theme changes
    public Func<int> Scale; public Action<int> SetScale;
    public int[] Widths; public Func<int> CardWidth; public Action<int> SetCardWidth; // optional: Narrow, Normal, Wide
    public Func<string> FontName; public Action<string> SetFontName;   // optional; "" = the default font
    public Func<int> Opacity; public Action<int> SetOpacity;
    public Func<int> Layer; public Action<int> SetLayer;
    public Func<bool> Locked; public Action<bool> SetLocked;
    public Func<bool> ClickThrough; public Action<bool> SetClickThrough; // optional
    public Func<bool> Snap; public Action<bool> SetSnap;
    public string LockText = "Lock position";
    public Action OpenSettings;                                     // optional: the app's Settings window
    public Func<bool> RunAtStartup; public Action<bool> SetRunAtStartup;   // optional
    public Action BackUp, Restore; public Func<string> LastBackup; // optional
    public Action Exit;                                         // optional

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    // Appearance and placement: Theme, Size, Font, Background opacity, Placement.
    public void AddLook(ToolStripItemCollection items)
    {
        if (OpenTheme != null) items.Add(Item("Widget theme…", false, OpenTheme));
        items.Add(KinetikSuite.Look.MenuItem(LookChanged)); // the theme of every Kinetik app's windows and menus

        var size = new ToolStripMenuItem("Size");
        foreach (var s in Scales) { int v = s; size.DropDownItems.Add(Item(v + "%", Scale() == v, () => SetScale(v))); }
        if (Widths != null)
        {
            size.DropDownItems.Add(new ToolStripSeparator());
            string[] names = { "Narrow", "Normal", "Wide" };
            for (int i = 0; i < Widths.Length && i < 3; i++) { int v = Widths[i]; size.DropDownItems.Add(Item(names[i], CardWidth() == v, () => SetCardWidth(v))); }
        }
        items.Add(size);

        if (FontName != null)
        {
            var font = new ToolStripMenuItem("Font");
            font.DropDownItems.Add(Item("Default", FontName() == "", () => SetFontName("")));
            foreach (var n in Fonts.Where(HasFont)) { string v = n; font.DropDownItems.Add(Item(v, FontName() == v, () => SetFontName(v))); }
            if (FontName() != "" && !Fonts.Contains(FontName())) font.DropDownItems.Add(Item(FontName(), true, () => { }));
            items.Add(font);
        }

        var opacity = new ToolStripMenuItem("Background opacity");
        foreach (var o in Opacities) { int v = o; opacity.DropDownItems.Add(Item(v + "%", Opacity() == v, () => SetOpacity(v))); }
        if (!Opacities.Contains(Opacity())) opacity.DropDownItems.Add(Item(Opacity() + "%", true, () => { }));
        items.Add(opacity);

        var place = new ToolStripMenuItem("Placement");
        string[] layers = { "On the desktop", "Normal window", "Always on top" };
        for (int i = 0; i < 3; i++) { int v = i; place.DropDownItems.Add(Item(layers[i], Layer() == v, () => SetLayer(v))); }
        place.DropDownItems.Add(new ToolStripSeparator());
        place.DropDownItems.Add(Item(LockText, Locked(), () => SetLocked(!Locked())));
        if (ClickThrough != null) place.DropDownItems.Add(Item("Click-through when locked", ClickThrough(), () => SetClickThrough(!ClickThrough())));
        place.DropDownItems.Add(Item("Snap to screen edges", Snap(), () => SetSnap(!Snap())));
        items.Add(place);
    }

    // The rest: Settings…, Run at startup, Backup, then the version and Exit.
    public void AddEnd(ToolStripItemCollection items)
    {
        items.Add(new ToolStripSeparator());
        if (OpenSettings != null) items.Add(Item("Settings…", false, OpenSettings));
        if (RunAtStartup != null) items.Add(Item("Run at startup", RunAtStartup(), () => SetRunAtStartup(!RunAtStartup())));
        if (BackUp != null)
        {
            var backup = new ToolStripMenuItem("Backup");
            backup.DropDownItems.Add(Item("Back up settings", false, BackUp));
            backup.DropDownItems.Add(Item("Restore settings…", false, Restore));
            backup.DropDownItems.Add(new ToolStripMenuItem("Last backup: " + LastBackup()) { Enabled = false });
            items.Add(backup);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Title + " " + Version) { Enabled = false });
        if (Exit != null) items.Add(Item("Exit", false, Exit));
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
