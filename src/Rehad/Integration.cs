using System.Text.Json;
using Microsoft.Win32;
using Rehad.Core;

namespace Rehad;

/// <summary>Registers the native messaging host (RehadHost.exe) for Chrome, Edge, Brave, Chromium and Firefox (per user, no admin needed).</summary>
public static class Integration
{
    public const string HostName = "com.rehad.host";
    public const string ChromeExtensionId = "emlmbpfejolackcakidklmefljggldda";
    public const string FirefoxExtensionId = "rehad@rehad.app";

    public static void Register()
    {
        try
        {
            var hostExe = Path.Combine(AppContext.BaseDirectory, "RehadHost.exe");
            if (!File.Exists(hostExe)) return;
            var dir = Path.Combine(Store.DataDir, "host");
            Directory.CreateDirectory(dir);
            var chromeJson = Path.Combine(dir, HostName + ".json");
            var firefoxJson = Path.Combine(dir, HostName + ".firefox.json");
            File.WriteAllText(chromeJson, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "Rehad Download Manager browser helper",
                ["path"] = hostExe,
                ["type"] = "stdio",
                ["allowed_origins"] = new[] { $"chrome-extension://{ChromeExtensionId}/" },
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(firefoxJson, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "Rehad Download Manager browser helper",
                ["path"] = hostExe,
                ["type"] = "stdio",
                ["allowed_extensions"] = new[] { FirefoxExtensionId },
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var root in new[]
            {
                @"Software\Google\Chrome\NativeMessagingHosts",
                @"Software\Chromium\NativeMessagingHosts",
                @"Software\Microsoft\Edge\NativeMessagingHosts",
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
                @"Software\Vivaldi\NativeMessagingHosts",
            })
                Set(root + "\\" + HostName, chromeJson);
            Set(@"Software\Mozilla\NativeMessagingHosts\" + HostName, firefoxJson);
        }
        catch { }
    }

    static void Set(string key, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k?.SetValue("", value);
    }
}
