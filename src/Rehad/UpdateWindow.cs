using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rehad.Core;

namespace Rehad;

/// <summary>"A new version is available" → downloads the installer with our own engine and runs it silently.</summary>
public sealed class UpdateWindow : Dlg
{
    readonly AppRelease release;
    readonly ProgressBar bar = new() { Height = 16, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed };
    readonly TextBlock status = new() { Foreground = Brushes.DimGray };
    readonly CancellationTokenSource cts = new();

    public UpdateWindow(AppRelease release) : base(Loc.T("up.title"), 460, true)
    {
        this.release = release;
        Topmost = true;
        Body.Children.Add(new TextBlock { Text = Loc.T("up.available", release.Version.ToString(3), Updater.Current.ToString(3)), TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        Body.Children.Add(bar);
        Body.Children.Add(status);
        Button? now = null, later = null, skip = null;
        now = Btn(Loc.T("up.now"), async () =>
        {
            now!.IsEnabled = later!.IsEnabled = skip!.IsEnabled = false;
            await Install();
        }, isDefault: true);
        later = Btn(Loc.T("up.later"), Close, isCancel: true);
        skip = Btn(Loc.T("up.skip"), () => { App.Settings.SkippedVersion = release.Tag; App.Manager.SaveSettings(); Close(); });
        Closed += (_, _) => cts.Cancel();
    }

    async Task Install()
    {
        status.Text = Loc.T("up.downloading");
        bar.Visibility = Visibility.Visible;
        var dir = Path.Combine(Path.GetTempPath(), "RehadUpdate");
        Directory.CreateDirectory(dir);
        var item = new DownloadItem { Id = -1, Url = release.SetupUrl, FileName = release.SetupName, SaveDir = dir };
        var s = App.Settings;
        var tempSettings = Store.Deserialize<Settings>(Store.Serialize(s))!;
        tempSettings.TempFolder = Path.Combine(dir, "tmp");
        tempSettings.Duplicates = DuplicateAction.Overwrite;
        var job = new SegmentedDownloader(item, tempSettings, new SpeedLimiter(), new SpeedLimiter()).RunAsync(cts.Token);
        while (!job.IsCompleted)
        {
            bar.Value = item.Progress;
            status.Text = $"{Loc.T("up.downloading")}  {Fmt.Bytes(item.Downloaded)} / {Fmt.Bytes(release.Size)}";
            await Task.Delay(300);
        }
        try { await job; }
        catch (Exception ex) { status.Text = ex.Message; status.Foreground = Brushes.Firebrick; return; }
        bar.Value = 100;
        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            App.ExitApp();
        }
        catch (Exception ex) { status.Text = ex.Message; status.Foreground = Brushes.Firebrick; }
    }

    /// <summary>Startup / menu check. quiet = don't say "you have the latest version".</summary>
    public static async Task CheckAsync(bool quiet)
    {
        AppRelease? r = null;
        try { r = await Updater.CheckAsync(App.Settings, CancellationToken.None); } catch { }
        if (r == null)
        {
            if (!quiet) MessageBox.Show(Loc.T("up.none", Updater.Current.ToString(3)), Loc.T("up.title"));
            return;
        }
        if (quiet && r.Tag == App.Settings.SkippedVersion) return;
        var w = new UpdateWindow(r);
        w.Show();
        w.Activate();
    }
}
