using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rehad.Core;

public enum ProxyMode { None, System, Manual }
public enum DuplicateAction { Ask, Number, Overwrite, Skip }
public enum CompletionAction { None, Exit, Shutdown, Sleep }

public sealed class CategoryInfo
{
    public string Name { get; set; } = "";
    public string Extensions { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>Optional list of sites; files from other sites never go to this category.</summary>
    public string Sites { get; set; } = "";

    public bool Matches(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return false;
        foreach (var e in Extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Wildcard.Match(e.ToLowerInvariant(), ext)) return true;
        return false;
    }
}

public sealed class SiteLogin
{
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class QueueInfo
{
    public string Name { get; set; } = "";
    public int Simultaneous { get; set; } = 1;
    public bool StartEnabled { get; set; }
    public TimeSpan StartAt { get; set; } = new(2, 0, 0);
    public bool StopEnabled { get; set; }
    public TimeSpan StopAt { get; set; } = new(7, 0, 0);
    public bool Daily { get; set; } = true;
    /// <summary>Bit 0 = Sunday … bit 6 = Saturday.</summary>
    public int Days { get; set; } = 0x7F;
    public int Retries { get; set; } = 10;
    public CompletionAction OnComplete { get; set; }
    public List<int> Items { get; set; } = new();

    [JsonIgnore] public bool Running { get; set; }
}

public sealed class Settings
{
    // General
    public string Language { get; set; } = "";
    public bool LaunchOnStartup { get; set; } = true;
    public bool MonitorClipboard { get; set; } = true;
    public bool CaptureFromBrowsers { get; set; } = true;
    public bool ShowVideoButton { get; set; } = true;
    public bool DarkMode { get; set; }

    // File types
    public string CaptureExtensions { get; set; } =
        "3GP 7Z AAC ACE AIF APK ARJ ASF AVI BIN BZ2 EXE GZ GZIP IMG ISO LZH M4A M4V MKV MOV MP3 MP4 MPA MPE MPEG MPG MSI MSU OGG OGV PDF PLJ PPS PPT QT R0* R1* RA RAR RM RMVB SEA SIT SITX TAR TIF TIFF WAV WEBM WMA WMV Z ZIP";
    public string ExceptionSites { get; set; } = "*.update.microsoft.com download.windowsupdate.com siteseal.thawte.com ecom.cimetz.com *.voice2page.com";
    public string ExceptionAddresses { get; set; } = "";
    public long MinVideoSizeKB { get; set; } = 300;

    // Save to
    public string DefaultFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string TempFolder { get; set; } = Path.Combine(Store.DataDir, "DwnlData");
    public List<CategoryInfo> Categories { get; set; } = DefaultCategories();

    // Downloads
    public bool ShowStartDialog { get; set; } = true;
    public bool ShowProgressDialog { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;
    public DuplicateAction Duplicates { get; set; } = DuplicateAction.Ask;
    public string VirusScanner { get; set; } = "";
    public string VirusScannerArgs { get; set; } = "";

    // Connection
    public int MaxConnections { get; set; } = 8;
    public int TimeoutSeconds { get; set; } = 30;
    public int Retries { get; set; } = 20;
    public Dictionary<string, int> ServerLimits { get; set; } = new();
    public bool DownloadLimitOn { get; set; }
    public int DownloadLimitMB { get; set; } = 200;
    public int DownloadLimitHours { get; set; } = 5;

    // Speed limiter
    public bool SpeedLimitOn { get; set; }
    public bool SpeedLimitAlwaysOnStart { get; set; }
    public int SpeedLimitKBps { get; set; } = 500;

    // Proxy
    public ProxyMode Proxy { get; set; } = ProxyMode.System;
    public string HttpProxy { get; set; } = "";
    public bool UseSocks { get; set; }
    public string SocksProxy { get; set; } = "";
    public string ProxyUser { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string ProxyBypass { get; set; } = "localhost 127.0.0.1";

    // Logins
    public List<SiteLogin> Logins { get; set; } = new();

    // Queues
    public List<QueueInfo> Queues { get; set; } = new()
    {
        new QueueInfo { Name = "Main download queue", Simultaneous = 1 },
        new QueueInfo { Name = "Synchronization queue", Simultaneous = 1 },
    };

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public static List<CategoryInfo> DefaultCategories()
    {
        string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return new()
        {
            new() { Name = "Compressed", Folder = Path.Combine(dl, "Compressed"), Extensions = "zip rar r0* r1* arj gz sit sitx sea ace bz2 7z xz tar" },
            new() { Name = "Documents", Folder = Path.Combine(dl, "Documents"), Extensions = "doc docx pdf pps ppt pptx xls xlsx odt txt rtf epub" },
            new() { Name = "Music", Folder = Path.Combine(dl, "Music"), Extensions = "mp3 wav wma mpa ram ra aac aif m4a tsa flac ogg opus" },
            new() { Name = "Programs", Folder = Path.Combine(dl, "Programs"), Extensions = "exe msi msu apk dmg iso img bin" },
            new() { Name = "Video", Folder = Path.Combine(dl, "Video"), Extensions = "avi mpg mpe mpeg asf wmv mov qt rm rmvb mp4 flv m4v webm ogv mkv ts 3gp" },
        };
    }

    public CategoryInfo? CategoryFor(string fileName) => Categories.FirstOrDefault(c => c.Matches(fileName));

    public string FolderFor(string category) =>
        Categories.FirstOrDefault(c => c.Name == category)?.Folder is { Length: > 0 } f ? f : DefaultFolder;

    public bool ShouldCapture(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return false;
        foreach (var e in CaptureExtensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Wildcard.Match(e.ToLowerInvariant(), ext)) return true;
        return false;
    }

    public bool IsExceptionSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        foreach (var s in ExceptionSites.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Wildcard.Match(s.ToLowerInvariant(), u.Host.ToLowerInvariant())) return true;
        foreach (var a in ExceptionAddresses.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Wildcard.Match(a, url)) return true;
        return false;
    }

    public int ConnectionsFor(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            foreach (var kv in ServerLimits)
                if (Wildcard.Match(kv.Key.ToLowerInvariant(), u.Host.ToLowerInvariant())) return Math.Clamp(kv.Value, 1, 32);
        return Math.Clamp(MaxConnections, 1, 32);
    }

    public SiteLogin? LoginFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var hostPath = u.Host + u.AbsolutePath;
        return Logins.Where(l => Wildcard.Match(l.Site.ToLowerInvariant().TrimEnd('/') + "*", hostPath.ToLowerInvariant()))
                     .OrderByDescending(l => l.Site.Length).FirstOrDefault();
    }
}

public static class Wildcard
{
    /// <summary>Simple * and ? matching (case sensitive; lower-case both sides for case-insensitive).</summary>
    public static bool Match(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>JSON files in %LOCALAPPDATA%\Rehad.</summary>
public static class Store
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rehad");

    static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string PathOf(string name) => Path.Combine(DataDir, name);

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var p = PathOf(name);
            if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p), json) ?? new T();
            var bak = p + ".bak";
            if (File.Exists(bak)) return JsonSerializer.Deserialize<T>(File.ReadAllText(bak), json) ?? new T();
        }
        catch { }
        return new T();
    }

    static readonly object saveGate = new();

    public static void Save<T>(string name, T value)
    {
        lock (saveGate)
        {
            Directory.CreateDirectory(DataDir);
            var p = PathOf(name);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, json));
            if (File.Exists(p)) File.Copy(p, p + ".bak", true);
            File.Move(tmp, p, true);
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, json);
    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, json);
}
