using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Rehad.Core;

namespace Rehad;

public partial class App : Application
{
    public static DownloadManager Manager { get; private set; } = null!;
    public static ObservableCollection<DownloadItem> Items { get; } = new();
    public static Settings Settings => Manager.Settings;
    public static MainWindow? MainWin { get; private set; }

    readonly PipeServer pipe = new();
    System.Windows.Forms.NotifyIcon? tray;
    DispatcherTimer? timer;
    uint clipSeq;
    string lastClip = "";
    static readonly Dictionary<int, ProgressWindow> progressWindows = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        bool background = args.Any(a => a.Equals("/background", StringComparison.OrdinalIgnoreCase));
        var url = args.FirstOrDefault(a => a.Contains("://"));

        // single instance: hand our request to the running copy
        var forward = url != null ? JsonSerializer.Serialize(new { type = "download", url }) : background ? null : "{\"type\":\"show\"}";
        if (await PipeServer.SendAsync("{\"type\":\"ping\"}", 400) != null)
        {
            if (forward != null) await PipeServer.SendAsync(forward);
            Shutdown();
            return;
        }

        var settings = Store.Load<Settings>("settings.json");
        Loc.Init(settings.Language);
        Tools.AppDir = AppContext.BaseDirectory;
        Manager = new DownloadManager(settings, Items) { Ui = a => Dispatcher.BeginInvoke(a) };
        if (settings.SpeedLimitAlwaysOnStart) { settings.SpeedLimitOn = true; Manager.ApplySpeedLimit(); }
        Manager.Completed += OnCompleted;
        Manager.Failed += OnFailed;
        Manager.QueueFinished += OnQueueFinished;
        Manager.LimitExceeded += mb => MessageBox.Show(Loc.T("limit.exceeded", mb), Loc.T("app"), MessageBoxButton.OK, MessageBoxImage.Warning);
        ApplyStartup();
        Integration.Register();

        pipe.Message += OnPipeMessage;
        pipe.Start();
        SetupTray();

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { Manager.Tick(); CheckClipboard(); MainWin?.RefreshStatus(); };
        timer.Start();
        clipSeq = Native.GetClipboardSequenceNumber();

        MainWin = new MainWindow();
        if (!background) MainWin.Show();
        if (url != null) NewDownload(new Incoming { Url = url });
    }

    public static void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Rehad.exe");
            if (Settings.LaunchOnStartup) key.SetValue("Rehad", $"\"{exe}\" /background");
            else key.DeleteValue("Rehad", false);
        }
        catch { }
    }

    // ------------------------------------------------------------------ tray

    void SetupTray()
    {
        tray = new System.Windows.Forms.NotifyIcon
        {
            Text = Loc.T("app"),
            Visible = true,
        };
        try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => ShowMain());
        menu.Items.Add(Loc.T("m.add").Replace("_", ""), null, (_, _) => Dispatcher.Invoke(() => AddUrlWindow.Ask()));
        menu.Items.Add(Loc.T("m.stopall").Replace("_", ""), null, (_, _) => Manager.StopAll());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("m.exit").Replace("_", ""), null, (_, _) => Dispatcher.Invoke(ExitApp));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMain();
    }

    public static void ShowMain()
    {
        Current.Dispatcher.Invoke(() =>
        {
            MainWin ??= new MainWindow();
            MainWin.Show();
            if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
            MainWin.Activate();
            MainWin.Topmost = true; MainWin.Topmost = false;
        });
    }

    public static void ExitApp()
    {
        Manager.StopAll();
        Manager.Save();
        Manager.SaveSettings();
        if (Current is App app)
        {
            app.pipe.Stop();
            if (app.tray != null) { app.tray.Visible = false; app.tray.Dispose(); }
        }
        Current.Shutdown();
    }

    // ------------------------------------------------------------------ clipboard monitor

    void CheckClipboard()
    {
        if (!Settings.MonitorClipboard) return;
        var seq = Native.GetClipboardSequenceNumber();
        if (seq == clipSeq) return;
        clipSeq = seq;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Trim();
            if (text == lastClip || text.Length > 2048 || text.Contains('\n')) return;
            lastClip = text;
            var links = Links.Extract(text);
            if (links.Count != 1 || links[0] != text) return;
            var name = Net.FileNameFromUrl(text);
            if (!Settings.ShouldCapture(name) && !YtDlp.IsSitePage(text)) return;
            NewDownload(new Incoming { Url = text, FromClipboard = true });
        }
        catch { }
    }

    // ------------------------------------------------------------------ messages from the browser / second instance

    string OnPipeMessage(JsonElement m)
    {
        string type = Str(m, "type") ?? "";
        switch (type)
        {
            case "ping":
                return JsonSerializer.Serialize(new { ok = true, app = true, minVideoSize = Settings.MinVideoSizeKB * 1024, videoButton = Settings.ShowVideoButton, capture = Settings.CaptureFromBrowsers });
            case "show":
                ShowMain();
                return "{\"ok\":true}";
            case "download":
            case "stream":
            case "page":
            {
                var inc = new Incoming
                {
                    Url = Str(m, "url") ?? "",
                    Referer = Str(m, "referer"),
                    PageUrl = Str(m, "pageUrl"),
                    Cookies = Str(m, "cookies"),
                    UserAgent = Str(m, "userAgent"),
                    FileName = Str(m, "fileName"),
                    Title = Str(m, "title"),
                    Mime = Str(m, "mime"),
                    Size = m.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1,
                    Kind = type == "page" ? DownloadKind.Site : type == "stream" ? (Str(m, "kind") == "dash" ? DownloadKind.Site : DownloadKind.Hls) : (DownloadKind?)null,
                };
                if (inc.Url.Length == 0) return "{\"ok\":false}";
                bool capture = m.TryGetProperty("capture", out var c) && c.ValueKind == JsonValueKind.True;
                if (capture)
                {
                    var name = string.IsNullOrEmpty(inc.FileName) ? Net.FileNameFromUrl(inc.Url, inc.Mime) : inc.FileName;
                    bool take = Settings.CaptureFromBrowsers && Settings.ShouldCapture(name) && !Settings.IsExceptionSite(inc.Url)
                                && !Settings.IsExceptionSite(inc.Referer ?? "");
                    if (!take) return "{\"ok\":true,\"taken\":false}";
                }
                Dispatcher.InvokeAsync(() => NewDownload(inc));
                return "{\"ok\":true,\"taken\":true}";
            }
            case "links":
            {
                var urls = m.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.Array
                    ? u.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                var referer = Str(m, "referer");
                var cookies = Str(m, "cookies");
                Dispatcher.InvokeAsync(() => LinksWindow.Show(urls, referer, cookies));
                return "{\"ok\":true}";
            }
        }
        return "{\"ok\":false,\"error\":\"unknown\"}";
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------------ adding downloads

    public sealed class Incoming
    {
        public string Url = "";
        public string? Referer, PageUrl, Cookies, UserAgent, FileName, Title, Mime;
        public long Size = -1;
        public DownloadKind? Kind;
        public bool FromClipboard;
        public string? Login, Password;
    }

    public static DownloadItem MakeItem(Incoming inc)
    {
        var item = Manager.Create(inc.Url);
        if (inc.Kind != null) item.Kind = inc.Kind.Value;
        item.Referer = inc.Referer;
        item.PageUrl = inc.PageUrl ?? (item.Kind == DownloadKind.Site ? inc.Url : null);
        item.Cookies = inc.Cookies;
        item.UserAgent = inc.UserAgent;
        item.Login = inc.Login; item.Password = inc.Password;
        if (inc.Size > 0) item.Size = inc.Size;
        if (!string.IsNullOrWhiteSpace(inc.FileName)) item.FileName = Net.Sanitize(inc.FileName!);
        else if (item.Kind != DownloadKind.File && !string.IsNullOrWhiteSpace(inc.Title)) item.FileName = Net.Sanitize(inc.Title!) + ".mp4";
        else item.FileName = Net.FileNameFromUrl(inc.Url, inc.Mime);
        if (item.Kind != DownloadKind.File && !string.IsNullOrWhiteSpace(inc.Title)) item.Description = inc.Title!;
        Manager.Assign(item);
        return item;
    }

    /// <summary>Entry point for every new link: duplicate check, then the "Download File Info" dialog (or direct start).</summary>
    public static void NewDownload(Incoming inc)
    {
        var dup = Manager.FindDuplicate(inc.Url);
        if (dup != null && inc.Kind != DownloadKind.Site)
        {
            var action = Settings.Duplicates;
            if (action == DuplicateAction.Ask) action = DuplicateWindow.Ask(dup);
            if (action == DuplicateAction.Skip)
            {
                MainWin?.Select(dup);
                if (!dup.IsFinished) StartWithUi(dup);
                return;
            }
            if (action == (DuplicateAction)(-1)) return;
            var item0 = MakeItem(inc);
            if (action == DuplicateAction.Overwrite) item0.FileName = dup.FileName;
            Continue(item0, inc);
            return;
        }
        Continue(MakeItem(inc), inc);
    }

    static void Continue(DownloadItem item, Incoming inc)
    {
        if (item.Kind == DownloadKind.File && !Settings.ShowStartDialog)
        {
            Manager.Add(item);
            StartWithUi(item);
            return;
        }
        var w = new FileInfoWindow(item);
        w.Show();
        w.Activate();
    }

    public static void StartWithUi(DownloadItem item)
    {
        Manager.Start(item);
        if (Settings.ShowProgressDialog) ShowProgress(item);
    }

    public static void ShowProgress(DownloadItem item)
    {
        if (progressWindows.TryGetValue(item.Id, out var w) && w.IsLoaded) { w.Activate(); return; }
        w = new ProgressWindow(item);
        progressWindows[item.Id] = w;
        w.Closed += (_, _) => progressWindows.Remove(item.Id);
        w.Show();
    }

    static readonly HashSet<int> exitWhenDone = new(), shutdownWhenDone = new(), hideCompleteDialog = new();
    public static void SetCompletionOptions(DownloadItem item, bool showComplete, bool exit, bool shutdown)
    {
        if (!showComplete) hideCompleteDialog.Add(item.Id); else hideCompleteDialog.Remove(item.Id);
        if (exit) exitWhenDone.Add(item.Id); else exitWhenDone.Remove(item.Id);
        if (shutdown) shutdownWhenDone.Add(item.Id); else shutdownWhenDone.Remove(item.Id);
    }

    void OnCompleted(DownloadItem item)
    {
        if (progressWindows.TryGetValue(item.Id, out var w)) w.Close();
        if (Settings.ShowCompleteDialog && !hideCompleteDialog.Contains(item.Id)) new CompleteWindow(item).Show();
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        if (shutdownWhenDone.Remove(item.Id)) PowerOff();
        else if (exitWhenDone.Remove(item.Id)) ExitApp();
    }

    void OnFailed(DownloadItem item)
    {
        if (progressWindows.TryGetValue(item.Id, out var w)) w.ShowError(item.Error);
        else if (MainWin?.IsVisible == true) Main.FlashStatus($"{item.FileName}: {item.Error}");
    }

    void OnQueueFinished(QueueInfo q)
    {
        switch (q.OnComplete)
        {
            case CompletionAction.Exit: ExitApp(); break;
            case CompletionAction.Shutdown: PowerOff(); break;
            case CompletionAction.Sleep: System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); break;
        }
    }

    static void PowerOff()
    {
        Manager.Save();
        try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"Rehad: downloads finished\"") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();
}
