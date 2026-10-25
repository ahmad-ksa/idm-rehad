using System.Net;

namespace Rehad.Core;

/// <summary>
/// Multi-connection downloader with dynamic segmentation:
///  • the first connection asks for "bytes=0-" and learns the size and whether the server supports ranges;
///  • every connection writes its own part file (temp folder), so a pause/crash loses nothing;
///  • when a connection finishes, the biggest remaining segment is split in half and a new connection takes the second half;
///  • at the end all parts are appended into the final file ("assembling").
/// </summary>
public sealed class SegmentedDownloader
{
    const int Buffer = 64 * 1024;
    const long MinSplit = 256 * 1024;

    readonly DownloadItem item;
    readonly Settings settings;
    readonly SpeedLimiter global;
    readonly SpeedLimiter own;
    readonly object gate = new();
    readonly List<Task> workers = new();
    CancellationToken ct;
    string tempDir = "";
    int maxConn;
    int nextIndex;
    Exception? fatal;

    public SegmentedDownloader(DownloadItem item, Settings settings, SpeedLimiter global, SpeedLimiter own)
    {
        this.item = item; this.settings = settings; this.global = global; this.own = own;
    }

    public static string TempDirFor(Settings s, DownloadItem item) =>
        Path.Combine(s.TempFolder, $"dl{item.Id}"); // short on purpose: Windows paths are limited to 260 characters

    string PartPath(Segment seg) => Path.Combine(tempDir, $"part_{seg.Index:D3}.tmp");

    public async Task RunAsync(CancellationToken token)
    {
        ct = token;
        tempDir = TempDirFor(settings, item);
        Directory.CreateDirectory(tempDir);
        maxConn = item.MaxConnections > 0 ? item.MaxConnections : settings.ConnectionsFor(item.Url);
        item.Status = DownloadStatus.Connecting;
        item.Error = "";
        item.LastTry = DateTime.Now;

        lock (gate)
        {
            // restore progress from part files (resume after pause or crash)
            foreach (var seg in item.Segments)
            {
                var p = PartPath(seg);
                long len = File.Exists(p) ? new FileInfo(p).Length : 0;
                if (seg.End >= 0) len = Math.Min(len, seg.End - seg.Start + 1);
                seg.Done = len;
            }
            if (item.Segments.Count == 0 || item.Resumable == false)
            {
                foreach (var seg in item.Segments) TryDelete(PartPath(seg));
                item.Segments = new() { new Segment { Index = 0, Start = 0, End = -1 } };
            }
            nextIndex = item.Segments.Max(s => s.Index) + 1;
            item.Downloaded = item.Segments.Sum(s => s.Done);
        }

        // first pending segment discovers size / resume support
        var first = item.Segments.FirstOrDefault(s => !s.Finished);
        if (first != null)
        {
            StartWorker(first);
            // others start after the server answers (see OnHeaders)
        }

        // wait for all workers (new ones may be added while waiting)
        while (true)
        {
            Task[] snapshot;
            lock (gate) snapshot = workers.Where(w => !w.IsCompleted).ToArray();
            if (snapshot.Length == 0) break;
            await Task.WhenAny(snapshot);
        }

        ct.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;

        bool complete = item.Size >= 0
            ? item.Segments.Sum(s => s.Done) >= item.Size
            : item.Segments.All(s => s.Done > 0 || s.End >= 0);
        if (!complete) throw new IOException("Download incomplete");

        await AssembleAsync();
    }

    void StartWorker(Segment seg)
    {
        lock (gate)
        {
            if (seg.Active) return;
            seg.Active = true;
            item.ActiveConnections = item.Segments.Count(s => s.Active);
            workers.Add(Task.Run(() => WorkerAsync(seg)));
        }
    }

    bool headersSeen;

    void OnHeaders(ProbeResult info, Segment seg)
    {
        lock (gate)
        {
            if (headersSeen) return;
            headersSeen = true;
            item.Resumable = info.Resumable;
            if (info.Size >= 0) item.Size = info.Size;
            if (item.Size >= 0 && seg.End < 0) seg.End = item.Size - 1;
        }
        item.Status = DownloadStatus.Downloading;
        // resume any other unfinished segments, then open extra connections by splitting
        if (info.Resumable && item.Size > 0)
        {
            foreach (var other in item.Segments.Where(s => s != seg && !s.Finished).ToList())
                if (CountActive() < maxConn) StartWorker(other);
            while (CountActive() < maxConn && TrySplit()) { }
        }
    }

    int CountActive() { lock (gate) return item.Segments.Count(s => s.Active); }

    /// <summary>Halves the segment with the most bytes left and starts a connection for the second half.</summary>
    bool TrySplit()
    {
        Segment? created = null;
        lock (gate)
        {
            if (ct.IsCancellationRequested || item.Resumable != true || item.Size <= 0) return false;
            Segment? best = null; long bestLeft = 0;
            foreach (var s in item.Segments)
            {
                if (s.End < 0) continue;
                long left;
                lock (s.Gate) left = s.End - s.Position + 1;
                // an idle unfinished segment is picked up directly instead of being split
                if (!s.Active && left > 0) { best = s; bestLeft = -1; break; }
                if (left > bestLeft) { best = s; bestLeft = left; }
            }
            if (best == null) return false;
            if (bestLeft == -1) { created = best; }
            else
            {
                if (bestLeft < MinSplit * 2) return false;
                lock (best.Gate)
                {
                    long left = best.End - best.Position + 1;
                    if (left < MinSplit * 2) return false;
                    long mid = best.Position + left / 2;
                    created = new Segment { Index = nextIndex++, Start = mid, End = best.End };
                    best.End = mid - 1;
                }
                item.Segments.Add(created);
                item.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }
        StartWorker(created!);
        return true;
    }

    async Task WorkerAsync(Segment seg)
    {
        int failures = 0;
        try
        {
            while (!seg.Finished && !ct.IsCancellationRequested)
            {
                try
                {
                    await DownloadSegmentAsync(seg);
                    if (seg.End < 0) break; // unknown size: stream ended = done
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (FatalDownloadException ex) { fatal = ex; break; }
                catch (Exception ex)
                {
                    seg.Info = ex.Message;
                    if (++failures > Math.Max(1, settings.Retries)) { fatal ??= ex; break; }
                    item.Error = $"Connection restarted: {ex.Message}";
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + failures)), ct); } catch { break; }
                }
            }
        }
        finally
        {
            lock (gate)
            {
                seg.Active = false;
                item.ActiveConnections = item.Segments.Count(s => s.Active);
            }
            item.NotifyStats();
        }
        // this connection is free: help with the biggest remaining piece
        if (!ct.IsCancellationRequested && fatal == null && headersSeen) TrySplit();
    }

    async Task DownloadSegmentAsync(Segment seg)
    {
        long from, to;
        lock (seg.Gate) { from = seg.Position; to = seg.End; }
        bool askRange = from > 0 || item.Resumable != false;
        using var req = Net.Request(item.Url, item, settings, askRange ? from : -1, to);
        seg.Info = "Connecting...";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
        using var resp = await Net.Client(settings).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new FatalDownloadException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new IOException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (item.Size > 0 && from >= item.Size) return; // already complete
            throw new IOException("Range not satisfiable");
        }

        var info = Net.Describe(resp, item.Url);
        if (resp.StatusCode == HttpStatusCode.OK && from > 0)
        {
            // server ignored the range: it can't resume, start over with one connection
            lock (gate)
            {
                item.Resumable = false;
                foreach (var s in item.Segments) { TryDelete(PartPath(s)); s.Done = 0; }
                item.Segments = new() { seg };
                seg.Start = 0; seg.Done = 0; seg.End = info.Size > 0 ? info.Size - 1 : -1;
                item.Downloaded = 0;
                from = 0;
            }
        }
        OnHeaders(info, seg);
        if (string.IsNullOrEmpty(item.FileName) || item.FileName == "download") item.FileName = info.FileName;

        seg.Info = "Receiving data...";
        await using var body = await resp.Content.ReadAsStreamAsync(timeout.Token);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        await using var file = new FileStream(PartPath(seg), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, Buffer, false);
        file.SetLength(from - seg.Start);
        file.Seek(0, SeekOrigin.End);

        var buf = new byte[Buffer];
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
            int want = buf.Length;
            lock (seg.Gate) if (seg.End >= 0) want = (int)Math.Min(want, seg.End - seg.Position + 1);
            if (want <= 0) return;
            int n;
            try { n = await body.ReadAsync(buf.AsMemory(0, want), readTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Connection timed out"); }
            if (n == 0)
            {
                if (seg.End >= 0 && !seg.Finished) throw new IOException("Connection has been closed by the server");
                return;
            }
            await global.WaitAsync(n, ct);
            await own.WaitAsync(n, ct);
            int write;
            lock (seg.Gate)
            {
                // the segment may have been split meanwhile: never write past its (new) end
                write = seg.End >= 0 ? (int)Math.Min(n, seg.End - seg.Position + 1) : n;
                if (write <= 0) return;
                file.Write(buf, 0, write);
                seg.Done += write;
            }
            item.AddDownloaded(write);
            if (write < n) return;
        }
    }

    async Task AssembleAsync()
    {
        item.Status = DownloadStatus.Assembling;
        Directory.CreateDirectory(item.SaveDir);
        var target = item.FullPath;
        if (File.Exists(target))
        {
            if (settings.Duplicates == DuplicateAction.Overwrite) File.Delete(target);
            else target = Net.UniquePath(target);
        }
        var tmp = target + ".rehad";
        await using (var outFile = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            foreach (var seg in item.Segments.OrderBy(s => s.Start))
            {
                var p = PartPath(seg);
                if (!File.Exists(p)) { if (seg.Done == 0 && seg.End < seg.Start) continue; throw new IOException($"Missing part {seg.Index}"); }
                await using var part = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
                long len = seg.End >= 0 ? Math.Min(part.Length, seg.End - seg.Start + 1) : part.Length;
                await CopyAsync(part, outFile, len);
            }
        }
        if (item.Size >= 0 && new FileInfo(tmp).Length != item.Size)
            throw new IOException("An error has occurred during assembling the file (size mismatch)");
        File.Move(tmp, target, true);
        item.FileName = Path.GetFileName(target);
        try { Directory.Delete(tempDir, true); } catch { }
        item.Segments.Clear();
        item.Downloaded = item.Size >= 0 ? item.Size : new FileInfo(target).Length;
        if (item.Size < 0) item.Size = item.Downloaded;
    }

    async Task CopyAsync(Stream from, Stream to, long len)
    {
        var buf = new byte[1 << 20];
        while (len > 0)
        {
            int n = await from.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, len)), ct);
            if (n == 0) break;
            await to.WriteAsync(buf.AsMemory(0, n), ct);
            len -= n;
        }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}

public sealed class FatalDownloadException(string message) : Exception(message);
