using CatClawMusic.Plugins.LxSource;

// ── 实测 5 个 lx 音源脚本：加载 → 声明能力 → 用酷我真实歌曲测 musicUrl ──
var scriptDir = @"C:\Users\lvjin\OneDrive\桌面\音源";
var scripts = new[]
{
    "HYWmusic_beta_公益测试 v0.74.0.js",
    "K×H测试 v1.7.17.js",
    "独家音源-v5.js",
    "星海音乐源 v2.3.11.js",
    "长青SVIP音源(二改修复版) v1.2.0.js",
};

// 酷我真实歌曲（搜索"周杰伦"第一首：晴天）
var kwSongs = await LxKuwoApi.SearchAsync("周杰伦", 1, 3);
var kwSong = kwSongs?.FirstOrDefault();
Console.WriteLine($"测试曲目: {(kwSong == null ? "N/A" : kwSong.Title + " / " + kwSong.Artist + " / id=" + kwSong.Id)}");
if (kwSong == null) { Console.WriteLine("酷我搜索失败，无法测试"); return 1; }
var kwId = kwSong.Id.Split(':')[1];

foreach (var file in scripts)
{
    var path = Path.Combine(scriptDir, file);
    Console.WriteLine($"\n===== {file} =====");
    if (!File.Exists(path)) { Console.WriteLine("  文件不存在"); continue; }

    using var host = new LxScriptHost();
    var ok = await host.LoadFromFileAsync(path);
    Console.WriteLine($"  加载: {(ok ? "OK" : "FAIL: " + host.LastError)}");
    if (!ok)
    {
        if (host.HasUpdateAlert) Console.WriteLine($"  (updateAlert: {host.UpdateMessage})");
        continue;
    }

    // 声明能力
    var src = host.Sources!;
    Console.WriteLine($"  声明源: {string.Join(", ", src.SourceCodes)}");
    foreach (var code in src.SourceCodes)
    {
        var actions = string.Join("/", src.ActionsBySource[code]);
        var qs = src.Qualitys(code).Count > 0 ? " 音质:[" + string.Join(",", src.Qualitys(code)) + "]" : "";
        Console.WriteLine($"    {code}: actions={actions}{qs}");
    }

    // 测 musicUrl（kw 平台 + 320k）
    if (src.Supports("kw", "musicUrl"))
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? url = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            url = await host.GetMusicUrlAsync("kw", kwId, kwSong.Title, kwSong.Artist, kwSong.DurationMs / 1000, "320k")
                .WaitAsync(cts.Token);
        }
        catch (Exception ex) { url = null; Console.WriteLine($"  musicUrl 异常: {ex.GetType().Name}: {ex.Message}"); }
        sw.Stop();
        Console.WriteLine($"  kw musicUrl(320k) {sw.ElapsedMilliseconds}ms: {(string.IsNullOrEmpty(url) ? "FAIL" + (host.LastError != null ? " (" + host.LastError + ")" : "") : url[..Math.Min(90, url.Length)])}");
    }
    else
    {
        Console.WriteLine("  脚本未声明 kw/musicUrl，跳过");
    }
}

Console.WriteLine("\n===== 完成 =====");
return 0;
