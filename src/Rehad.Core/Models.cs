using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Rehad.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Assembling, Complete, Error }

/// <summary>File = plain HTTP/FTP file, Hls = m3u8 stream, Site = page handled by yt-dlp (YouTube, TikTok…).</summary>
public enum DownloadKind { File, Hls, Site }

/// <summary>A byte range downloaded by one connection into its own part file.</summary>
public sealed class Segment
{
    public int Index { get; set; }
    public long Start { get; set; }
    /// <summary>Inclusive end; -1 while the file size is unknown.</summary>
    public long End { get; set; } = -1;
    public long Done { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public string Info { get; set; } = "";
    [JsonIgnore] public readonly object Gate = new();

    [JsonIgnore] public long Position => Start + Done;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public bool Finished => End >= 0 && Position > End;
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        foreach (var a in also) Raise(a);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DownloadItem : Notifier
{
    public int Id { get; set; }
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? Referer { get; set; }
    public string? PageUrl { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Login { get; set; }
    public string? Password { get; set; }
    /// <summary>yt-dlp format id or HLS variant (bandwidth); null = best.</summary>
    public string? Format { get; set; }
    public string? Queue { get; set; }
    public int MaxConnections { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public List<Segment> Segments { get; set; } = new();

    string fileName = "";
    public string FileName { get => fileName; set => Set(ref fileName, value, also: nameof(FullPath)); }

    string saveDir = "";
    public string SaveDir { get => saveDir; set => Set(ref saveDir, value, also: nameof(FullPath)); }

    [JsonIgnore] public string FullPath => Path.Combine(SaveDir, FileName);

    string category = "General";
    public string Category { get => category; set => Set(ref category, value); }

    string description = "";
    public string Description { get => description; set => Set(ref description, value); }

    long size = -1;
    public long Size { get => size; set => Set(ref size, value, also: [nameof(Progress), nameof(SizeText)]); }

    long downloaded;
    public long Downloaded { get => downloaded; set => Set(ref downloaded, value, also: nameof(Progress)); }

    DownloadStatus status = DownloadStatus.Queued;
    public DownloadStatus Status { get => status; set => Set(ref status, value, also: nameof(IsFinished)); }

    string error = "";
    public string Error { get => error; set => Set(ref error, value); }

    bool? resumable;
    public bool? Resumable { get => resumable; set => Set(ref resumable, value); }

    DateTime? lastTry;
    public DateTime? LastTry { get => lastTry; set => Set(ref lastTry, value); }

    // live statistics (not saved)
    double speed;
    [JsonIgnore] public double Speed { get => speed; set => Set(ref speed, value); }

    TimeSpan? timeLeft;
    [JsonIgnore] public TimeSpan? TimeLeft { get => timeLeft; set => Set(ref timeLeft, value); }

    int connections;
    [JsonIgnore] public int ActiveConnections { get => connections; set => Set(ref connections, value); }

    [JsonIgnore] public double Progress => Size > 0 ? Math.Min(100.0, Downloaded * 100.0 / Size) : 0;
    [JsonIgnore] public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
    [JsonIgnore] public bool IsFinished => Status == DownloadStatus.Complete;
    [JsonIgnore] public bool IsRunning => Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Assembling;

    internal long AddDownloaded(long n)
    {
        var v = Interlocked.Add(ref downloaded, n);
        Raise(nameof(Downloaded));
        Raise(nameof(Progress));
        return v;
    }

    public void NotifyStats() => Raise(nameof(Segments));
}

public static class Fmt
{
    static readonly string[] units = { "Bytes", "KB", "MB", "GB", "TB" };

    public static string Bytes(long n)
    {
        if (n < 0) return "";
        double v = n; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{n} {units[0]}" : $"{v:0.##} {units[u]}";
    }

    public static string Rate(double bytesPerSec) => bytesPerSec <= 0 ? "" : $"{Bytes((long)bytesPerSec)}/sec";

    public static string Time(TimeSpan? t)
    {
        if (t is null) return "";
        var v = t.Value;
        if (v.TotalDays >= 1) return $"{(int)v.TotalDays} d {v.Hours} h";
        if (v.TotalHours >= 1) return $"{(int)v.TotalHours} h {v.Minutes} min";
        if (v.TotalMinutes >= 1) return $"{v.Minutes} min {v.Seconds} sec";
        return $"{Math.Max(1, v.Seconds)} sec";
    }
}
