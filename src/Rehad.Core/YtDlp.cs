using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rehad.Core;

/// <summary>Finds bundled tools (yt-dlp, ffmpeg) and runs processes.</summary>
public static class Tools
{
    public static string AppDir { get; set; } = AppContext.BaseDirectory;

    public static string? Find(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in new[] { AppDir, Path.Combine(AppDir, "tools"), Path.Combine(Store.DataDir, "tools") })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir, exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public static async Task<int> RunAsync(string exe, string args, Action<string>? onLine, CancellationToken ct, StringBuilder? stdout = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; stdout?.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}

public sealed class MediaFormat
{
    public string Id { get; set; } = "";
    public string Ext { get; set; } = "";
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public long Size { get; set; }
    public double Bitrate { get; set; }
    public string Note { get; set; } = "";

    public string Label
    {
        get
        {
            var size = Size > 0 ? " — " + Fmt.Bytes(Size) : "";
            if (HasVideo) return $"{Height}p{(Fps > 30 ? ((int)Fps).ToString() : "")} {Ext.ToUpperInvariant()}{(HasAudio ? "" : "")}{size}";
            return $"Audio {(Bitrate > 0 ? (int)Bitrate + " kbps " : "")}{Ext.ToUpperInvariant()}{size}";
        }
    }
}

public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public double Duration { get; set; }
    public List<MediaFormat> Choices { get; } = new();
}

/// <summary>Wraps yt-dlp for sites (YouTube, TikTok, Instagram, Facebook, X, Vimeo…): list qualities and download with merge.</summary>
public static class YtDlp
{
    public static string? Exe => Tools.Find("yt-dlp");

    static string CommonArgs(DownloadItem item, Settings s)
    {
        var sb = new StringBuilder("--no-playlist --no-warnings --no-mtime ");
        sb.Append($"--user-agent \"{(item.UserAgent ?? s.UserAgent).Replace("\"", "")}\" ");
        if (!string.IsNullOrEmpty(item.Referer)) sb.Append($"--referer \"{item.Referer}\" ");
        var cookies = CookieFile(item);
        if (cookies != null) sb.Append($"--cookies \"{cookies}\" ");
        if (s.Proxy == ProxyMode.Manual)
            sb.Append(s.UseSocks && s.SocksProxy.Length > 0 ? $"--proxy socks5://{s.SocksProxy} " : s.HttpProxy.Length > 0 ? $"--proxy http://{s.HttpProxy} " : "");
        if (Tools.Find("ffmpeg") is { } ff) sb.Append($"--ffmpeg-location \"{Path.GetDirectoryName(ff)}\" ");
        // YouTube needs a JavaScript runtime to unlock its formats; Deno is shipped next to yt-dlp
        if (Tools.Find("deno") is { } deno) sb.Append($"--js-runtimes \"deno:{deno}\" ");
        return sb.ToString();
    }

    /// <summary>Writes the cookies sent by the browser extension as a Netscape cookie file for yt-dlp.</summary>
    static string? CookieFile(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.Cookies) || !Uri.TryCreate(item.PageUrl ?? item.Url, UriKind.Absolute, out var u)) return null;
        var host = u.Host;
        var parts = host.Split('.');
        var domain = parts.Length >= 2 ? "." + string.Join('.', parts[^2..]) : host;
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in item.Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = c.IndexOf('=');
            if (eq <= 0) continue;
            sb.Append($"{domain}\tTRUE\t/\tTRUE\t0\t{c[..eq]}\t{c[(eq + 1)..]}\n");
        }
        Directory.CreateDirectory(Store.DataDir);
        var path = Path.Combine(Store.DataDir, $"cookies_{item.Id}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static async Task<MediaInfo> InfoAsync(DownloadItem item, Settings s, CancellationToken ct)
    {
        var exe = Exe ?? throw new FileNotFoundException("yt-dlp.exe is missing");
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var code = await Tools.RunAsync(exe, $"-J {CommonArgs(item, s)} \"{item.PageUrl ?? item.Url}\"",
            line => { if (line.StartsWith("ERROR")) errors.AppendLine(line); }, ct, output);
        var text = output.ToString();
        int start = text.IndexOf('{');
        if (code != 0 || start < 0) throw new IOException(errors.Length > 0 ? FirstLines(errors.ToString(), 3) : "This page has no downloadable video");
        using var doc = JsonDocument.Parse(text[start..]);
        var root = doc.RootElement;
        var info = new MediaInfo
        {
            Title = Str(root, "title") ?? "video",
            Thumbnail = Str(root, "thumbnail"),
            Duration = Num(root, "duration"),
        };
        var formats = new List<MediaFormat>();
        if (root.TryGetProperty("formats", out var fs))
            foreach (var f in fs.EnumerateArray())
            {
                var vc = Str(f, "vcodec") ?? "none"; var ac = Str(f, "acodec") ?? "none";
                if (Str(f, "protocol") is "mhtml") continue;
                formats.Add(new MediaFormat
                {
                    Id = Str(f, "format_id") ?? "",
                    Ext = Str(f, "ext") ?? "",
                    Height = (int)Num(f, "height"),
                    Fps = Num(f, "fps"),
                    HasVideo = vc != "none",
                    HasAudio = ac != "none",
                    Size = (long)(Num(f, "filesize") > 0 ? Num(f, "filesize") : Num(f, "filesize_approx")),
                    Bitrate = Num(f, "abr") > 0 ? Num(f, "abr") : Num(f, "tbr"),
                    Note = Str(f, "format_note") ?? "",
                });
            }
        var bestAudio = formats.Where(f => f.HasAudio && !f.HasVideo).OrderByDescending(f => f.Ext == "m4a").ThenByDescending(f => f.Bitrate).FirstOrDefault();
        // one entry per resolution: best video of that height (+ best audio when needed)
        foreach (var g in formats.Where(f => f.HasVideo && f.Height > 0).GroupBy(f => f.Height).OrderByDescending(g => g.Key))
        {
            var v = g.OrderByDescending(f => f.Ext == "mp4").ThenByDescending(f => f.Bitrate).First();
            var choice = new MediaFormat
            {
                Id = $"bv*[height<={v.Height}]+ba/b[height<={v.Height}]/bv*+ba/b",
                Ext = "mp4", Height = v.Height, Fps = v.Fps, HasVideo = true, HasAudio = true,
                Size = v.Size + (v.HasAudio ? 0 : bestAudio?.Size ?? 0),
            };
            info.Choices.Add(choice);
        }
        if (info.Choices.Count == 0 && formats.Count > 0)
            info.Choices.Add(new MediaFormat { Id = "bv*+ba/b", Ext = Str(root, "ext") ?? "mp4", HasVideo = true, HasAudio = true, Height = (int)Num(root, "height") });
        if (bestAudio != null)
            info.Choices.Add(new MediaFormat { Id = "bestaudio[ext=m4a]/bestaudio/b", Ext = "m4a", HasAudio = true, Bitrate = bestAudio.Bitrate, Size = bestAudio.Size });
        return info;
    }

    static readonly Regex progress = new(@"\[download\]\s+([\d.]+)% of\s+~?\s*([\d.]+)(\w+)(?: at\s+([\d.]+)(\w+)/s)?(?: ETA ([\d:]+))?", RegexOptions.Compiled);

    public static async Task DownloadAsync(DownloadItem item, Settings s, CancellationToken ct)
    {
        try { await DownloadOnceAsync(item, s, ct); }
        catch (IOException ex) when (!ct.IsCancellationRequested &&
            (ex.Message.Contains("format is not available", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)))
        {
            var fmt = item.Format; var cookies = item.Cookies;
            item.Format = item.Format?.StartsWith("bestaudio") == true ? "bestaudio/b" : "bv*+ba/b";
            if (ex.Message.Contains("format is not available", StringComparison.OrdinalIgnoreCase)) item.Cookies = null;
            try { await DownloadOnceAsync(item, s, ct); }
            finally { item.Format = fmt; item.Cookies = cookies; }
        }
    }

    static async Task DownloadOnceAsync(DownloadItem item, Settings s, CancellationToken ct)
    {
        var exe = Exe ?? throw new FileNotFoundException("yt-dlp.exe is missing");
        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        Directory.CreateDirectory(item.SaveDir);
        // short, emoji-free name: long titles break Windows' 260-character path limit
        var stem = SafeStem(Path.GetFileNameWithoutExtension(item.FileName));
        if (string.IsNullOrWhiteSpace(stem)) stem = "%(title)s";
        var fmt = string.IsNullOrEmpty(item.Format) ? "bv*+ba/b" : item.Format;
        var audioOnly = fmt.StartsWith("bestaudio");
        var tmpDir = SegmentedDownloader.TempDirFor(s, item);
        Directory.CreateDirectory(tmpDir);
        int conns = Math.Clamp(item.MaxConnections > 0 ? item.MaxConnections : s.MaxConnections, 1, 16);
        var args = new StringBuilder(CommonArgs(item, s));
        args.Append($"-f \"{fmt}\" -N {conns} --newline --progress --no-quiet --continue --windows-filenames --trim-filenames 100 ");
        args.Append($"--paths \"temp:{tmpDir}\" --paths \"home:{item.SaveDir}\" -o \"{stem.Replace("\"", "")}.%(ext)s\" ");
        if (!audioOnly) args.Append("--merge-output-format mp4 ");
        if (s.SpeedLimitOn && s.SpeedLimitKBps > 0) args.Append($"--limit-rate {s.SpeedLimitKBps}K ");
        args.Append("--print after_move:filepath ");
        args.Append($"\"{item.PageUrl ?? item.Url}\"");

        string? finalPath = null;
        var errors = new StringBuilder();
        long partBase = 0; double lastPct = 0;
        var code = await Tools.RunAsync(exe, args.ToString(), line =>
        {
            var m = progress.Match(line);
            if (m.Success)
            {
                item.Status = DownloadStatus.Downloading;
                double pct = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                long total = ToBytes(m.Groups[2].Value, m.Groups[3].Value);
                // video and audio are downloaded one after the other: keep the counters growing
                if (pct + 1 < lastPct) partBase = item.Downloaded;
                lastPct = pct;
                item.Size = partBase + total;
                item.Downloaded = partBase + (long)(total * pct / 100);
                if (m.Groups[4].Success) item.Speed = ToBytes(m.Groups[4].Value, m.Groups[5].Value);
            }
            else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[FixupM3u8]")) item.Status = DownloadStatus.Assembling;
            else if (line.StartsWith("ERROR") && !errors.ToString().Contains(line)) errors.AppendLine(line);
            else if (!line.StartsWith('[') && line.Length > 3 && (line.Contains(Path.DirectorySeparatorChar) || line.Contains('/')) && File.Exists(line.Trim())) finalPath = line.Trim();
        }, ct);
        if (code != 0 || finalPath == null)
            throw new IOException(errors.Length > 0 ? FirstLines(errors.ToString(), 3) : $"yt-dlp failed (code {code})");
        item.FileName = Path.GetFileName(finalPath);
        item.SaveDir = Path.GetDirectoryName(finalPath)!;
        item.Size = item.Downloaded = new FileInfo(finalPath).Length;
        try { Directory.Delete(tmpDir, true); } catch { }
        try { File.Delete(Path.Combine(Store.DataDir, $"cookies_{item.Id}.txt")); } catch { }
    }

    static string FirstLines(string text, int n) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(n));

    /// <summary>Removes emoji/symbols and limits the length of a title used as a file name.</summary>
    public static string SafeStem(string stem)
    {
        var sb = new StringBuilder(stem.Length);
        foreach (var rune in stem.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark && rune.Value > 0xFE00) continue;
            sb.Append(rune.ToString());
        }
        var r = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"[\s_]{2,}", " ").Trim(' ', '_', '-', '.');
        r = Net.ShortName(Net.Sanitize(r.Length == 0 ? "video" : r), 80);
        return r.Replace("%", "%%");
    }

    static long ToBytes(string num, string unit)
    {
        double v = double.Parse(num, CultureInfo.InvariantCulture);
        return (long)(v * unit.ToUpperInvariant() switch
        {
            "KIB" or "KB" => 1024d, "MIB" or "MB" => 1024d * 1024, "GIB" or "GB" => 1024d * 1024 * 1024, "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        });
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Pages that need yt-dlp instead of a direct download.</summary>
    public static bool IsSitePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        string[] sites = { "youtube.com", "youtu.be", "tiktok.com", "instagram.com", "facebook.com", "fb.watch", "twitter.com", "x.com",
            "vimeo.com", "dailymotion.com", "twitch.tv", "reddit.com", "soundcloud.com", "snapchat.com", "pinterest.com", "bilibili.com", "ok.ru", "vk.com", "threads.net", "linkedin.com" };
        return sites.Any(s => h == s || h.EndsWith("." + s));
    }
}
