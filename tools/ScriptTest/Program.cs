using CatClawMusic.Plugins.LxSource;

// ── 实测 5 个音源能否下载会员无损(flac)：每源分配一首不同周杰伦歌曲 ──
// 流程：酷我搜索 → 分配歌曲 → 脚本 flac 音质取直链 → 下载文件 → 校验魔数/大小
var scriptDir = @"C:\Users\lvjin\OneDrive\桌面\音源";
var outDir = Path.Combine(Path.GetTempPath(), "lx_dl_test");
Directory.CreateDirectory(outDir);

// 酷我搜周杰伦 10 首，按顺序分配给各脚本（每源一首不同歌）
var songs = await LxKuwoApi.SearchAsync("周杰伦", 1, 10);
if (songs is not { Count: >= 5 })
{
    Console.WriteLine("酷我搜索失败，无法测试");
    return 1;
}
var testPlan = new[]
{
    (script: "HYWmusic_beta_公益测试 v0.74.0.js", song: songs[0]),
    (script: "K×H测试 v1.7.17.js", song: songs[1]),
    (script: "独家音源-v5.js", song: songs[2]),
    (script: "星海音乐源 v2.3.11.js", song: songs[3]),
    (script: "长青SVIP音源(二改修复版) v1.2.0.js", song: songs[4]),
};

Console.WriteLine("=== 会员无损(flac)下载测试 ===");
foreach (var p in testPlan)
{
    var (script, song) = p;
    var songId = song.Id.Split(':')[1];
    Console.WriteLine($"\n===== {script} =====");
    Console.WriteLine($"  歌曲: {song.Title} / {song.Artist} / id={songId}");

    var path = Path.Combine(scriptDir, script);
    if (!File.Exists(path)) { Console.WriteLine("  文件不存在"); continue; }

    using var host = new LxScriptHost();
    var ok = await host.LoadFromFileAsync(path);
    if (!ok) { Console.WriteLine($"  加载失败: {host.LastError?.Split('\n')[0]}"); continue; }

    var src = host.Sources!;
    var qualitys = src.Qualitys("kw");
    Console.WriteLine($"  kw qualitys: {(qualitys.Count > 0 ? string.Join(",", qualitys) : "(未声明)")}");

    // 请求 flac；若脚本未声明 flac 用最高档（模拟降级链）
    var wantFlac = qualitys.Contains("flac");
    var levels = wantFlac ? new[] { "flac", "320k", "128k" } : new[] { "320k", "128k" };
    string? url = null;
    var usedLevel = "";
    foreach (var level in levels)
    {
        if (!qualitys.Contains(level)) continue;
        Console.WriteLine($"  请求音质: {level} ...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        url = await host.GetMusicUrlAsync("kw", songId, song.Title, song.Artist, song.DurationMs / 1000, level)
            .WaitAsync(cts.Token);
        if (!string.IsNullOrWhiteSpace(url)) { usedLevel = level; break; }
    }

    if (string.IsNullOrWhiteSpace(url))
    {
        Console.WriteLine($"  取直链失败: {host.LastError?.Split('\n')[0]}");
        continue;
    }
    Console.WriteLine($"  直链({usedLevel}): {url[..Math.Min(100, url.Length)]}");

    // 下载验证
    try
    {
        var ext = usedLevel == "flac" ? ".flac" : ".mp3";
        var file = Path.Combine(outDir, $"{script.Split(' ')[0].Trim()}_{songId}{ext}");
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        using var resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = file + ".part";
        await using (var fs = File.Create(tmp))
        await using (var src2 = await resp.Content.ReadAsStreamAsync())
            await src2.CopyToAsync(fs);
        File.Move(tmp, file, true);
        var size = new FileInfo(file).Length;
        var magic = await ReadMagic(file);
        Console.WriteLine($"  下载成功: {size / 1024.0:F0} KB  魔数: {magic}  →  {file}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  下载失败: {ex.Message}");
    }
}

Console.WriteLine("\n=== 完成 ===");
return 0;

static async Task<string> ReadMagic(string file)
{
    var head = new byte[8];
    await using var fs = File.OpenRead(file);
    var n = await fs.ReadAsync(head);
    if (n >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return "FLAC ✓";
    if (n >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3') return "MP3(ID3) ✓";
    if (n >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "MP3(帧) ✓";
    return Convert.ToHexString(head);
}
