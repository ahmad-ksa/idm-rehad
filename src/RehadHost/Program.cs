using System.Diagnostics;
using System.Text;
using Rehad.Core;

// Native messaging host for the browser extension (Chrome / Edge / Firefox).
// Browser → stdin: [4-byte little-endian length][UTF-8 JSON]; we answer the same way on stdout.
// Each message is forwarded to the running Rehad app over the named pipe; the app is started when needed.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (true)
{
    var lenBuf = new byte[4];
    if (!await ReadExactly(stdin, lenBuf)) break;
    int len = BitConverter.ToInt32(lenBuf, 0);
    if (len <= 0 || len > 64 * 1024 * 1024) break;
    var body = new byte[len];
    if (!await ReadExactly(stdin, body)) break;
    var json = Encoding.UTF8.GetString(body);

    string reply;
    if (json.Contains("\"type\":\"ping\""))
        reply = await PipeServer.SendAsync("{\"type\":\"ping\"}", 500) ?? "{\"ok\":true,\"app\":false}";
    else
    {
        reply = await PipeServer.SendAsync(json, 1500) ?? await StartAppAndSend(json);
    }
    var outBytes = Encoding.UTF8.GetBytes(reply.Trim());
    await stdout.WriteAsync(BitConverter.GetBytes(outBytes.Length));
    await stdout.WriteAsync(outBytes);
    await stdout.FlushAsync();
}

static async Task<bool> ReadExactly(Stream s, byte[] buf)
{
    int got = 0;
    while (got < buf.Length)
    {
        int n = await s.ReadAsync(buf.AsMemory(got));
        if (n == 0) return false;
        got += n;
    }
    return true;
}

static async Task<string> StartAppAndSend(string json)
{
    var exe = Path.Combine(AppContext.BaseDirectory, "Rehad.exe");
    if (!File.Exists(exe)) return "{\"ok\":false,\"error\":\"Rehad.exe not found\"}";
    try { Process.Start(new ProcessStartInfo(exe, "/background") { UseShellExecute = true }); } catch { }
    for (int i = 0; i < 40; i++)
    {
        await Task.Delay(250);
        var r = await PipeServer.SendAsync(json, 500);
        if (r != null) return r;
    }
    return "{\"ok\":false,\"error\":\"Rehad did not start\"}";
}
