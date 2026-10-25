using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rehad.Core;

namespace Rehad;

public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c) => ShellIcons.ForFile(value as string ?? "");
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length < 2 || values[0] is not DownloadStatus st) return "";
        double pct = values[1] is double d ? d : 0;
        return st switch
        {
            DownloadStatus.Downloading => Loc.T("st.Downloading", pct),
            DownloadStatus.Paused => pct > 0 ? Loc.T("st.Paused", pct) : Loc.T("st.Paused", 0.0).Replace("0% ", ""),
            DownloadStatus.Error => Loc.T("st.Error") + (values.Length > 2 && values[2] is string e && e.Length > 0 ? ": " + e : ""),
            _ => Loc.T("st." + st),
        };
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => Array.Empty<object>();
}

public sealed class RateConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? Fmt.Rate(d) : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TimeLeftConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Fmt.Time(value as TimeSpan?);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class DateConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is DateTime d ? d.ToString("MMM dd HH:mm", Loc.Arabic ? new CultureInfo("ar-SA") { DateTimeFormat = { Calendar = new GregorianCalendar() } } : CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class QueueMarkConverter : IValueConverter
{
    public static readonly QueueMarkConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is string s && s.Length > 0 ? "▶" : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Windows file-type icons (same icons Explorer shows) by extension.</summary>
public static class ShellIcons
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;

    public static ImageSource? ForFile(string name, bool large = false)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) ext = ".file";
        var key = ext + (large ? "#L" : "");
        if (cache.TryGetValue(key, out var img)) return img;
        img = null;
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo("x" + ext, 0x80, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON) | SHGFI_USEFILEATTRIBUTES);
            if (info.hIcon != IntPtr.Zero)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                DestroyIcon(info.hIcon);
            }
        }
        catch { }
        cache[key] = img;
        return img;
    }

    public static string TypeName(string name)
    {
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo("x" + Path.GetExtension(name), 0x80, ref info, (uint)Marshal.SizeOf(info), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return info.szTypeName ?? "";
        }
        catch { return ""; }
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("app")); }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch { }
    }

    public static void OpenWith(string path)
    {
        try { Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"); } catch { }
    }

    public static string? PickFolder(string start)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Directory.Exists(start) ? start : "", UseDescriptionForTitle = true };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
    }
}
