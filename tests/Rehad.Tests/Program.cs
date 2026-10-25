using System.Security.Cryptography;
using Rehad.Core;

// Engine tests against a local server: tests/Rehad.Tests <baseUrl> <wwwDir>
if (args.Length > 0 && args[0] == "pipe-echo")
{
    var srv = new PipeServer();
    srv.Message += m => "{\"ok\":true,\"echo\":" + m.GetRawText() + "}";
    srv.Start();
    await Task.Delay(TimeSpan.FromSeconds(20));
    return 0;
}
var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:8765";
var www = args.Length > 1 ? args[1] : ".";
var work = Path.Combine(Path.GetTempPath(), "rehad-test-" + Guid.NewGuid().ToString("N")[..6]);
var s = new Settings { TempFolder = Path.Combine(work, "tmp"), DefaultFolder = Path.Combine(work, "out"), Proxy = ProxyMode.None, MaxConnections = 8, TimeoutSeconds = 15, Retries = 5 };
int failed = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));

// 1. segmented download, 8 connections, dynamic splitting
var item = new DownloadItem { Id = 1, Url = baseUrl + "/big.bin", FileName = "big.bin", SaveDir = s.DefaultFolder };
int maxConns = 0;
var job = new SegmentedDownloader(item, s, new SpeedLimiter(), new SpeedLimiter()).RunAsync(CancellationToken.None);
while (!job.IsCompleted) { maxConns = Math.Max(maxConns, item.ActiveConnections); await Task.Delay(50); }
await job;
Check(Hash(item.FullPath) == Hash(Path.Combine(www, "big.bin")), $"segmented download identical (max {maxConns} connections, {item.Segments.Count} segs left)");
Check(maxConns >= 6, "used multiple connections");

// 2. pause in the middle, then resume
var item2 = new DownloadItem { Id = 2, Url = baseUrl + "/big.bin", FileName = "resume.bin", SaveDir = s.DefaultFolder };
var cts = new CancellationTokenSource();
var j2 = new SegmentedDownloader(item2, s, new SpeedLimiter(), new SpeedLimiter()).RunAsync(cts.Token);
while (item2.Downloaded < 8 * 1024 * 1024) await Task.Delay(20);
cts.Cancel();
try { await j2; } catch (OperationCanceledException) { }
long before = item2.Downloaded;
var saved = Store.Deserialize<DownloadItem>(Store.Serialize(item2))!; // survive a restart
await new SegmentedDownloader(saved, s, new SpeedLimiter(), new SpeedLimiter()).RunAsync(CancellationToken.None);
Check(Hash(saved.FullPath) == Hash(Path.Combine(www, "big.bin")), $"pause at {before / 1048576} MB + resume after restart identical");

// 3. server without range support → single connection
var item3 = new DownloadItem { Id = 3, Url = baseUrl + "/norange/big.bin", FileName = "norange.bin", SaveDir = s.DefaultFolder };
await new SegmentedDownloader(item3, s, new SpeedLimiter(), new SpeedLimiter()).RunAsync(CancellationToken.None);
Check(item3.Resumable == false && Hash(item3.FullPath) == Hash(Path.Combine(www, "big.bin")), "non-resumable server");

// 4. speed limiter
var item4 = new DownloadItem { Id = 4, Url = baseUrl + "/big.bin", FileName = "limited.bin", SaveDir = s.DefaultFolder };
var lim = new SpeedLimiter { Rate = 2 * 1024 * 1024 };
var c4 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var sw = System.Diagnostics.Stopwatch.StartNew();
try { await new SegmentedDownloader(item4, s, lim, new SpeedLimiter()).RunAsync(c4.Token); } catch (OperationCanceledException) { }
double rate = item4.Downloaded / sw.Elapsed.TotalSeconds;
Check(rate < 3.2 * 1024 * 1024, $"speed limiter ~2 MB/s (measured {rate / 1048576:0.00} MB/s)");

// 5. HLS master → best variant, AES-128, 12 segments
var item5 = new DownloadItem { Id = 5, Kind = DownloadKind.Hls, Url = baseUrl + "/master.m3u8", FileName = "stream.mp4", SaveDir = s.DefaultFolder };
var variants = await HlsDownloader.VariantsAsync(item5, s, CancellationToken.None);
Check(variants.Count == 2 && variants[0].Bandwidth == 2000000, "HLS variants parsed");
Tools.AppDir = work; // no ffmpeg here → .ts output
await new HlsDownloader(item5, s, new SpeedLimiter()).RunAsync(CancellationToken.None);
var hlsOut = item5.FullPath;
Check(hlsOut.EndsWith(".ts") || hlsOut.EndsWith(".mp4"), "HLS output " + Path.GetFileName(hlsOut));
if (hlsOut.EndsWith(".ts")) Check(Hash(hlsOut) == Hash(Path.Combine(www, "plain.expected")), "HLS decrypted + joined correctly");

// 6. helpers
Check(Links.Batch("http://x/img*.jpg", "1", "12", 3)[11] == "http://x/img012.jpg", "batch pattern");
Check(Links.Extract("see http://a.com/f.zip and https://b.org/x?y=1.").Count == 2, "clipboard links");
Check(Wildcard.Match("*.example.com", "dl.example.com") && !Wildcard.Match("r0*", "rar"), "wildcards");
Check(s.CategoryFor("movie.MKV")?.Name == "Video" && s.CategoryFor("a.r01")?.Name == "Compressed", "categories");
Check(YtDlp.IsSitePage("https://www.youtube.com/watch?v=x") && !YtDlp.IsSitePage("https://example.com/a.mp4"), "site detection");

var stemT = YtDlp.SafeStem("فيلم _ كوميدي جديد كامل 😂 أقوى أفلام الكوميديا العربية بجودة عالية HD | فيلم كامل 2025 حصريا على القناة بجودة 1080p");
Check(stemT.Length <= 80 && !stemT.Contains("😂") && stemT.StartsWith("فيلم"), "safe short title: " + stemT);
Check(SegmentedDownloader.TempDirFor(s, new DownloadItem { Id = 42, FileName = new string('x', 200) }).EndsWith("dl42"), "short temp dir");

// yt-dlp path (generic extractor on a local file, Arabic name)
if (YtDlp.Exe != null)
{
    var yi = new DownloadItem { Id = 77, Kind = DownloadKind.Site, Url = baseUrl + "/clip.mp4", PageUrl = baseUrl + "/clip.mp4", FileName = "تامر حسني - قال فاكرني 😂.mp4", SaveDir = s.DefaultFolder };
    var mi = await YtDlp.InfoAsync(yi, s, CancellationToken.None);
    Check(File.Exists(Path.Combine(Store.DataDir, "info_77.json")), "page info saved for reuse");
    var log = new List<string>();
    await YtDlp.DownloadAsync(yi, s, CancellationToken.None);
    Check(File.Exists(yi.FullPath) && yi.Size == 3000000, "yt-dlp download + final path: " + yi.FileName);
}

// 7. manager + queue: two files, one at a time, both complete
var list = new List<DownloadItem>();
var mgr = new DownloadManager(s, list);
list.Clear();
var qa = mgr.Create(baseUrl + "/big.bin"); qa.FileName = "q1.bin"; qa.SaveDir = s.DefaultFolder; mgr.Add(qa);
var qb = mgr.Create(baseUrl + "/norange/big.bin"); qb.FileName = "q2.bin"; qb.SaveDir = s.DefaultFolder; mgr.Add(qb);
mgr.AddToQueue(qa, mgr.MainQueue); mgr.AddToQueue(qb, mgr.MainQueue);
bool finished = false; int maxRunning = 0;
mgr.QueueFinished += _ => finished = true;
mgr.StartQueue(mgr.MainQueue);
for (int i = 0; i < 600 && !finished; i++) { maxRunning = Math.Max(maxRunning, mgr.RunningCount); mgr.Tick(); await Task.Delay(100); }
Check(finished && qa.IsFinished && qb.IsFinished && maxRunning == 1, $"queue processed one file at a time (status {qa.Status}/{qb.Status})");

try { Directory.Delete(work, true); } catch { }
Console.WriteLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
return failed;
