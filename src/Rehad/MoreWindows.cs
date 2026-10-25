using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rehad.Core;

namespace Rehad;

/// <summary>Site grabber: explore a site N levels deep and pick the files to download.</summary>
public sealed class GrabberWindow : Dlg
{
    CancellationTokenSource? cts;

    public GrabberWindow() : base(Loc.T("gr.title"), 700, true)
    {
        var start = Input("https://");
        var depth = new ComboBox { Width = 70 };
        for (int i = 0; i <= 5; i++) depth.Items.Add(i);
        depth.SelectedIndex = 1;
        var types = Input(new GrabberOptions().FileTypes);
        var same = Check(Loc.T("gr.same"), true);
        var progress = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 4) };
        var list = new ListBox { Height = 260 };
        Body.Children.Add(Form((Loc.T("gr.start"), start), (Loc.T("gr.depth"), depth), (Loc.T("gr.types"), types)));
        Body.Children.Add(same);
        Body.Children.Add(progress);
        Body.Children.Add(list);

        Button? explore = null;
        explore = Btn(Loc.T("gr.explore"), async () =>
        {
            if (cts != null) { cts.Cancel(); return; }
            if (!Uri.TryCreate(start.Text.Trim(), UriKind.Absolute, out _)) { start.Focus(); return; }
            list.Items.Clear();
            cts = new CancellationTokenSource();
            explore!.Content = Loc.T("gr.stop").Replace("_", "");
            var opt = new GrabberOptions { StartUrl = start.Text.Trim(), Depth = (int)depth.SelectedItem, FileTypes = types.Text, SameSiteOnly = same.IsChecked == true };
            var prog = new Progress<(int pages, int files)>(p => progress.Text = Loc.T("gr.progress", p.pages, p.files));
            try
            {
                var files = await Grabber.ExploreAsync(opt, App.Settings, prog, cts.Token);
                foreach (var f in files.Distinct()) list.Items.Add(new CheckBox { Content = f, IsChecked = true });
                progress.Text = Loc.T("gr.progress", "✓", files.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { progress.Text = ex.Message; }
            cts = null;
            explore.Content = Loc.T("gr.explore").Replace("_", "");
        }, isDefault: true);
        Btn(Loc.T("gr.addchecked"), () =>
        {
            var urls = list.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
            if (urls.Count == 0) return;
            LinksWindow.Show(urls, start.Text.Trim(), null);
            Close();
        });
        Btn(Loc.T("close"), Close, isCancel: true);
        Closed += (_, _) => cts?.Cancel();
    }
}

/// <summary>Scheduler: queues, start/stop times, days, files in queue, what to do when done.</summary>
public sealed class SchedulerWindow : Dlg
{
    readonly ListBox queues = new() { Width = 190, Height = 330 };
    readonly ContentControl editor = new() { Margin = new Thickness(12, 0, 0, 0) };

    public SchedulerWindow() : base(Loc.T("sch.title"), 820)
    {
        var grid = new DockPanel();
        var left = new StackPanel();
        left.Children.Add(Label(Loc.T("sch.queues"), 0, true));
        left.Children.Add(queues);
        var newQ = new Button { Content = Loc.T("sch.newq").Replace("_", ""), Margin = new Thickness(0, 6, 0, 0) };
        newQ.Click += (_, _) =>
        {
            var name = Prompt(this, Loc.T("sch.newq").Replace("_", ""), Loc.T("sch.qname"));
            if (name == null || App.Settings.Queues.Any(q => q.Name == name)) return;
            App.Settings.Queues.Add(new QueueInfo { Name = name });
            Fill(); queues.SelectedIndex = queues.Items.Count - 1;
        };
        var del = new Button { Content = Loc.T("delete"), Margin = new Thickness(0, 6, 0, 0) };
        del.Click += (_, _) =>
        {
            if (queues.SelectedIndex < 2) return; // the two built-in queues stay
            var q = App.Settings.Queues[queues.SelectedIndex];
            App.Manager.StopQueue(q);
            foreach (var it in App.Items.Where(i => i.Queue == q.Name)) it.Queue = null;
            App.Settings.Queues.Remove(q);
            Fill(); queues.SelectedIndex = 0;
        };
        left.Children.Add(newQ); left.Children.Add(del);
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(editor);
        Body.Children.Add(grid);
        queues.SelectionChanged += (_, _) => { if (queues.SelectedIndex >= 0) editor.Content = Editor(App.Settings.Queues[queues.SelectedIndex]); };
        Fill();
        queues.SelectedIndex = 0;

        Btn(Loc.T("sch.startnow"), () => { if (queues.SelectedIndex >= 0) App.Manager.StartQueue(App.Settings.Queues[queues.SelectedIndex]); });
        Btn(Loc.T("sch.stop"), () => { if (queues.SelectedIndex >= 0) App.Manager.StopQueue(App.Settings.Queues[queues.SelectedIndex]); });
        Btn(Loc.T("apply"), () => { App.Manager.SaveSettings(); App.Manager.Save(); });
        Btn(Loc.T("close"), () => { App.Manager.SaveSettings(); App.Manager.Save(); App.Main?.RebuildTree(); Close(); }, isCancel: true);
    }

    void Fill()
    {
        queues.Items.Clear();
        foreach (var q in App.Settings.Queues) queues.Items.Add(MainWindow.QueueName(q));
    }

    UIElement Editor(QueueInfo q)
    {
        var tabs = new TabControl { Height = 400 };

        // schedule
        var sp = new StackPanel { Margin = new Thickness(10) };
        var startOn = Check(Loc.T("sch.startat"), q.StartEnabled);
        var startTime = Input(q.StartAt.ToString(@"hh\:mm"), 70);
        var stopOn = Check(Loc.T("sch.stopat"), q.StopEnabled);
        var stopTime = Input(q.StopAt.ToString(@"hh\:mm"), 70);
        var daily = new RadioButton { Content = Loc.T("sch.daily"), IsChecked = q.Daily };
        var once = new RadioButton { Content = Loc.T("sch.once"), IsChecked = !q.Daily };
        var days = new WrapPanel { Margin = new Thickness(20, 2, 0, 6) };
        for (int d = 0; d < 7; d++)
        {
            int bit = d;
            var cb = new CheckBox { Content = Loc.T("day" + d), IsChecked = (q.Days & (1 << d)) != 0, Margin = new Thickness(0, 0, 10, 0) };
            cb.Click += (_, _) => q.Days = cb.IsChecked == true ? q.Days | (1 << bit) : q.Days & ~(1 << bit);
            days.Children.Add(cb);
        }
        var simul = new ComboBox { Width = 60 };
        for (int i = 1; i <= 10; i++) simul.Items.Add(i);
        simul.SelectedItem = Math.Clamp(q.Simultaneous, 1, 10);
        var retries = Input(q.Retries.ToString(), 60);
        var done = new ComboBox { Width = 200 };
        foreach (CompletionAction a in Enum.GetValues<CompletionAction>()) done.Items.Add(new ComboBoxItem { Content = Loc.T("ca." + a), Tag = a });
        done.SelectedIndex = (int)q.OnComplete;

        void Save()
        {
            q.StartEnabled = startOn.IsChecked == true;
            q.StopEnabled = stopOn.IsChecked == true;
            if (TimeSpan.TryParse(startTime.Text, out var st)) q.StartAt = st;
            if (TimeSpan.TryParse(stopTime.Text, out var et)) q.StopAt = et;
            q.Daily = daily.IsChecked == true;
            q.Simultaneous = (int)(simul.SelectedItem ?? 1);
            if (int.TryParse(retries.Text, out var r)) q.Retries = r;
            q.OnComplete = (CompletionAction)((ComboBoxItem)done.SelectedItem).Tag;
        }
        foreach (var c in new Control[] { startOn, stopOn, daily, once })
            if (c is System.Windows.Controls.Primitives.ToggleButton tb) tb.Click += (_, _) => Save();
        startTime.LostFocus += (_, _) => Save(); stopTime.LostFocus += (_, _) => Save(); retries.LostFocus += (_, _) => Save();
        simul.SelectionChanged += (_, _) => Save(); done.SelectionChanged += (_, _) => Save();

        sp.Children.Add(Row(startOn, startTime));
        sp.Children.Add(once);
        sp.Children.Add(daily);
        sp.Children.Add(days);
        sp.Children.Add(Row(stopOn, stopTime));
        sp.Children.Add(Row(new TextBlock { Text = Loc.T("sch.retries") }, retries));
        sp.Children.Add(Row(new TextBlock { Text = Loc.T("sch.oncomplete") }, done));
        tabs.Items.Add(new TabItem { Header = Loc.T("sch.schedule"), Content = sp });

        // files in the queue
        var fp = new DockPanel { Margin = new Thickness(10) };
        var list = new ListBox();
        void FillFiles()
        {
            list.Items.Clear();
            foreach (var id in q.Items)
                if (App.Items.FirstOrDefault(i => i.Id == id) is { } it) list.Items.Add(new ListBoxItem { Content = $"{it.FileName}   {it.SizeText}", Tag = it });
        }
        FillFiles();
        var top = Row(new TextBlock { Text = Loc.T("batch.from").Length > 0 ? "" : "" }, simul, new TextBlock { Text = Loc.T("sch.files.at") });
        DockPanel.SetDock(top, Dock.Top);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        Button B(string key, Action a) { var b = new Button { Content = Loc.T(key), Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        B("sch.up", () => Move(-1)); B("sch.down", () => Move(1));
        B("sch.remove", () =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: DownloadItem it }) { q.Items.Remove(it.Id); it.Queue = null; FillFiles(); }
        });
        void Move(int dir)
        {
            int i = list.SelectedIndex;
            if (i < 0 || i + dir < 0 || i + dir >= q.Items.Count) return;
            (q.Items[i], q.Items[i + dir]) = (q.Items[i + dir], q.Items[i]);
            FillFiles(); list.SelectedIndex = i + dir;
        }
        DockPanel.SetDock(bar, Dock.Bottom);
        fp.Children.Add(top); fp.Children.Add(bar); fp.Children.Add(list);
        tabs.Items.Add(new TabItem { Header = Loc.T("sch.files"), Content = fp });
        return tabs;
    }
}

/// <summary>Options: General, File types, Save to, Downloads, Connection, Proxy, Site logins, Video button.</summary>
public sealed class OptionsWindow : Dlg
{
    public OptionsWindow() : base(Loc.T("op.title"), 680)
    {
        var s = App.Settings;
        var tabs = new TabControl { Height = 470 };
        TabItem Tab(string key, UIElement content) { var t = new TabItem { Header = Loc.T(key), Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) } }; tabs.Items.Add(t); return t; }

        // General
        var startup = Check(Loc.T("op.startup"), s.LaunchOnStartup);
        var clip = Check(Loc.T("op.clipboard"), s.MonitorClipboard);
        var capture = Check(Loc.T("op.capture"), s.CaptureFromBrowsers);
        var lang = new ComboBox { Width = 150 };
        lang.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        lang.Items.Add(new ComboBoxItem { Content = "العربية", Tag = "ar" });
        lang.SelectedIndex = Loc.Arabic ? 1 : 0;
        var integ = new Button { Content = Loc.T("op.integration"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        integ.Click += (_, _) => new BrowsersWindow { Owner = this }.ShowDialog();
        var g = new StackPanel();
        g.Children.Add(startup); g.Children.Add(clip); g.Children.Add(capture); g.Children.Add(integ);
        g.Children.Add(Row(new TextBlock { Text = Loc.T("op.lang") }, lang));
        Tab("op.general", g);

        // File types
        var types = new TextBox { Text = s.CaptureExtensions, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sites = new TextBox { Text = s.ExceptionSites, TextWrapping = TextWrapping.Wrap, Height = 60 };
        var addrs = new TextBox { Text = s.ExceptionAddresses, AcceptsReturn = true, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var defTypes = new Button { Content = Loc.T("op.default"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        defTypes.Click += (_, _) => types.Text = new Settings().CaptureExtensions;
        var f = new StackPanel();
        f.Children.Add(Label(Loc.T("op.capturetypes"), 0)); f.Children.Add(types); f.Children.Add(defTypes);
        f.Children.Add(Label(Loc.T("op.exsites"))); f.Children.Add(sites);
        f.Children.Add(Label(Loc.T("op.exaddr"))); f.Children.Add(addrs);
        Tab("op.filetypes", f);

        // Save to
        var cats = new ComboBox { Width = 200 };
        foreach (var c in s.Categories) cats.Items.Add(c);
        cats.DisplayMemberPath = nameof(CategoryInfo.Name);
        var exts = Input(); var folder = Input();
        CategoryInfo? current = null;
        void Commit() { if (current != null) { current.Extensions = exts.Text.Trim(); current.Folder = folder.Text.Trim(); } }
        cats.SelectionChanged += (_, _) => { Commit(); current = cats.SelectedItem as CategoryInfo; exts.Text = current?.Extensions ?? ""; folder.Text = current?.Folder ?? ""; };
        var general = Input(s.DefaultFolder);
        var temp = Input(s.TempFolder);
        var sv = new StackPanel();
        sv.Children.Add(Row(new TextBlock { Text = Loc.T("op.category") }, cats));
        sv.Children.Add(Label(Loc.T("op.exts"))); sv.Children.Add(exts);
        sv.Children.Add(Label(Loc.T("op.folder"))); sv.Children.Add(WithButton(folder, Loc.T("browse"), () => { if (Shell.PickFolder(folder.Text) is { } p) folder.Text = p; }, 80));
        sv.Children.Add(Label(Loc.T("op.generalfolder"), 14)); sv.Children.Add(WithButton(general, Loc.T("browse"), () => { if (Shell.PickFolder(general.Text) is { } p) general.Text = p; }, 80));
        sv.Children.Add(Label(Loc.T("op.temp"))); sv.Children.Add(WithButton(temp, Loc.T("browse"), () => { if (Shell.PickFolder(temp.Text) is { } p) temp.Text = p; }, 80));
        cats.SelectedIndex = 0;
        Tab("op.saveto", sv);

        // Downloads
        var showStart = Check(Loc.T("op.showstart"), s.ShowStartDialog);
        var showProgress = Check(Loc.T("op.showprogress"), s.ShowProgressDialog);
        var showComplete = Check(Loc.T("op.showcomplete"), s.ShowCompleteDialog);
        var dup = new ComboBox { Width = 280 };
        foreach (DuplicateAction a in Enum.GetValues<DuplicateAction>()) dup.Items.Add(new ComboBoxItem { Content = Loc.T("dupa." + a), Tag = a });
        dup.SelectedIndex = (int)s.Duplicates;
        var virus = Input(s.VirusScanner); var virusArgs = Input(s.VirusScannerArgs);
        var d = new StackPanel();
        d.Children.Add(showStart); d.Children.Add(showProgress); d.Children.Add(showComplete);
        d.Children.Add(Label(Loc.T("op.duplicates"))); d.Children.Add(dup);
        d.Children.Add(Label(Loc.T("op.virus"), 14));
        d.Children.Add(WithButton(virus, Loc.T("browse"), () =>
        {
            var o = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
            if (o.ShowDialog(this) == true) virus.Text = o.FileName;
        }, 80));
        d.Children.Add(Label(Loc.T("op.virusargs"))); d.Children.Add(virusArgs);
        Tab("op.downloads", d);

        // Connection
        var maxConn = new ComboBox { Width = 70 };
        foreach (var n in new[] { 1, 2, 4, 8, 16, 24, 32 }) maxConn.Items.Add(n);
        maxConn.SelectedItem = new[] { 1, 2, 4, 8, 16, 24, 32 }.OrderBy(n => Math.Abs(n - s.MaxConnections)).First();
        var timeout = Input(s.TimeoutSeconds.ToString(), 60);
        var retries = Input(s.Retries.ToString(), 60);
        var limits = new TextBox { Text = string.Join(Environment.NewLine, s.ServerLimits.Select(kv => $"{kv.Key}={kv.Value}")), AcceptsReturn = true, Height = 80 };
        var limitOn = Check(Loc.T("op.limits"), s.DownloadLimitOn);
        var mb = Input(s.DownloadLimitMB.ToString(), 60); var hrs = Input(s.DownloadLimitHours.ToString(), 40);
        var cn = new StackPanel();
        cn.Children.Add(Form((Loc.T("op.maxconn"), maxConn), (Loc.T("op.timeout"), timeout), (Loc.T("op.retries"), retries)));
        cn.Children.Add(new TextBlock { Text = Loc.T("op.connwarn"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 6) });
        cn.Children.Add(Label(Loc.T("op.exceptions"))); cn.Children.Add(limits);
        cn.Children.Add(Row(limitOn, mb, new TextBlock { Text = Loc.T("op.mbevery") }, hrs, new TextBlock { Text = Loc.T("op.hours") }));
        Tab("op.connection", cn);

        // Proxy
        var none = new RadioButton { Content = Loc.T("px.none"), IsChecked = s.Proxy == ProxyMode.None };
        var sys = new RadioButton { Content = Loc.T("px.system"), IsChecked = s.Proxy == ProxyMode.System };
        var man = new RadioButton { Content = Loc.T("px.manual"), IsChecked = s.Proxy == ProxyMode.Manual };
        var http = Input(s.HttpProxy); var useSocks = Check(Loc.T("px.socks"), s.UseSocks); var socks = Input(s.SocksProxy);
        var pu = Input(s.ProxyUser); var pp = new PasswordBox { Password = s.ProxyPassword, MinHeight = 24 };
        var bypass = Input(s.ProxyBypass);
        var px = new StackPanel();
        px.Children.Add(none); px.Children.Add(sys); px.Children.Add(man);
        px.Children.Add(Form((Loc.T("px.http"), http), ("", useSocks), ("SOCKS5", socks), (Loc.T("login"), pu), (Loc.T("password"), pp), (Loc.T("px.bypass"), bypass)));
        Tab("op.proxy", px);

        // Site logins
        var logins = new TextBox
        {
            Text = string.Join(Environment.NewLine, s.Logins.Select(l => $"{l.Site} {l.User} {l.Password}")),
            AcceptsReturn = true, Height = 220, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var lg = new StackPanel();
        lg.Children.Add(Label($"{Loc.T("lg.site")}  {Loc.T("login")}  {Loc.T("password")}", 0));
        lg.Children.Add(logins);
        Tab("op.logins", lg);

        // Video button
        var vbtn = Check(Loc.T("op.videobtn"), s.ShowVideoButton);
        var minKb = Input(s.MinVideoSizeKB.ToString(), 80);
        var vp = new StackPanel();
        vp.Children.Add(vbtn);
        vp.Children.Add(Row(new TextBlock { Text = Loc.T("op.minsize") }, minKb));
        Tab("op.video", vp);

        Body.Children.Add(tabs);
        Btn(Loc.T("ok"), () =>
        {
            Commit();
            s.LaunchOnStartup = startup.IsChecked == true;
            s.MonitorClipboard = clip.IsChecked == true;
            s.CaptureFromBrowsers = capture.IsChecked == true;
            var newLang = (string)((ComboBoxItem)lang.SelectedItem).Tag;
            bool langChanged = newLang != (Loc.Arabic ? "ar" : "en");
            s.Language = newLang;
            s.CaptureExtensions = types.Text.Trim();
            s.ExceptionSites = sites.Text.Trim();
            s.ExceptionAddresses = addrs.Text.Trim();
            s.DefaultFolder = general.Text.Trim();
            s.TempFolder = temp.Text.Trim();
            s.ShowStartDialog = showStart.IsChecked == true;
            s.ShowProgressDialog = showProgress.IsChecked == true;
            s.ShowCompleteDialog = showComplete.IsChecked == true;
            s.Duplicates = (DuplicateAction)((ComboBoxItem)dup.SelectedItem).Tag;
            s.VirusScanner = virus.Text.Trim(); s.VirusScannerArgs = virusArgs.Text.Trim();
            s.MaxConnections = (int)maxConn.SelectedItem;
            if (int.TryParse(timeout.Text, out var to)) s.TimeoutSeconds = Math.Clamp(to, 5, 600);
            if (int.TryParse(retries.Text, out var rt)) s.Retries = Math.Clamp(rt, 0, 1000);
            s.ServerLimits = limits.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=')).Where(p => p.Length == 2 && int.TryParse(p[1], out _)).ToDictionary(p => p[0].Trim(), p => int.Parse(p[1]));
            s.DownloadLimitOn = limitOn.IsChecked == true;
            if (int.TryParse(mb.Text, out var m)) s.DownloadLimitMB = m;
            if (int.TryParse(hrs.Text, out var h)) s.DownloadLimitHours = h;
            s.Proxy = none.IsChecked == true ? ProxyMode.None : man.IsChecked == true ? ProxyMode.Manual : ProxyMode.System;
            s.HttpProxy = http.Text.Trim(); s.UseSocks = useSocks.IsChecked == true; s.SocksProxy = socks.Text.Trim();
            s.ProxyUser = pu.Text.Trim(); s.ProxyPassword = pp.Password; s.ProxyBypass = bypass.Text.Trim();
            s.Logins = logins.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2)
                .Select(p => new SiteLogin { Site = p[0], User = p[1], Password = p.Length > 2 ? p[2] : "" }).ToList();
            s.ShowVideoButton = vbtn.IsChecked == true;
            if (long.TryParse(minKb.Text, out var mk)) s.MinVideoSizeKB = mk;
            App.Manager.SaveSettings();
            App.Manager.ApplySpeedLimit();
            App.ApplyStartup();
            if (langChanged) MessageBox.Show(this, newLang == "ar" ? "أعد تشغيل رهاد لتطبيق اللغة." : "Restart Rehad to apply the language.", Loc.T("app"));
            DialogResult = true;
        }, isDefault: true);
        Btn(Loc.T("cancel"), () => DialogResult = false, isCancel: true);
    }
}
