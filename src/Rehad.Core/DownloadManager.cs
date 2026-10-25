namespace Rehad.Core;

public sealed class DownloadList
{
    public int NextId { get; set; } = 1;
    public List<DownloadItem> Items { get; set; } = new();
}

/// <summary>
/// Owns the download list, runs jobs (segmented / HLS / yt-dlp), queues, scheduler, speed limiter and statistics.
/// All list mutations must be made on the UI thread (the list is bound to the UI); jobs only change item properties.
/// </summary>
public sealed class DownloadManager
{
    public Settings Settings { get; private set; }
    public IList<DownloadItem> Items { get; }
    public SpeedLimiter GlobalLimiter { get; } = new();

    readonly Dictionary<int, CancellationTokenSource> running = new();
    readonly Dictionary<int, SpeedLimiter> perItemLimit = new();
    readonly Dictionary<int, (long bytes, DateTime at)> samples = new();
    int nextId;
    long windowBytes; DateTime windowStart = DateTime.Now;

    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;
    public event Action<DownloadItem>? Started;
    public event Action<QueueInfo>? QueueFinished;
    public event Action<string>? LimitExceeded;

    /// <summary>Marshals a callback to the UI thread (set by the app).</summary>
    public Action<Action> Ui { get; set; } = a => a();

    public DownloadManager(Settings settings, IList<DownloadItem> items)
    {
        Settings = settings;
        Items = items;
        var saved = Store.Load<DownloadList>("downloads.json");
        nextId = Math.Max(1, saved.NextId);
        foreach (var it in saved.Items)
        {
            if (it.IsRunning || it.Status == DownloadStatus.Queued && it.Downloaded > 0) it.Status = DownloadStatus.Paused;
            Items.Add(it);
            nextId = Math.Max(nextId, it.Id + 1);
        }
        ApplySpeedLimit();
    }

    public void ReplaceSettings(Settings s) { Settings = s; ApplySpeedLimit(); }

    public void ApplySpeedLimit() => GlobalLimiter.Rate = Settings.SpeedLimitOn ? Settings.SpeedLimitKBps * 1024L : 0;

    public SpeedLimiter LimiterFor(DownloadItem item)
    {
        lock (perItemLimit)
        {
            if (!perItemLimit.TryGetValue(item.Id, out var l)) perItemLimit[item.Id] = l = new SpeedLimiter();
            return l;
        }
    }

    public void Save()
    {
        // jobs may be changing segment lists while we serialize: retry a few times
        for (int i = 0; i < 5; i++)
        {
            try { Store.Save("downloads.json", new DownloadList { NextId = nextId, Items = Items.ToList() }); return; }
            catch (InvalidOperationException) { Thread.Sleep(20); }
            catch (IOException) { Thread.Sleep(50); }
        }
    }
    public void SaveSettings() => Store.Save("settings.json", Settings);

    public DownloadItem Create(string url) => new()
    {
        Id = nextId++,
        Url = url,
        Kind = YtDlp.IsSitePage(url) ? DownloadKind.Site : IsHls(url) ? DownloadKind.Hls : DownloadKind.File,
        FileName = Net.FileNameFromUrl(url),
    };

    public static bool IsHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    public void Assign(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.Category) || item.Category == "General")
            item.Category = Settings.CategoryFor(item.FileName)?.Name ?? "General";
        if (string.IsNullOrEmpty(item.SaveDir)) item.SaveDir = Settings.FolderFor(item.Category);
    }

    public void Add(DownloadItem item)
    {
        Assign(item);
        Items.Add(item);
        Save();
    }

    public DownloadItem? FindDuplicate(string url) =>
        Items.FirstOrDefault(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning(DownloadItem item) { lock (running) return running.ContainsKey(item.Id); }
    public int RunningCount { get { lock (running) return running.Count; } }

    public void Start(DownloadItem item)
    {
        lock (running)
        {
            if (running.ContainsKey(item.Id) || item.Status == DownloadStatus.Complete) return;
            var cts = new CancellationTokenSource();
            running[item.Id] = cts;
            item.Status = DownloadStatus.Connecting;
            _ = Task.Run(() => RunJob(item, cts.Token));
        }
        Started?.Invoke(item);
    }

    async Task RunJob(DownloadItem item, CancellationToken ct)
    {
        try
        {
            switch (item.Kind)
            {
                case DownloadKind.Site: await YtDlp.DownloadAsync(item, Settings, ct); break;
                case DownloadKind.Hls: await new HlsDownloader(item, Settings, GlobalLimiter).RunAsync(ct); break;
                default: await new SegmentedDownloader(item, Settings, GlobalLimiter, LimiterFor(item)).RunAsync(ct); break;
            }
            item.Status = DownloadStatus.Complete;
            item.Speed = 0; item.TimeLeft = null; item.ActiveConnections = 0;
            item.Error = "";
            await VirusCheck(item);
            Ui(() => { Save(); Completed?.Invoke(item); });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Status = DownloadStatus.Paused;
            item.Speed = 0; item.TimeLeft = null; item.ActiveConnections = 0;
            Ui(Save);
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Error;
            item.Error = ex.Message;
            item.Speed = 0; item.TimeLeft = null; item.ActiveConnections = 0;
            Ui(() => { Save(); Failed?.Invoke(item); });
        }
        finally
        {
            lock (running) running.Remove(item.Id);
            Ui(PumpQueues);
        }
    }

    async Task VirusCheck(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(Settings.VirusScanner) || !File.Exists(Settings.VirusScanner)) return;
        var args = string.IsNullOrWhiteSpace(Settings.VirusScannerArgs)
            ? $"\"{item.FullPath}\""
            : Settings.VirusScannerArgs.Replace("[URL]", item.Url).Replace("[FILE]", item.FullPath);
        try { await Tools.RunAsync(Settings.VirusScanner, args, null, CancellationToken.None); } catch { }
    }

    public void Stop(DownloadItem item)
    {
        lock (running) if (running.TryGetValue(item.Id, out var cts)) cts.Cancel();
    }

    public void StopAll()
    {
        lock (running) foreach (var c in running.Values) c.Cancel();
        foreach (var q in Settings.Queues) q.Running = false;
    }

    public void Remove(DownloadItem item, bool deleteFile)
    {
        Stop(item);
        Items.Remove(item);
        foreach (var q in Settings.Queues) q.Items.Remove(item.Id);
        try { var t = SegmentedDownloader.TempDirFor(Settings, item); if (Directory.Exists(t)) Directory.Delete(t, true); } catch { }
        if (deleteFile && item.IsFinished) try { File.Delete(item.FullPath); } catch { }
        Save();
    }

    /// <summary>Starts the file again from zero (File → Redownload).</summary>
    public void Redownload(DownloadItem item)
    {
        Stop(item);
        try { var t = SegmentedDownloader.TempDirFor(Settings, item); if (Directory.Exists(t)) Directory.Delete(t, true); } catch { }
        item.Segments.Clear(); item.Downloaded = 0; item.Size = -1; item.Resumable = null;
        item.Status = DownloadStatus.Queued;
        Start(item);
    }

    // ------------------------------------------------------------------ queues

    public QueueInfo MainQueue => Settings.Queues[0];

    public void AddToQueue(DownloadItem item, QueueInfo q)
    {
        foreach (var other in Settings.Queues) other.Items.Remove(item.Id);
        q.Items.Add(item.Id);
        item.Queue = q.Name;
        if (item.Status is DownloadStatus.Error or DownloadStatus.Paused) item.Status = DownloadStatus.Queued;
    }

    public void StartQueue(QueueInfo q) { q.Running = true; PumpQueues(); }

    public void StopQueue(QueueInfo q)
    {
        q.Running = false;
        foreach (var id in q.Items) if (Items.FirstOrDefault(i => i.Id == id) is { } it) Stop(it);
    }

    /// <summary>Keeps each running queue busy with up to N simultaneous files.</summary>
    public void PumpQueues()
    {
        foreach (var q in Settings.Queues.Where(q => q.Running))
        {
            var members = q.Items.Select(id => Items.FirstOrDefault(i => i.Id == id)).Where(i => i != null).Cast<DownloadItem>().ToList();
            int active = members.Count(IsRunning);
            foreach (var it in members.Where(i => !i.IsFinished && !IsRunning(i) && i.Status != DownloadStatus.Error))
            {
                if (active >= Math.Max(1, q.Simultaneous)) break;
                Start(it); active++;
            }
            if (members.All(i => i.IsFinished || i.Status == DownloadStatus.Error) && !members.Any(IsRunning))
            {
                q.Running = false;
                QueueFinished?.Invoke(q);
            }
        }
    }

    /// <summary>Called every second by the app: speeds, time left, scheduler and download limits.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        long totalBytes = 0;
        foreach (var it in Items.Where(IsRunning).ToList())
        {
            if (samples.TryGetValue(it.Id, out var prev))
            {
                var dt = (now - prev.at).TotalSeconds;
                if (dt > 0.2)
                {
                    long delta = Math.Max(0, it.Downloaded - prev.bytes);
                    totalBytes += delta;
                    if (it.Kind != DownloadKind.Site) it.Speed = it.Speed <= 0 ? delta / dt : it.Speed * 0.6 + delta / dt * 0.4;
                    it.TimeLeft = it.Speed > 1 && it.Size > 0 ? TimeSpan.FromSeconds((it.Size - it.Downloaded) / it.Speed) : null;
                    samples[it.Id] = (it.Downloaded, now);
                }
            }
            else samples[it.Id] = (it.Downloaded, now);
            it.NotifyStats();
        }
        foreach (var id in samples.Keys.ToList()) if (!Items.Any(i => i.Id == id && IsRunning(i))) samples.Remove(id);

        // download limits ("no more than X MB every Y hours")
        if (Settings.DownloadLimitOn)
        {
            if ((now - windowStart).TotalHours >= Math.Max(1, Settings.DownloadLimitHours)) { windowStart = now; windowBytes = 0; }
            windowBytes += totalBytes;
            if (windowBytes > Settings.DownloadLimitMB * 1024L * 1024 && RunningCount > 0)
            {
                StopAll();
                LimitExceeded?.Invoke($"{windowBytes / (1024 * 1024)} MB");
            }
        }

        // scheduler: start / stop queues at their times
        if (lastMinute != now.Minute)
        {
            lastMinute = now.Minute;
            foreach (var q in Settings.Queues)
            {
                bool today = !q.Daily || (q.Days & (1 << (int)now.DayOfWeek)) != 0;
                if (q.StartEnabled && today && Same(now, q.StartAt) && !q.Running) StartQueue(q);
                if (q.StopEnabled && today && Same(now, q.StopAt) && q.Running) StopQueue(q);
            }
        }
    }

    int lastMinute = -1;
    static bool Same(DateTime now, TimeSpan at) => now.Hour == at.Hours && now.Minute == at.Minutes;
}
