using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rehad.Core;

public static class Links
{
    static readonly Regex urlRx = new(@"(?:https?|ftp)://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>All web addresses in a piece of text (clipboard).</summary>
    public static List<string> Extract(string? text) =>
        string.IsNullOrEmpty(text) ? new() : urlRx.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ']')).Distinct().ToList();

    /// <summary>Batch download: "http://site/img*.jpg" from 1 to 20 with wildcard size 3 → img001.jpg … img020.jpg.
    /// Letters are allowed too (from a to z).</summary>
    public static List<string> Batch(string pattern, string from, string to, int wildcardSize)
    {
        var list = new List<string>();
        int star = pattern.IndexOf('*');
        if (star < 0) return list;
        string Build(string v) => pattern[..star] + v + pattern[(star + 1)..];
        if (int.TryParse(from, out var a) && int.TryParse(to, out var b))
        {
            if (b < a) (a, b) = (b, a);
            for (int i = a; i <= b && list.Count < 100000; i++) list.Add(Build(i.ToString().PadLeft(Math.Max(1, wildcardSize), '0')));
        }
        else if (from.Length == 1 && to.Length == 1 && char.IsLetter(from[0]) && char.IsLetter(to[0]))
        {
            char x = from[0], y = to[0];
            if (y < x) (x, y) = (y, x);
            for (char c = x; c <= y; c++) list.Add(Build(c.ToString()));
        }
        return list;
    }

    static readonly Regex hrefRx = new(@"(?:href|src)\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Links on one HTML page (absolute).</summary>
    public static IEnumerable<string> FromHtml(string html, string baseUrl)
    {
        foreach (Match m in hrefRx.Matches(html))
        {
            var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || v.StartsWith('#')) continue;
            if (Uri.TryCreate(new Uri(baseUrl), v, out var u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "ftp"))
                yield return u.GetLeftPart(UriPartial.Query);
        }
    }
}

public sealed class GrabberOptions
{
    public string StartUrl { get; set; } = "";
    public int Depth { get; set; } = 1;
    public bool SameSiteOnly { get; set; } = true;
    public string FileTypes { get; set; } = "jpg jpeg png gif zip rar 7z pdf mp3 mp4 exe";
    public int Parallel { get; set; } = 4;
}

/// <summary>Site grabber: explores pages up to N levels and returns the files that match the filters.</summary>
public static class Grabber
{
    public static async Task<List<string>> ExploreAsync(GrabberOptions o, Settings s, IProgress<(int pages, int files)>? progress, CancellationToken ct)
    {
        var start = new Uri(o.StartUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { o.StartUrl };
        var files = new List<string>();
        var types = o.FileTypes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('.').ToLowerInvariant()).ToHashSet();
        var level = new List<string> { o.StartUrl };
        int pages = 0;
        for (int depth = 0; depth <= o.Depth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            await System.Threading.Tasks.Parallel.ForEachAsync(level, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(o.Parallel, 1, 10), CancellationToken = ct }, async (url, token) =>
            {
                string html;
                try
                {
                    using var req = Net.Request(url, null, s);
                    using var resp = await Net.Client(s).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType?.Contains("html") != true) return;
                    html = await resp.Content.ReadAsStringAsync(token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { return; }
                Interlocked.Increment(ref pages);
                foreach (var link in Links.FromHtml(html, url))
                {
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) continue;
                    if (o.SameSiteOnly && !u.Host.EndsWith(start.Host.Replace("www.", ""), StringComparison.OrdinalIgnoreCase)) continue;
                    lock (seen) { if (!seen.Add(link)) continue; }
                    var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.').ToLowerInvariant();
                    if (types.Contains(ext)) lock (files) files.Add(link);
                    else if (ext is "" or "html" or "htm" or "php" or "asp" or "aspx" or "jsp") lock (next) next.Add(link);
                }
                progress?.Report((pages, files.Count));
            });
            level = next;
        }
        return files;
    }
}

/// <summary>
/// Local channel used by the browser helper (RehadHost.exe) and by a second app instance:
/// one JSON object per connection on the named pipe "RehadPipe", answered with a JSON line.
/// </summary>
public sealed class PipeServer
{
    public const string Name = "RehadPipe";
    public event Func<JsonElement, string>? Message;
    CancellationTokenSource? cts;

    public void Start()
    {
        cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(cts.Token));
    }

    public void Stop() => cts?.Cancel();

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                var p = pipe; pipe = null;
                _ = Task.Run(() => Handle(p, ct));
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, CancellationToken.None); }
            finally { pipe?.Dispose(); }
        }
    }

    async Task Handle(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                var line = await reader.ReadLineAsync(ct);
                string reply = "{\"ok\":false}";
                if (!string.IsNullOrEmpty(line))
                {
                    using var doc = JsonDocument.Parse(line);
                    reply = Message?.Invoke(doc.RootElement.Clone()) ?? "{\"ok\":true}";
                }
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
            }
            catch { }
        }
    }

    /// <summary>Sends one message to the running app; returns the reply, or null when the app is not running.</summary>
    public static async Task<string?> SendAsync(string json, int timeoutMs = 3000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(json.Replace("\r", "").Replace("\n", " ") + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return await reader.ReadLineAsync();
        }
        catch { return null; }
    }
}
