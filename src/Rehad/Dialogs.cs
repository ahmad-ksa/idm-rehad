using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Rehad.Core;

namespace Rehad;

/// <summary>"Enter new address to download".</summary>
public sealed class AddUrlWindow : Dlg
{
    AddUrlWindow() : base(Loc.T("addurl.title"), 520, true) { }

    public static void Ask(Window? owner = null)
    {
        var d = new AddUrlWindow();
        if (owner is { IsVisible: true }) { d.Owner = owner; d.ShowInTaskbar = false; }
        string clip = "";
        try { if (Clipboard.ContainsText()) clip = Links.Extract(Clipboard.GetText()).FirstOrDefault() ?? ""; } catch { }
        var url = Input(clip);
        var auth = Check(Loc.T("addurl.auth").Replace("_", ""), false);
        var login = Input(); var pass = new PasswordBox { MinHeight = 24 };
        var authGrid = Form((Loc.T("login"), login), (Loc.T("password"), pass));
        authGrid.IsEnabled = false;
        auth.Checked += (_, _) => authGrid.IsEnabled = true;
        auth.Unchecked += (_, _) => authGrid.IsEnabled = false;
        d.Body.Children.Add(Label(Loc.T("addurl.address"), 0));
        d.Body.Children.Add(url);
        d.Body.Children.Add(auth);
        d.Body.Children.Add(authGrid);
        d.Btn(Loc.T("ok"), () =>
        {
            var u = url.Text.Trim();
            if (!Uri.TryCreate(u, UriKind.Absolute, out _)) { url.Focus(); return; }
            d.Close();
            App.NewDownload(new App.Incoming { Url = u, Login = auth.IsChecked == true ? login.Text : null, Password = auth.IsChecked == true ? pass.Password : null });
        }, isDefault: true);
        d.Btn(Loc.T("cancel"), d.Close, isCancel: true);
        d.Loaded += (_, _) => { url.Focus(); url.SelectAll(); };
        d.Show();
        d.Activate();
    }
}

/// <summary>"Download File Info": category, save as, description, quality (videos) → Start / Later.</summary>
public sealed class FileInfoWindow : Dlg
{
    readonly DownloadItem item;
    readonly TextBox url, path, desc;
    readonly ComboBox category, quality;
    readonly CheckBox remember, subs, wholeList;
    List<YtDlp.PlaylistEntry> playlist = new();
    readonly TextBlock info;
    readonly Image icon;
    readonly CancellationTokenSource cts = new();
    bool nameEdited;

    public FileInfoWindow(DownloadItem item) : base(Loc.T("fi.title"), 600, true)
    {
        this.item = item;
        Topmost = true;
        url = Input(item.PageUrl ?? item.Url);
        url.IsReadOnly = item.Kind != DownloadKind.File;
        category = new ComboBox { MinWidth = 170 };
        foreach (var c in App.Settings.Categories) category.Items.Add(new ComboBoxItem { Content = CatName(c.Name), Tag = c.Name });
        category.Items.Add(new ComboBoxItem { Content = CatName("General"), Tag = "General" });
        category.SelectedItem = category.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == item.Category) ?? category.Items[^1];
        path = Input(item.FullPath);
        path.TextChanged += (_, _) => nameEdited = true;
        remember = Check(Loc.T("fi.remember", CatName(item.Category)), false);
        subs = Check(Loc.T("fi.subs"), false);
        subs.Visibility = item.Kind == DownloadKind.Site ? Visibility.Visible : Visibility.Collapsed;
        wholeList = Check("", false);
        wholeList.Visibility = Visibility.Collapsed;
        wholeList.FontWeight = FontWeights.SemiBold;
        desc = Input(item.Description);
        quality = new ComboBox { MinWidth = 260, Visibility = item.Kind == DownloadKind.File ? Visibility.Collapsed : Visibility.Visible };
        info = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Source = ShellIcons.ForFile(item.FileName, true) };

        category.SelectionChanged += (_, _) =>
        {
            var name = (string)((ComboBoxItem)category.SelectedItem).Tag;
            path.Text = Path.Combine(App.Settings.FolderFor(name), Path.GetFileName(path.Text));
            remember.Content = Loc.T("fi.remember", CatName(name));
            nameEdited = false;
        };

        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        var form = Form(
            (Loc.T("fi.url"), url),
            (Loc.T("fi.category"), category),
            (Loc.T("fi.saveas"), WithButton(path, "...", BrowseSave)),
            (Loc.T("fi.quality"), quality),
            (Loc.T("fi.desc"), desc));
        if (item.Kind == DownloadKind.File) form.RowDefinitions[3].Height = new GridLength(0);
        top.Children.Add(form);
        Body.Children.Add(top);
        Body.Children.Add(remember);
        Body.Children.Add(subs);
        Body.Children.Add(wholeList);
        Body.Children.Add(info);

        Btn(Loc.T("fi.start"), () => Finish(true), isDefault: true);
        var later = Btn(Loc.T("fi.later"), () => { });
        later.Click += (_, _) => LaterMenu(later);
        Btn(Loc.T("cancel"), Close, isCancel: true);
        Loaded += async (_, _) => { Topmost = false; await Inspect(); };
        Closed += (_, _) => cts.Cancel();
    }

    static string CatName(string n) => Loc.T("cat." + n) is var t && t != "cat." + n ? t : n;

    void BrowseSave()
    {
        var d = new SaveFileDialog { FileName = Path.GetFileName(path.Text), InitialDirectory = Path.GetDirectoryName(path.Text) };
        if (d.ShowDialog(this) == true) path.Text = d.FileName;
    }

    async Task Inspect()
    {
        info.Text = item.Kind == DownloadKind.File ? Loc.T("fi.checking") : Loc.T("fi.loadingq");
        try
        {
            if (item.Kind == DownloadKind.File)
            {
                var r = await Net.ProbeAsync(item, App.Settings, cts.Token);
                item.Size = r.Size; item.Resumable = r.Resumable;
                if (r.FinalUrl != item.Url && DownloadManager.IsHls(r.FinalUrl)) item.Kind = DownloadKind.Hls;
                if (r.ContentType is "application/vnd.apple.mpegurl" or "application/x-mpegurl")
                {
                    item.Kind = DownloadKind.Hls;
                    await LoadHls();
                    return;
                }
                if (!nameEdited && r.FileName.Length > 0 && r.FileName != item.FileName)
                {
                    item.FileName = r.FileName;
                    var cat = App.Settings.CategoryFor(r.FileName)?.Name ?? "General";
                    category.SelectedItem = category.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cat) ?? category.SelectedItem;
                    path.Text = Path.Combine(App.Settings.FolderFor(cat), r.FileName);
                    nameEdited = false;
                    icon.Source = ShellIcons.ForFile(r.FileName, true);
                }
                info.Text = Loc.T("fi.size", r.Size > 0 ? Fmt.Bytes(r.Size) : Loc.T("unknown")) +
                            "    " + Loc.T("pr.resume") + ": " + (r.Resumable ? Loc.T("pr.yes") : Loc.T("pr.no"));
            }
            else if (item.Kind == DownloadKind.Hls) await LoadHls();
            else
            {
                if (YtDlp.Exe == null) { info.Text = Loc.T("ytdlp.missing"); return; }
                if (YtDlp.LooksLikePlaylist(item.PageUrl ?? item.Url)) _ = LoadPlaylist();
                var m = await YtDlp.InfoAsync(item, App.Settings, cts.Token);
                if (!nameEdited || Path.GetFileNameWithoutExtension(path.Text) is "videoplayback" or "video" or "watch")
                {
                    path.Text = Path.Combine(Path.GetDirectoryName(path.Text)!, Net.Sanitize(m.Title) + ".mp4");
                    nameEdited = false;
                }
                if (desc.Text.Length == 0) desc.Text = m.Title;
                quality.Items.Clear();
                foreach (var f in m.Choices) quality.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f });
                if (quality.Items.Count > 0) quality.SelectedIndex = 0;
                info.Text = m.Duration > 0 ? $"{m.Title}  ({TimeSpan.FromSeconds(m.Duration):h\\:mm\\:ss})" : m.Title;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { info.Text = ex.Message; }
    }

    async Task LoadPlaylist()
    {
        try
        {
            var (title, entries) = await YtDlp.PlaylistAsync(item, App.Settings, cts.Token);
            if (entries.Count < 2) return;
            playlist = entries;
            wholeList.Content = Loc.T("fi.playlist", entries.Count, title);
            wholeList.Visibility = Visibility.Visible;
        }
        catch { }
    }

    async Task LoadHls()
    {
        var variants = await HlsDownloader.VariantsAsync(item, App.Settings, cts.Token);
        quality.Visibility = Visibility.Visible;
        if (Body.Children[0] is DockPanel dp && dp.Children[1] is Grid g) g.RowDefinitions[3].Height = GridLength.Auto;
        quality.Items.Clear();
        foreach (var v in variants) quality.Items.Add(new ComboBoxItem { Content = v.ToString(), Tag = v });
        if (quality.Items.Count > 0) quality.SelectedIndex = 0;
        if (Path.GetExtension(path.Text).ToLowerInvariant() is ".m3u8" or "")
            path.Text = Path.ChangeExtension(path.Text, ".mp4");
        info.Text = variants.Count > 0 ? "HLS" : "HLS (single stream)";
    }

    void Apply()
    {
        item.Url = item.Kind == DownloadKind.File ? url.Text.Trim() : item.Url;
        item.Category = (string)((ComboBoxItem)category.SelectedItem).Tag;
        var full = path.Text.Trim();
        item.SaveDir = Path.GetDirectoryName(full) ?? App.Settings.FolderFor(item.Category);
        item.FileName = Net.Sanitize(Path.GetFileName(full));
        item.Description = desc.Text;
        if (quality.SelectedItem is ComboBoxItem { Tag: MediaFormat f })
        {
            item.Format = f.Id;
            if (f.Id.StartsWith("mp3:")) item.FileName = Path.ChangeExtension(item.FileName, ".mp3");
            else if (!f.HasVideo) item.FileName = Path.ChangeExtension(item.FileName, "." + f.Ext);
        }
        item.Subtitles = subs.IsChecked == true;
        if (quality.SelectedItem is ComboBoxItem { Tag: HlsVariant v }) item.Format = v.Bandwidth.ToString();
        if (remember.IsChecked == true)
        {
            var c = App.Settings.Categories.FirstOrDefault(x => x.Name == item.Category);
            if (c != null) c.Folder = item.SaveDir; else App.Settings.DefaultFolder = item.SaveDir;
            App.Manager.SaveSettings();
        }
    }

    void Finish(bool start)
    {
        Apply();
        if (wholeList.IsChecked == true && playlist.Count > 1)
        {
            // one download per video, all in a queue (2 at a time)
            var q = App.Manager.MainQueue;
            var ext = Path.GetExtension(item.FileName);
            foreach (var e in playlist)
            {
                var it = App.MakeItem(new App.Incoming { Url = e.Url, PageUrl = e.Url, Kind = DownloadKind.Site, Title = e.Title, Cookies = item.Cookies, UserAgent = item.UserAgent });
                it.FileName = Net.Sanitize(e.Title) + ext;
                it.Description = e.Title;
                it.Format = item.Format;
                it.Subtitles = item.Subtitles;
                it.Category = item.Category;
                it.SaveDir = item.SaveDir;
                App.Manager.Add(it);
                App.Manager.AddToQueue(it, q);
            }
            q.Simultaneous = Math.Max(q.Simultaneous, 2);
            App.Manager.Save();
            Close();
            if (start) { App.Manager.StartQueue(q); App.ShowMain(); }
            return;
        }
        App.Manager.Add(item);
        Close();
        if (start) App.StartWithUi(item);
    }

    void LaterMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var q in App.Settings.Queues)
        {
            var mi = new MenuItem { Header = MainWindow.QueueName(q) };
            mi.Click += (_, _) => { Apply(); App.Manager.Add(item); App.Manager.AddToQueue(item, q); App.Manager.Save(); Close(); };
            menu.Items.Add(mi);
        }
        var none = new MenuItem { Header = Loc.T("fi.later").Replace("_", "") };
        none.Click += (_, _) => Finish(false);
        menu.Items.Add(new Separator());
        menu.Items.Add(none);
        menu.IsOpen = true;
    }
}

/// <summary>"Download complete".</summary>
public sealed class CompleteWindow : Dlg
{
    public CompleteWindow(DownloadItem item) : base(Loc.T("cp.title"), 560, true)
    {
        Topmost = true;
        var icon = new Image { Width = 32, Height = 32, Source = ShellIcons.ForFile(item.FileName, true), Margin = new Thickness(0, 0, 12, 0) };
        var head = Row(icon, new TextBlock { Text = Loc.T("cp.downloaded", Fmt.Bytes(item.Size)), FontSize = 14, FontWeight = FontWeights.SemiBold });
        Body.Children.Add(head);
        Body.Children.Add(Form(
            (Loc.T("cp.address"), new TextBox { Text = item.PageUrl ?? item.Url, IsReadOnly = true }),
            (Loc.T("cp.savedas"), new TextBox { Text = item.FullPath, IsReadOnly = true })));
        var dont = Check(Loc.T("cp.dontshow"), false);
        Body.Children.Add(dont);
        Btn(Loc.T("cp.open"), () => { Shell.Open(item.FullPath); Close(); }, isDefault: true);
        Btn(Loc.T("cp.openwith"), () => { Shell.OpenWith(item.FullPath); Close(); });
        Btn(Loc.T("cp.openfolder"), () => { Shell.OpenFolder(item.FullPath); Close(); });
        Btn(Loc.T("close"), Close, isCancel: true);
        Closed += (_, _) => { if (dont.IsChecked == true) { App.Settings.ShowCompleteDialog = false; App.Manager.SaveSettings(); } };
        Loaded += (_, _) => Topmost = false;
    }
}

/// <summary>"File Properties".</summary>
public sealed class PropertiesWindow : Dlg
{
    public PropertiesWindow(DownloadItem item) : base(Loc.T("pp.title"), 580)
    {
        var name = Input(item.FileName);
        var dir = Input(item.SaveDir);
        var address = Input(item.Url);
        var referer = Input(item.Referer ?? "");
        var desc = Input(item.Description);
        var login = Input(item.Login ?? ""); var pass = new PasswordBox { Password = item.Password ?? "", MinHeight = 24 };
        var icon = new Image { Width = 32, Height = 32, Source = ShellIcons.ForFile(item.FileName, true) };
        Body.Children.Add(Row(icon, new TextBlock { Text = item.FileName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 470 }));
        Body.Children.Add(Form(
            (Loc.T("pp.type"), new TextBlock { Text = ShellIcons.TypeName(item.FileName) }),
            (Loc.T("pp.status"), new TextBlock { Text = new StatusConverter().Convert(new object[] { item.Status, item.Progress, item.Error }, typeof(string), null!, null!).ToString() }),
            (Loc.T("pp.size"), new TextBlock { Text = item.Size > 0 ? $"{Fmt.Bytes(item.Size)} ({item.Size:N0})" : Loc.T("unknown") }),
            (Loc.T("col.name"), name),
            (Loc.T("pp.saveto"), WithButton(dir, Loc.T("browse"), () => { if (Shell.PickFolder(dir.Text) is { } f) dir.Text = f; }, 80)),
            (Loc.T("pp.address"), address),
            (Loc.T("pp.referer"), referer),
            (Loc.T("pp.desc"), desc),
            (Loc.T("login"), login),
            (Loc.T("password"), pass),
            (Loc.T("pp.lasttry"), new TextBlock { Text = item.LastTry?.ToString("g") ?? "" })));
        bool editable = !App.Manager.IsRunning(item);
        name.IsEnabled = dir.IsEnabled = address.IsEnabled = editable && !item.IsFinished;
        Btn(Loc.T("cp.open"), () => Shell.Open(item.FullPath)).IsEnabled = item.IsFinished;
        Btn(Loc.T("ok"), () =>
        {
            if (name.IsEnabled)
            {
                item.FileName = Net.Sanitize(name.Text.Trim());
                item.SaveDir = dir.Text.Trim();
                item.Url = address.Text.Trim();
            }
            item.Referer = referer.Text.Trim().Length > 0 ? referer.Text.Trim() : null;
            item.Description = desc.Text;
            item.Login = login.Text.Length > 0 ? login.Text : null;
            item.Password = pass.Password.Length > 0 ? pass.Password : null;
            App.Manager.Save();
            Close();
        }, isDefault: true);
        Btn(Loc.T("cancel"), Close, isCancel: true);
    }
}

public sealed class DuplicateWindow : Dlg
{
    DuplicateAction result = (DuplicateAction)(-1);

    DuplicateWindow(DownloadItem existing) : base(Loc.T("dup.title"), 520, true)
    {
        Topmost = true;
        Body.Children.Add(Label(Loc.T("dup.text"), 0));
        Body.Children.Add(new TextBlock { Text = existing.FileName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });
        var a = new RadioButton { Content = Loc.T("dup.number"), IsChecked = true };
        var b = new RadioButton { Content = Loc.T("dup.overwrite") };
        var c = new RadioButton { Content = Loc.T("dup.show") };
        Body.Children.Add(a); Body.Children.Add(b); Body.Children.Add(c);
        Btn(Loc.T("ok"), () =>
        {
            result = a.IsChecked == true ? DuplicateAction.Number : b.IsChecked == true ? DuplicateAction.Overwrite : DuplicateAction.Skip;
            DialogResult = true;
        }, isDefault: true);
        Btn(Loc.T("cancel"), () => DialogResult = false, isCancel: true);
    }

    public static DuplicateAction Ask(DownloadItem existing)
    {
        var d = new DuplicateWindow(existing);
        d.ShowDialog();
        return d.result;
    }
}

public sealed class ConfirmDeleteWindow : Dlg
{
    ConfirmDeleteWindow() : base(Loc.T("m.remove").Replace("_", ""), 440) { }

    public static (bool ok, bool files) Ask(Window owner, bool hasFinished)
    {
        var d = new ConfirmDeleteWindow { Owner = owner };
        d.Body.Children.Add(Label(Loc.T("confirm.delete"), 0));
        var files = Check(Loc.T("confirm.deletefiles"), false);
        files.Foreground = Brushes.Firebrick;
        if (hasFinished) d.Body.Children.Add(files);
        d.Btn(Loc.T("yes"), () => d.DialogResult = true, isDefault: true);
        d.Btn(Loc.T("no"), () => d.DialogResult = false, isCancel: true);
        return (d.ShowDialog() == true, files.IsChecked == true);
    }
}

/// <summary>"Batch download": address with * + range → many links.</summary>
public sealed class BatchWindow : Dlg
{
    public BatchWindow(string? pattern) : base(Loc.T("batch.title"), 560)
    {
        var addr = Input(pattern ?? "http://www.example.com/pictures/img*.jpg");
        var from = Input("1", 70); var to = Input("10", 70); var wild = Input("3", 50);
        var first = new TextBlock { Foreground = Brushes.DimGray }; var last = new TextBlock { Foreground = Brushes.DimGray };
        void Preview()
        {
            int.TryParse(wild.Text, out var w);
            var list = Links.Batch(addr.Text.Trim(), from.Text.Trim(), to.Text.Trim(), w);
            first.Text = list.FirstOrDefault() ?? ""; last.Text = list.LastOrDefault() ?? "";
        }
        foreach (var t in new[] { addr, from, to, wild }) t.TextChanged += (_, _) => Preview();
        Body.Children.Add(Label(Loc.T("batch.text"), 0));
        Body.Children.Add(Form((Loc.T("addurl.address"), addr)));
        Body.Children.Add(Row(new TextBlock { Text = Loc.T("batch.from") }, from, new TextBlock { Text = Loc.T("batch.to") }, to, new TextBlock { Text = Loc.T("batch.wild") }, wild));
        Body.Children.Add(Form((Loc.T("batch.first"), first), (Loc.T("batch.last"), last)));
        Preview();
        Btn(Loc.T("ok"), () =>
        {
            int.TryParse(wild.Text, out var w);
            var list = Links.Batch(addr.Text.Trim(), from.Text.Trim(), to.Text.Trim(), w);
            if (list.Count == 0) return;
            Close();
            LinksWindow.Show(list, null, null);
        }, isDefault: true);
        Btn(Loc.T("cancel"), Close, isCancel: true);
    }
}

/// <summary>"Download all links": checklist → add to queue (and start).</summary>
public sealed class LinksWindow : Dlg
{
    sealed class Row2 { public bool Check { get; set; } = true; public string Url { get; set; } = ""; public string Name { get; set; } = ""; public string Ext { get; set; } = ""; }

    LinksWindow(List<string> urls, string? referer, string? cookies) : base(Loc.T("links.title"), 760, true)
    {
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        Height = 520;
        var rows = urls.Distinct().Select(u => new Row2 { Url = u, Name = Net.FileNameFromUrl(u), Ext = Path.GetExtension(Net.FileNameFromUrl(u)).TrimStart('.').ToLowerInvariant() }).ToList();
        foreach (var r in rows) r.Check = App.Settings.ShouldCapture(r.Name) || YtDlp.IsSitePage(r.Url);
        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None, Height = 330, Background = Brushes.White,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Binding = new System.Windows.Data.Binding("Check") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 30 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("col.name"), Binding = new System.Windows.Data.Binding("Name"), Width = 220, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("col.url"), Binding = new System.Windows.Data.Binding("Url"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        Body.Children.Add(Label(Loc.T("links.text"), 0));
        Body.Children.Add(grid);
        var types = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        types.Children.Add(new TextBlock { Text = Loc.T("links.types"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var ext in rows.Select(r => r.Ext).Where(e => e.Length > 0 && e.Length < 6).Distinct().OrderBy(e => e).Take(20))
        {
            var cb = new CheckBox { Content = ext, Margin = new Thickness(0, 0, 10, 0), IsChecked = rows.Where(r => r.Ext == ext).All(r => r.Check) };
            cb.Click += (_, _) => { foreach (var r in rows.Where(r => r.Ext == ext)) r.Check = cb.IsChecked == true; grid.Items.Refresh(); };
            types.Children.Add(cb);
        }
        Body.Children.Add(types);
        var all = new Button { Content = Loc.T("links.all"), Margin = new Thickness(0, 6, 6, 0) };
        all.Click += (_, _) => { foreach (var r in rows) r.Check = true; grid.Items.Refresh(); };
        var none = new Button { Content = Loc.T("links.none"), Margin = new Thickness(0, 6, 6, 0) };
        none.Click += (_, _) => { foreach (var r in rows) r.Check = false; grid.Items.Refresh(); };
        var startNow = Check(Loc.T("links.start"), true);
        Body.Children.Add(Row(all, none, startNow));
        Btn(Loc.T("ok"), () =>
        {
            var q = App.Manager.MainQueue;
            foreach (var r in rows.Where(r => r.Check))
            {
                if (App.Manager.FindDuplicate(r.Url) != null) continue;
                var item = App.MakeItem(new App.Incoming { Url = r.Url, Referer = referer, Cookies = cookies });
                App.Manager.Add(item);
                App.Manager.AddToQueue(item, q);
            }
            App.Manager.Save();
            if (startNow.IsChecked == true) App.Manager.StartQueue(q);
            Close();
        }, isDefault: true);
        Btn(Loc.T("cancel"), Close, isCancel: true);
    }

    public static void Show(List<string> urls, string? referer, string? cookies)
    {
        if (urls.Count == 0) return;
        var w = new LinksWindow(urls, referer, cookies);
        w.Show();
        w.Activate();
    }
}

public sealed class SpeedLimiterWindow : Dlg
{
    public SpeedLimiterWindow() : base(Loc.T("sl.title"), 460)
    {
        var s = App.Settings;
        var on = Check(Loc.T("pr.uselimit"), s.SpeedLimitOn);
        var always = Check(Loc.T("sl.always"), s.SpeedLimitAlwaysOnStart);
        var kb = Input(s.SpeedLimitKBps.ToString(), 90);
        Body.Children.Add(on);
        Body.Children.Add(Row(new TextBlock { Text = Loc.T("sl.max") }, kb, new TextBlock { Text = Loc.T("kbps") }));
        Body.Children.Add(always);
        Btn(Loc.T("ok"), () =>
        {
            s.SpeedLimitOn = on.IsChecked == true;
            s.SpeedLimitAlwaysOnStart = always.IsChecked == true;
            if (int.TryParse(kb.Text, out var v) && v > 0) s.SpeedLimitKBps = v;
            App.Manager.ApplySpeedLimit();
            App.Manager.SaveSettings();
            DialogResult = true;
        }, isDefault: true);
        Btn(Loc.T("cancel"), () => DialogResult = false, isCancel: true);
    }
}

/// <summary>How to add the browser extension.</summary>
public sealed class BrowsersWindow : Dlg
{
    public BrowsersWindow() : base(Loc.T("bi.title"), 560)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "extension");
        Body.Children.Add(new TextBlock { Text = Loc.T("bi.text"), TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        var f = new Button { Content = Loc.T("bi.folder"), Margin = new Thickness(0, 12, 8, 0) };
        f.Click += (_, _) => { try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { } };
        var c = new Button { Content = Loc.T("bi.chrome"), Margin = new Thickness(0, 12, 8, 0) };
        c.Click += (_, _) => OpenBrowser("chrome.exe", "chrome://extensions/");
        var e = new Button { Content = Loc.T("bi.edge"), Margin = new Thickness(0, 12, 8, 0) };
        e.Click += (_, _) => OpenBrowser("msedge.exe", "edge://extensions/");
        Body.Children.Add(Row(f, c, e));
        Btn(Loc.T("close"), Close, isDefault: true, isCancel: true);
    }

    static void OpenBrowser(string exe, string url)
    {
        try { Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = true }); }
        catch { Clipboard.SetText(url); }
    }
}
