using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Rehad.Core;

namespace Rehad;

public partial class MainWindow : Window
{
    readonly ICollectionView view;
    Func<DownloadItem, bool> filter = _ => true;
    string findText = "";
    DateTime flashUntil;

    public MainWindow()
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        view = CollectionViewSource.GetDefaultView(App.Items);
        view.Filter = o => o is DownloadItem it && filter(it) && (findText.Length == 0 || Matches(it));
        view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.Added), ListSortDirection.Ascending));
        List.ItemsSource = view;
        RebuildTree();
        BuildArrangeMenu();
        LangEnMenu.IsChecked = !Loc.Arabic;
        LangArMenu.IsChecked = Loc.Arabic;
        UpdateSpeedMenu();
        UpdateButtons();
        InputBindings.Add(new KeyBinding(new Relay(() => AddUrl_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(() => Find_Click(this, new RoutedEventArgs())), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(() => { if (List.SelectedItems.Count > 0) Resume_Click(this, new RoutedEventArgs()); }), Key.F5, ModifierKeys.None));
    }

    bool Matches(DownloadItem it) =>
        it.FileName.Contains(findText, StringComparison.OrdinalIgnoreCase) ||
        it.Description.Contains(findText, StringComparison.OrdinalIgnoreCase) ||
        it.Url.Contains(findText, StringComparison.OrdinalIgnoreCase);

    protected override void OnClosing(CancelEventArgs e)
    {
        // like the classic download managers: closing the window keeps running in the tray
        e.Cancel = true;
        Hide();
        App.Manager.Save();
    }

    // ------------------------------------------------------------------ categories tree

    public void RebuildTree()
    {
        Tree.Items.Clear();
        var all = Node(Loc.T("cat.all"), "", (Func<DownloadItem, bool>)(_ => true));
        foreach (var c in App.Settings.Categories)
        {
            var name = c.Name;
            all.Items.Add(Node(Loc.T("cat." + name) is var t && t != "cat." + name ? t : name, "", (Func<DownloadItem, bool>)(i => i.Category == name)));
        }
        all.IsExpanded = true;
        Tree.Items.Add(all);
        Tree.Items.Add(Node(Loc.T("cat.unfinished"), "", (Func<DownloadItem, bool>)(i => !i.IsFinished)));
        Tree.Items.Add(Node(Loc.T("cat.finished"), "", (Func<DownloadItem, bool>)(i => i.IsFinished)));
        var queues = Node(Loc.T("cat.queues"), "", (Func<DownloadItem, bool>)(i => !string.IsNullOrEmpty(i.Queue)));
        foreach (var q in App.Settings.Queues)
        {
            var qn = q.Name;
            queues.Items.Add(Node(QueueName(q), "", (Func<DownloadItem, bool>)(i => i.Queue == qn)));
        }
        queues.IsExpanded = true;
        Tree.Items.Add(queues);
        all.IsSelected = true;
        BuildQueueMenus();
    }

    public static string QueueName(QueueInfo q) => Loc.T("q." + q.Name) is var t && t != "q." + q.Name ? t : q.Name;

    static TreeViewItem Node(string text, string glyph, Func<DownloadItem, bool> f)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x2B)), Margin = new Thickness(0, 1, 6, 0) });
        sp.Children.Add(new TextBlock { Text = text });
        return new TreeViewItem { Header = sp, Tag = f };
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: Func<DownloadItem, bool> f }) { filter = f; view.Refresh(); }
    }

    void HideCat_Click(object sender, RoutedEventArgs e)
    {
        bool hide = HideCatMenu.IsChecked;
        CatColumn.Width = hide ? new GridLength(0) : new GridLength(200);
        CatPanel.Visibility = CatSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ selection helpers

    List<DownloadItem> Selected => List.SelectedItems.Cast<DownloadItem>().ToList();

    public void Select(DownloadItem item)
    {
        App.ShowMain();
        List.SelectedItem = item;
        List.ScrollIntoView(item);
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void UpdateButtons()
    {
        var sel = Selected;
        ResumeBtn.IsEnabled = sel.Any(i => !i.IsFinished && !App.Manager.IsRunning(i));
        StopBtn.IsEnabled = sel.Any(App.Manager.IsRunning);
        DeleteBtn.IsEnabled = sel.Count > 0;
    }

    string lastStatuses = "";

    public void RefreshStatus()
    {
        if (!IsVisible) return;
        UpdateButtons();
        // re-apply the category filter when a download changes state (Unfinished → Finished …)
        var sig = string.Concat(App.Items.Select(i => (char)('a' + (int)i.Status)));
        if (sig != lastStatuses) { lastStatuses = sig; view.Refresh(); }
        if (DateTime.Now < flashUntil) return;
        int running = App.Manager.RunningCount;
        double speed = App.Items.Where(App.Manager.IsRunning).Sum(i => i.Speed);
        StatusText.Text = Loc.T("status.ready", App.Items.Count, running, Fmt.Rate(speed));
        SpeedStatus.Text = App.Settings.SpeedLimitOn ? $"{Loc.T("m.speed").Replace("_", "")}: {App.Settings.SpeedLimitKBps} {Loc.T("kbps")}" : "";
    }

    public void FlashStatus(string text)
    {
        StatusText.Text = text;
        flashUntil = DateTime.Now.AddSeconds(8);
    }

    // ------------------------------------------------------------------ list actions

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not DownloadItem it) return;
        if (it.IsFinished) Shell.Open(it.FullPath);
        else if (App.Manager.IsRunning(it)) App.ShowProgress(it);
        else new PropertiesWindow(it) { Owner = this }.ShowDialog();
    }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) Delete_Click(sender, e);
        else if (e.Key == Key.Enter) List_DoubleClick(sender, null!);
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) List.SelectAll();
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        string? text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
        foreach (var url in Links.Extract(text)) App.NewDownload(new App.Incoming { Url = url });
    }

    void ListMenu_Opened(object sender, RoutedEventArgs e) => BuildQueueMenus();
    void FileMenu_Opened(object sender, RoutedEventArgs e) => BuildQueueMenus();

    void BuildQueueMenus()
    {
        foreach (var menu in new[] { MoveQueueMenu, CtxQueueMenu })
        {
            menu.Items.Clear();
            foreach (var q in App.Settings.Queues)
            {
                var mi = new MenuItem { Header = QueueName(q) };
                mi.Click += (_, _) => { foreach (var it in Selected) App.Manager.AddToQueue(it, q); App.Manager.Save(); view.Refresh(); };
                menu.Items.Add(mi);
            }
        }
        StartQueueMenu.Items.Clear(); StopQueueMenu.Items.Clear();
        foreach (var q in App.Settings.Queues)
        {
            var s = new MenuItem { Header = QueueName(q) }; s.Click += (_, _) => App.Manager.StartQueue(q); StartQueueMenu.Items.Add(s);
            var t = new MenuItem { Header = QueueName(q) }; t.Click += (_, _) => App.Manager.StopQueue(q); StopQueueMenu.Items.Add(t);
        }
    }

    void BuildArrangeMenu()
    {
        (string key, string prop)[] cols =
        {
            ("col.name", nameof(DownloadItem.FileName)), ("col.size", nameof(DownloadItem.Size)), ("col.status", nameof(DownloadItem.Status)),
            ("col.left", nameof(DownloadItem.TimeLeft)), ("col.rate", nameof(DownloadItem.Speed)), ("col.last", nameof(DownloadItem.LastTry)),
            ("col.desc", nameof(DownloadItem.Description)), ("col.url", nameof(DownloadItem.Url)),
        };
        var order = new MenuItem { Header = "#" };
        order.Click += (_, _) => SortBy(nameof(DownloadItem.Added));
        ArrangeMenu.Items.Add(order);
        foreach (var (key, prop) in cols)
        {
            var mi = new MenuItem { Header = Loc.T(key) };
            mi.Click += (_, _) => SortBy(prop);
            ArrangeMenu.Items.Add(mi);
        }
    }

    string sortProp = nameof(DownloadItem.Added);
    ListSortDirection sortDir = ListSortDirection.Ascending;

    void SortBy(string prop)
    {
        sortDir = sortProp == prop && sortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        sortProp = prop;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(prop, sortDir));
    }

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        var h = col.Header as string;
        string prop =
            h == Loc.T("col.name") ? nameof(DownloadItem.FileName) :
            h == Loc.T("col.size") ? nameof(DownloadItem.Size) :
            h == Loc.T("col.status") ? nameof(DownloadItem.Status) :
            h == Loc.T("col.left") ? nameof(DownloadItem.TimeLeft) :
            h == Loc.T("col.rate") ? nameof(DownloadItem.Speed) :
            h == Loc.T("col.last") ? nameof(DownloadItem.LastTry) :
            h == Loc.T("col.desc") ? nameof(DownloadItem.Description) : nameof(DownloadItem.Queue);
        SortBy(prop);
    }

    // ------------------------------------------------------------------ commands

    void AddUrl_Click(object sender, RoutedEventArgs e) => AddUrlWindow.Ask(this);
    void Batch_Click(object sender, RoutedEventArgs e) => new BatchWindow(null) { Owner = this }.ShowDialog();
    void BatchClip_Click(object sender, RoutedEventArgs e)
    {
        var links = Links.Extract(Clipboard.ContainsText() ? Clipboard.GetText() : "");
        if (links.Count == 0) { MessageBox.Show(this, Loc.T("nolinks"), Loc.T("app")); return; }
        LinksWindow.Show(links, null, null);
    }
    void Grabber_Click(object sender, RoutedEventArgs e) => new GrabberWindow { Owner = this }.Show();
    void Exit_Click(object sender, RoutedEventArgs e) => App.ExitApp();

    void Open_Click(object sender, RoutedEventArgs e) { if (List.SelectedItem is DownloadItem it && it.IsFinished) Shell.Open(it.FullPath); }
    void OpenFolder_Click(object sender, RoutedEventArgs e) { if (List.SelectedItem is DownloadItem it) Shell.OpenFolder(it.FullPath); }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in Selected.Where(i => !i.IsFinished)) App.StartWithUi(it);
    }

    void Stop_Click(object sender, RoutedEventArgs e) { foreach (var it in Selected) App.Manager.Stop(it); }
    void StopAll_Click(object sender, RoutedEventArgs e) => App.Manager.StopAll();

    void Redownload_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in Selected) { App.Manager.Redownload(it); if (App.Settings.ShowProgressDialog) App.ShowProgress(it); }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var (ok, files) = ConfirmDeleteWindow.Ask(this, sel.Any(i => i.IsFinished));
        if (!ok) return;
        foreach (var it in sel) App.Manager.Remove(it, files);
    }

    void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in App.Items.Where(i => i.IsFinished).ToList()) App.Manager.Remove(it, false);
    }

    void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join(Environment.NewLine, Selected.Select(i => i.PageUrl ?? i.Url));
        if (urls.Length > 0) Clipboard.SetText(urls);
    }

    void Props_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DownloadItem it) new PropertiesWindow(it) { Owner = this }.ShowDialog();
    }

    void Find_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Visible; FindBox.Focus(); }
    void CloseFind_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Collapsed; FindBox.Text = ""; }
    void FindBox_TextChanged(object sender, TextChangedEventArgs e) { findText = FindBox.Text.Trim(); view.Refresh(); }
    void FindBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) CloseFind_Click(sender, e); }

    void Scheduler_Click(object sender, RoutedEventArgs e) => new SchedulerWindow { Owner = this }.ShowDialog();
    void StartQueue_Click(object sender, RoutedEventArgs e) => App.Manager.StartQueue(App.Manager.MainQueue);
    void StopQueue_Click(object sender, RoutedEventArgs e) => App.Manager.StopQueue(App.Manager.MainQueue);

    void UpdateSpeedMenu()
    {
        SpeedOnMenu.IsChecked = App.Settings.SpeedLimitOn;
        SpeedOffMenu.IsChecked = !App.Settings.SpeedLimitOn;
    }
    void SpeedOn_Click(object sender, RoutedEventArgs e) { App.Settings.SpeedLimitOn = true; App.Manager.ApplySpeedLimit(); App.Manager.SaveSettings(); UpdateSpeedMenu(); }
    void SpeedOff_Click(object sender, RoutedEventArgs e) { App.Settings.SpeedLimitOn = false; App.Manager.ApplySpeedLimit(); App.Manager.SaveSettings(); UpdateSpeedMenu(); }
    void SpeedSettings_Click(object sender, RoutedEventArgs e) { new SpeedLimiterWindow { Owner = this }.ShowDialog(); UpdateSpeedMenu(); }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        if (new OptionsWindow { Owner = this }.ShowDialog() == true) RebuildTree();
    }

    void LangEn_Click(object sender, RoutedEventArgs e) => SetLang("en");
    void LangAr_Click(object sender, RoutedEventArgs e) => SetLang("ar");
    void SetLang(string lang)
    {
        App.Settings.Language = lang;
        App.Manager.SaveSettings();
        LangEnMenu.IsChecked = lang == "en";
        LangArMenu.IsChecked = lang == "ar";
        MessageBox.Show(this, lang == "ar" ? "أعد تشغيل رهاد لتطبيق اللغة." : "Restart Rehad to apply the language.", Loc.T("app"));
    }

    void Browsers_Click(object sender, RoutedEventArgs e) => new BrowsersWindow { Owner = this }.ShowDialog();
    void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, Loc.T("ab.text", typeof(App).Assembly.GetName().Version?.ToString(3) ?? ""), Loc.T("m.about").Replace("_", ""), MessageBoxButton.OK, MessageBoxImage.Information);

    async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var exe = YtDlp.Exe;
        if (exe == null) { MessageBox.Show(this, Loc.T("ytdlp.missing"), Loc.T("app")); return; }
        FlashStatus("yt-dlp -U ...");
        var log = new System.Text.StringBuilder();
        try { await Tools.RunAsync(exe, "-U", l => log.AppendLine(l), CancellationToken.None); } catch (Exception ex) { log.AppendLine(ex.Message); }
        MessageBox.Show(this, Loc.T("ytdlp.updated") + "\n\n" + log.ToString().Trim(), Loc.T("app"));
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt|Rehad list (*.json)|*.json", FileName = "downloads.txt" };
        if (d.ShowDialog(this) != true) return;
        var items = Selected.Count > 1 ? Selected : App.Items.ToList();
        if (d.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(d.FileName, Store.Serialize(items));
        else File.WriteAllLines(d.FileName, items.Select(i => i.PageUrl ?? i.Url));
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Text or Rehad list (*.txt;*.json)|*.txt;*.json" };
        if (d.ShowDialog(this) != true) return;
        var text = File.ReadAllText(d.FileName);
        List<string> urls;
        if (d.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            urls = (Store.Deserialize<List<DownloadItem>>(text) ?? new()).Select(i => i.PageUrl ?? i.Url).ToList();
        else urls = Links.Extract(text);
        LinksWindow.Show(urls, null, null);
    }
}

public sealed class Relay(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => run();
}
