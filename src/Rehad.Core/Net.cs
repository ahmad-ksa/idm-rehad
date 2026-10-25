using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Rehad.Core;

/// <summary>Token bucket shared by all connections (global) or one download.</summary>
public sealed class SpeedLimiter
{
    readonly object gate = new();
    double tokens;
    DateTime last = DateTime.UtcNow;

    /// <summary>Bytes per second; 0 = unlimited.</summary>
    public long Rate { get; set; }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            long rate = Rate;
            if (rate <= 0) return;
            TimeSpan wait;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                tokens = Math.Min(rate, tokens + (now - last).TotalSeconds * rate);
                last = now;
                if (tokens >= bytes || tokens >= rate) { tokens -= bytes; return; }
                wait = TimeSpan.FromSeconds((bytes - tokens) / rate);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait, ct);
        }
    }
}

public sealed record ProbeResult(long Size, string FileName, bool Resumable, string? ContentType, string FinalUrl, int Status);

public static class Net
{
    static HttpClient? client;
    static string clientKey = "";

    /// <summary>One shared HttpClient per proxy configuration (connections are pooled).</summary>
    public static HttpClient Client(Settings s)
    {
        var key = $"{s.Proxy}|{s.HttpProxy}|{s.UseSocks}|{s.SocksProxy}|{s.ProxyUser}|{s.TimeoutSeconds}";
        if (client != null && key == clientKey) return client;
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, s.TimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        switch (s.Proxy)
        {
            case ProxyMode.None: h.UseProxy = false; break;
            case ProxyMode.System: h.UseProxy = true; h.Proxy = WebRequest.DefaultWebProxy; break;
            case ProxyMode.Manual:
                var addr = s.UseSocks && s.SocksProxy.Length > 0 ? "socks5://" + s.SocksProxy : "http://" + s.HttpProxy;
                var proxy = new WebProxy(addr)
                {
                    BypassProxyOnLocal = true,
                    BypassList = s.ProxyBypass.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => System.Text.RegularExpressions.Regex.Escape(b).Replace("\\*", ".*")).ToArray(),
                };
                if (s.ProxyUser.Length > 0) proxy.Credentials = new NetworkCredential(s.ProxyUser, s.ProxyPassword);
                h.UseProxy = true; h.Proxy = proxy;
                break;
        }
        client = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        clientKey = key;
        return client;
    }

    public static HttpRequestMessage Request(string url, DownloadItem? item, Settings s, long from = -1, long to = -1)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", item?.UserAgent ?? s.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrEmpty(item?.Referer)) req.Headers.TryAddWithoutValidation("Referer", item!.Referer);
        if (!string.IsNullOrEmpty(item?.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", item!.Cookies);
        if (item != null)
            foreach (var kv in item.Headers)
                if (!kv.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        var user = item?.Login; var pass = item?.Password;
        if (string.IsNullOrEmpty(user) && s.LoginFor(url) is { } l) { user = l.User; pass = l.Password; }
        if (!string.IsNullOrEmpty(user))
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        if (from >= 0) req.Headers.Range = new RangeHeaderValue(from, to >= 0 ? to : null);
        return req;
    }

    /// <summary>Asks the server for size, name and resume support (what the "Download File Info" dialog shows).</summary>
    public static async Task<ProbeResult> ProbeAsync(DownloadItem item, Settings s, CancellationToken ct)
    {
        using var req = Request(item.Url, item, s, 0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, s.TimeoutSeconds)));
        using var resp = await Client(s).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return Describe(resp, item.Url);
    }

    public static ProbeResult Describe(HttpResponseMessage resp, string originalUrl)
    {
        long size = -1; bool resumable = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            resumable = true;
            size = resp.Content.Headers.ContentRange?.Length ?? -1;
        }
        else if (resp.IsSuccessStatusCode) size = resp.Content.Headers.ContentLength ?? -1;
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        var name = FileNameFrom(resp.Content.Headers.ContentDisposition, final, resp.Content.Headers.ContentType?.MediaType);
        return new ProbeResult(size, name, resumable, resp.Content.Headers.ContentType?.MediaType, final, (int)resp.StatusCode);
    }

    public static string FileNameFrom(ContentDispositionHeaderValue? cd, string url, string? mime)
    {
        string? name = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(name)) return Sanitize(name.Trim('"'));
        return FileNameFromUrl(url, mime);
    }

    public static string FileNameFromUrl(string url, string? mime = null)
    {
        string name = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            name = Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/'));
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Length == 0) name = u.Host;
        }
        if (name.Length == 0) name = "download";
        if (!Path.HasExtension(name) && mime != null && ExtFromMime(mime) is { } ext) name += ext;
        return Sanitize(name);
    }

    static string? ExtFromMime(string mime) => mime.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4", "video/webm" => ".webm", "video/x-flv" => ".flv", "video/mp2t" => ".ts",
        "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/webm" => ".weba", "audio/ogg" => ".ogg",
        "application/zip" => ".zip", "application/pdf" => ".pdf", "application/x-msdownload" => ".exe",
        "application/vnd.android.package-archive" => ".apk", "image/jpeg" => ".jpg", "image/png" => ".png",
        "text/html" => ".html", "application/x-rar-compressed" or "application/vnd.rar" => ".rar",
        _ => null,
    };

    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(bad.Contains(ch) || ch < 32 ? '_' : ch);
        var r = sb.ToString().Trim().TrimEnd('.');
        if (r.Length > 180) r = r[..(180 - Path.GetExtension(r).Length)] + Path.GetExtension(r);
        return r.Length == 0 ? "download" : r;
    }

    /// <summary>"name.ext" → "name(2).ext" until the path is free.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
