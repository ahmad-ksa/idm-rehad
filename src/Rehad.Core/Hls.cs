using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Rehad.Core;

public sealed class HlsVariant
{
    public string Url { get; set; } = "";
    public long Bandwidth { get; set; }
    public string Resolution { get; set; } = "";
    public string? AudioGroup { get; set; }
    public override string ToString() => Resolution.Length > 0 ? $"{Resolution} ({Bandwidth / 1000} kbps)" : $"{Bandwidth / 1000} kbps";
}

public sealed class HlsSegment
{
    public string Url { get; set; } = "";
    public double Duration { get; set; }
    public long Sequence { get; set; }
    public string? KeyMethod { get; set; }
    public string? KeyUrl { get; set; }
    public byte[]? Iv { get; set; }
    public long ByteStart { get; set; } = -1;
    public long ByteLength { get; set; } = -1;
}

public sealed class HlsPlaylist
{
    public List<HlsVariant> Variants { get; } = new();
    public Dictionary<string, string> AudioGroups { get; } = new();
    public List<HlsSegment> Segments { get; } = new();
    public string? InitUrl { get; set; }
    public bool IsMaster => Variants.Count > 0;
}

/// <summary>Parser for Apple HLS (.m3u8) playlists: master (variants) and media (segments, AES-128 keys, byte ranges, init map).</summary>
public static class M3U
{
    static readonly Regex attr = new("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.Compiled);

    static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in attr.Matches(s)) d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    static string Resolve(string baseUrl, string rel) => new Uri(new Uri(baseUrl), rel).ToString();

    public static HlsPlaylist Parse(string text, string baseUrl)
    {
        var pl = new HlsPlaylist();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        HlsVariant? pendingVariant = null;
        double duration = 0;
        long seq = 0, nextByte = 0;
        string? keyMethod = null, keyUrl = null; byte[]? keyIv = null;
        long brLen = -1, brStart = -1;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var a = Attrs(line[18..]);
                pendingVariant = new HlsVariant
                {
                    Bandwidth = long.TryParse(a.GetValueOrDefault("BANDWIDTH"), out var bw) ? bw : 0,
                    Resolution = a.GetValueOrDefault("RESOLUTION") ?? "",
                    AudioGroup = a.GetValueOrDefault("AUDIO"),
                };
            }
            else if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attrs(line[13..]);
                if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.TryGetValue("URI", out var uri) && a.TryGetValue("GROUP-ID", out var gid) && !pl.AudioGroups.ContainsKey(gid))
                    pl.AudioGroups[gid] = Resolve(baseUrl, uri);
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(line[22..], out seq);
            else if (line.StartsWith("#EXTINF:"))
                double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var a = Attrs(line[11..]);
                keyMethod = a.GetValueOrDefault("METHOD");
                keyUrl = a.TryGetValue("URI", out var u) ? Resolve(baseUrl, u) : null;
                keyIv = a.TryGetValue("IV", out var iv) ? Convert.FromHexString(iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? iv[2..] : iv) : null;
                if (keyMethod == "NONE") { keyMethod = null; keyUrl = null; keyIv = null; }
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = Attrs(line[11..]);
                if (a.TryGetValue("URI", out var u)) pl.InitUrl = Resolve(baseUrl, u);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var parts = line[17..].Split('@');
                brLen = long.Parse(parts[0], CultureInfo.InvariantCulture);
                brStart = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : nextByte;
            }
            else if (!line.StartsWith('#'))
            {
                var url = Resolve(baseUrl, line);
                if (pendingVariant != null) { pendingVariant.Url = url; pl.Variants.Add(pendingVariant); pendingVariant = null; continue; }
                var seg = new HlsSegment { Url = url, Duration = duration, Sequence = seq++, KeyMethod = keyMethod, KeyUrl = keyUrl, Iv = keyIv };
                if (brLen >= 0) { seg.ByteStart = brStart; seg.ByteLength = brLen; nextByte = brStart + brLen; brLen = -1; }
                pl.Segments.Add(seg);
            }
        }
        return pl;
    }
}

/// <summary>Downloads an HLS stream: segments in parallel → decrypt → join → remux to MP4 with ffmpeg (if present).</summary>
public sealed class HlsDownloader
{
    readonly DownloadItem item;
    readonly Settings settings;
    readonly SpeedLimiter global;

    public HlsDownloader(DownloadItem item, Settings settings, SpeedLimiter global)
    { this.item = item; this.settings = settings; this.global = global; }

    async Task<string> GetText(string url, CancellationToken ct)
    {
        using var req = Net.Request(url, item, settings);
        using var resp = await Net.Client(settings).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    async Task<byte[]> GetBytes(string url, long start, long len, CancellationToken ct)
    {
        using var req = Net.Request(url, item, settings, start, start >= 0 && len > 0 ? start + len - 1 : -1);
        using var resp = await Net.Client(settings).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        await global.WaitAsync(data.Length, ct);
        return data;
    }

    public static async Task<List<HlsVariant>> VariantsAsync(DownloadItem item, Settings s, CancellationToken ct)
    {
        var d = new HlsDownloader(item, s, new SpeedLimiter());
        var pl = M3U.Parse(await d.GetText(item.Url, ct), item.Url);
        return pl.Variants.OrderByDescending(v => v.Bandwidth).ToList();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        var tempDir = SegmentedDownloader.TempDirFor(settings, item);
        Directory.CreateDirectory(tempDir);

        var pl = M3U.Parse(await GetText(item.Url, ct), item.Url);
        string? audioUrl = null;
        if (pl.IsMaster)
        {
            var v = pl.Variants.FirstOrDefault(x => x.Bandwidth.ToString() == item.Format)
                    ?? pl.Variants.OrderByDescending(x => x.Bandwidth).First();
            if (v.AudioGroup != null && pl.AudioGroups.TryGetValue(v.AudioGroup, out var a)) audioUrl = a;
            pl = M3U.Parse(await GetText(v.Url, ct), v.Url);
        }
        HlsPlaylist? audio = audioUrl != null ? M3U.Parse(await GetText(audioUrl, ct), audioUrl) : null;

        var video = await FetchTrack(pl, Path.Combine(tempDir, "v"), "video.ts", ct);
        string? audioFile = audio != null ? await FetchTrack(audio, Path.Combine(tempDir, "a"), "audio.ts", ct) : null;

        item.Status = DownloadStatus.Assembling;
        Directory.CreateDirectory(item.SaveDir);
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var ffmpeg = Tools.Find("ffmpeg");
        string target;
        if (ffmpeg != null)
        {
            target = Net.UniquePath(Path.Combine(item.SaveDir, stem + ".mp4"));
            var args = audioFile != null
                ? $"-y -i \"{video}\" -i \"{audioFile}\" -map 0:v? -map 1:a? -c copy -bsf:a aac_adtstoasc \"{target}\""
                : $"-y -i \"{video}\" -c copy -bsf:a aac_adtstoasc \"{target}\"";
            var code = await Tools.RunAsync(ffmpeg, args, null, ct);
            if (code != 0 || !File.Exists(target))
            {
                target = Net.UniquePath(Path.Combine(item.SaveDir, stem + ".ts"));
                File.Copy(video, target);
            }
        }
        else
        {
            target = Net.UniquePath(Path.Combine(item.SaveDir, stem + ".ts"));
            File.Copy(video, target);
        }
        item.FileName = Path.GetFileName(target);
        item.Size = item.Downloaded = new FileInfo(target).Length;
        try { Directory.Delete(tempDir, true); } catch { }
    }

    async Task<string> FetchTrack(HlsPlaylist pl, string dir, string outName, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var segs = pl.Segments;
        if (segs.Count == 0) throw new IOException("The playlist has no segments");
        var keys = new Dictionary<string, byte[]>();
        var existing = Directory.GetFiles(dir, "*.seg");
        int done = existing.Length;
        if (done > 0) item.Downloaded = existing.Sum(f => new FileInfo(f).Length);
        double total = segs.Count;
        item.Status = DownloadStatus.Downloading;
        item.Resumable = true;

        int parallel = Math.Clamp(item.MaxConnections > 0 ? item.MaxConnections : settings.MaxConnections, 1, 16);
        item.ActiveConnections = parallel;
        await Parallel.ForEachAsync(Enumerable.Range(0, segs.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (i, token) =>
        {
            var path = Path.Combine(dir, $"{i:D6}.seg");
            if (File.Exists(path)) return;
            var s = segs[i];
            byte[]? data = null;
            for (int attempt = 0; ; attempt++)
            {
                try { data = await GetBytes(s.Url, s.ByteStart, s.ByteLength, token); break; }
                catch (Exception) when (attempt < settings.Retries && !token.IsCancellationRequested) { await Task.Delay(1000 * Math.Min(5, attempt + 1), token); }
            }
            if (s.KeyMethod == "AES-128" && s.KeyUrl != null)
            {
                byte[] key;
                lock (keys) keys.TryGetValue(s.KeyUrl, out key!);
                if (key == null) { key = await GetBytes(s.KeyUrl, -1, -1, token); lock (keys) keys[s.KeyUrl] = key; }
                var iv = s.Iv ?? SequenceIv(s.Sequence);
                using var aes = Aes.Create();
                aes.Key = key;
                data = aes.DecryptCbc(data!, iv, PaddingMode.PKCS7);
            }
            await File.WriteAllBytesAsync(path + ".part", data!, token);
            File.Move(path + ".part", path, true);
            int d = Interlocked.Increment(ref done);
            item.AddDownloaded(data!.Length);
            // estimate the final size from the average segment size
            item.Size = (long)(item.Downloaded / (double)d * total);
        });

        var output = Path.Combine(dir, outName);
        await using var outFile = File.Create(output);
        if (pl.InitUrl != null)
        {
            var init = await GetBytes(pl.InitUrl, -1, -1, ct);
            await outFile.WriteAsync(init, ct);
        }
        for (int i = 0; i < segs.Count; i++)
        {
            await using var f = File.OpenRead(Path.Combine(dir, $"{i:D6}.seg"));
            await f.CopyToAsync(outFile, ct);
        }
        return output;
    }

    static byte[] SequenceIv(long seq)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--) { iv[i] = (byte)(seq & 0xFF); seq >>= 8; }
        return iv;
    }
}
