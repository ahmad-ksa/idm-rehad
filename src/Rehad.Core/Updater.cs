using System.Text.Json;

namespace Rehad.Core;

public sealed record AppRelease(Version Version, string Tag, string SetupUrl, string SetupName, long Size, string Notes);

/// <summary>Checks GitHub for a newer Rehad release and keeps yt-dlp fresh.</summary>
public static class Updater
{
    public const string Repo = "ahmad-ksa/idm-rehad";

    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    /// <summary>Returns the latest release when it is newer than this app, otherwise null.</summary>
    public static async Task<AppRelease?> CheckAsync(Settings s, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.TryAddWithoutValidation("User-Agent", "Rehad-Updater");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var resp = await Net.Client(s).SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        if (v <= Current) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("Rehad-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new AppRelease(v, tag, a.GetProperty("browser_download_url").GetString() ?? "", name,
                    a.TryGetProperty("size", out var sz) ? sz.GetInt64() : -1,
                    root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }
        return null;
    }

    /// <summary>Updates yt-dlp at most once a week (YouTube changes often and old versions stop working).</summary>
    public static async Task<string?> UpdateYtDlpAsync(Settings s, bool force, CancellationToken ct)
    {
        if (!force && DateTime.Now - s.LastYtDlpUpdate < TimeSpan.FromDays(7)) return null;
        var exe = YtDlp.Exe;
        if (exe == null) return null;
        var log = new System.Text.StringBuilder();
        int code;
        try { code = await Tools.RunAsync(exe, "-U", l => log.AppendLine(l), ct); }
        catch (Exception ex) { return ex.Message; }
        if (code == 0) s.LastYtDlpUpdate = DateTime.Now;
        return log.ToString().Trim();
    }
}
