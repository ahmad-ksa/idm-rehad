using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rehad.Core;

namespace Rehad;

/// <summary>Per-file "Download progress" window: status tab, speed limiter tab, options on completion, connections map.</summary>
public sealed class ProgressWindow : Window
{
    readonly DownloadItem item;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly TextBlock url = Val(), status = Val(), size = Val(), done = Val(), rate = Val(), left = Val(), resume = Val();
    readonly ProgressBar bar = new() { Height = 18, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0xA7, 0x5B)) };
    readonly Canvas map = new() { Height = 16, Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF4)), ClipToBounds = true };
    readonly ListView conns = new() { Height = 120 };
    readonly StackPanel details = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button pause, toggle;

    static TextBlock Val() => new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2) };

    public ProgressWindow(DownloadItem item)
    {
        this.item = item;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = Loc.Flow;
        Background = Brushes.White;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        var tabs = new TabControl { Margin = new Thickness(10, 10, 10, 0) };

        // status tab
        var st = new StackPanel { Margin = new Thickness(10) };
        st.Children.Add(Dlg.Form(
            (Loc.T("fi.url"), url),
            (Loc.T("col.status"), status),
            (Loc.T("pr.filesize"), size),
            (Loc.T("pr.downloaded"), done),
            (Loc.T("pr.rate"), rate),
            (Loc.T("pr.left"), left),
            (Loc.T("pr.resume"), resume)));
        st.Children.Add(bar);
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("pr.n"), Width = 40, DisplayMemberBinding = new System.Windows.Data.Binding("N") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("pr.downloaded"), Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Done") });
        gv.Columns.Add(new GridViewColumn { Header = Loc.T("pr.info"), Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding("Info") });
        conns.View = gv;
        details.Children.Add(new TextBlock { Text = Loc.T("pr.conns"), Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.DimGray });
        details.Children.Add(new Border { BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1), Child = map, Margin = new Thickness(0, 0, 0, 6) });
        details.Children.Add(conns);
        st.Children.Add(details);
        tabs.Items.Add(new TabItem { Header = Loc.T("pr.status"), Content = st });

        // speed limiter tab
        var limiter = App.Manager.LimiterFor(item);
        var useLimit = Dlg.Check(Loc.T("pr.uselimit"), limiter.Rate > 0);
        var kb = Dlg.Input(limiter.Rate > 0 ? (limiter.Rate / 1024).ToString() : "200", 80);
        void ApplyLimit() => limiter.Rate = useLimit.IsChecked == true && long.TryParse(kb.Text, out var v) && v > 0 ? v * 1024 : 0;
        useLimit.Click += (_, _) => ApplyLimit();
        kb.TextChanged += (_, _) => ApplyLimit();
        var sl = new StackPanel { Margin = new Thickness(10) };
        sl.Children.Add(useLimit);
        sl.Children.Add(Dlg.Row(new TextBlock { Text = Loc.T("pr.maxspeed") }, kb, new TextBlock { Text = Loc.T("kbps") }));
        tabs.Items.Add(new TabItem { Header = Loc.T("pr.limiter"), Content = sl });

        // options on completion
        var showComplete = Dlg.Check(Loc.T("pr.showcomplete"), App.Settings.ShowCompleteDialog);
        var exit = Dlg.Check(Loc.T("pr.exitwhen"), false);
        var shutdown = Dlg.Check(Loc.T("pr.shutdown"), false);
        void ApplyCompletion() => App.SetCompletionOptions(item, showComplete.IsChecked == true, exit.IsChecked == true, shutdown.IsChecked == true);
        foreach (var c in new[] { showComplete, exit, shutdown }) c.Click += (_, _) => ApplyCompletion();
        var oc = new StackPanel { Margin = new Thickness(10) };
        oc.Children.Add(showComplete); oc.Children.Add(exit); oc.Children.Add(shutdown);
        tabs.Items.Add(new TabItem { Header = Loc.T("pr.oncomplete"), Content = oc });

        var buttons = new DockPanel { Margin = new Thickness(10, 8, 10, 10), LastChildFill = false };
        toggle = new Button { Content = Loc.T("pr.showdetails"), MinWidth = 120, Height = 28 };
        toggle.Click += (_, _) =>
        {
            bool show = details.Visibility != Visibility.Visible;
            details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = Loc.T(show ? "pr.hidedetails" : "pr.showdetails");
        };
        pause = new Button { Content = Loc.T("pr.pause").Replace("_", ""), MinWidth = 90, Height = 28 };
        pause.Click += (_, _) =>
        {
            if (App.Manager.IsRunning(item)) App.Manager.Stop(item);
            else App.Manager.Start(item);
        };
        var cancel = new Button { Content = Loc.T("cancel"), MinWidth = 90, Height = 28, IsCancel = true };
        cancel.Click += (_, _) => { App.Manager.Stop(item); Close(); };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(pause, Dock.Right);
        buttons.Children.Add(toggle);
        buttons.Children.Add(cancel);
        buttons.Children.Add(pause);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;

        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
        map.SizeChanged += (_, _) => DrawMap();
    }

    public void ShowError(string message)
    {
        Refresh();
        status.Text = Loc.T("st.Error") + ": " + message;
        status.Foreground = Brushes.Firebrick;
        Activate();
    }

    void Refresh()
    {
        Title = $"{item.Progress:0}% {item.FileName}";
        url.Text = item.PageUrl ?? item.Url;
        url.ToolTip = url.Text;
        var running = App.Manager.IsRunning(item);
        status.Foreground = item.Status == DownloadStatus.Error ? Brushes.Firebrick : Brushes.Black;
        status.Text = item.Status switch
        {
            DownloadStatus.Downloading => item.ActiveConnections > 1 ? $"Receiving data... ({item.ActiveConnections} connections)" : "Receiving data...",
            DownloadStatus.Error => Loc.T("st.Error") + ": " + item.Error,
            _ => new StatusConverter().Convert(new object[] { item.Status, item.Progress, item.Error }, typeof(string), null!, null!).ToString()!,
        };
        size.Text = item.Size > 0 ? Fmt.Bytes(item.Size) : Loc.T("unknown");
        done.Text = item.Size > 0 ? $"{Fmt.Bytes(item.Downloaded)} ({item.Progress:0.00} %)" : Fmt.Bytes(item.Downloaded);
        rate.Text = Fmt.Rate(item.Speed);
        left.Text = Fmt.Time(item.TimeLeft);
        resume.Text = item.Resumable switch { true => Loc.T("pr.yes"), false => Loc.T("pr.no"), _ => Loc.T("unknown") };
        bar.Value = item.Progress;
        bar.IsIndeterminate = item.Size <= 0 && running;
        pause.Content = (running ? Loc.T("pr.pause") : Loc.T("pr.resumebtn")).Replace("_", "");
        pause.IsEnabled = !item.IsFinished;
        if (details.Visibility == Visibility.Visible)
        {
            conns.ItemsSource = item.Segments.ToList().Select((s, i) => new { N = i + 1, Done = Fmt.Bytes(s.Done), Info = s.Active ? (s.Info.Length > 0 ? s.Info : "Receiving data...") : s.Finished ? "Done" : "" }).ToList();
            DrawMap();
        }
    }

    void DrawMap()
    {
        map.Children.Clear();
        double w = map.ActualWidth;
        if (w <= 0 || item.Size <= 0) return;
        var done = new SolidColorBrush(Color.FromRgb(0x3A, 0x8E, 0xDB));
        var active = new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x5C));
        foreach (var s in item.Segments.ToList())
        {
            double x = s.Start * w / item.Size;
            double dw = Math.Max(0, s.Done * w / item.Size);
            var r = new Rectangle { Width = Math.Max(1, dw), Height = 16, Fill = s.Active ? active : done };
            Canvas.SetLeft(r, x);
            map.Children.Add(r);
            var tick = new Rectangle { Width = 1, Height = 16, Fill = Brushes.DimGray };
            Canvas.SetLeft(tick, x);
            map.Children.Add(tick);
        }
    }
}
