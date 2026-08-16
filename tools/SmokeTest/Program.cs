using System.Net;
using System.Reflection;
using System.Text;
using CatClawMusic.Plugins.LxSource;

// ── 模拟 lx-music-api-server：本机 HTTP 服务，按协议返回固定 JSON ──
const int Port = 18765;
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
listener.Start();
Console.WriteLine($"[mock] lx-music-api-server 模拟服务已启动: http://127.0.0.1:{Port}");

var serverTask = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { break; }
        var path = ctx.Request.Url!.AbsolutePath;
        var q = ctx.Request.QueryString;
        byte[] body;
        switch (path)
        {
            case "/ping":
                body = Encoding.UTF8.GetBytes("pong");
                break;
            case "/search":
                if (q["name"] == "error")
                    body = Encoding.UTF8.GetBytes("{\"code\":-1,\"msg\":\"模拟失败\"}");
                else
                    body = Encoding.UTF8.GetBytes(
                        "{\"code\":0,\"data\":{\"total\":3,\"list\":[" +
                        "{\"id\":\"S1\",\"name\":\"Test Song\",\"artist\":\"Tester\",\"album\":\"Album One\",\"pic_id\":\"P1\",\"url_id\":\"U1\",\"interval\":240,\"source\":\"netease\",\"quality\":{\"128\":true,\"320\":true}}," +
                        "{\"id\":\"S2\",\"name\":\"Second\",\"artist\":\"Tester\",\"album\":\"Album Two\",\"interval\":\"04:00\",\"source\":\"qq\",\"quality\":{\"128\":true,\"320\":true,\"flac\":true}}," +
                        "{\"id\":\"S3\",\"name\":\"Third\",\"artist\":\"Tester\",\"album\":\"Album Three\",\"pic_id\":\"P2\",\"url_id\":\"U3\",\"interval\":123.5,\"source\":\"bilibili\"}" +
                        "]}}");
                break;
            case "/songurl":
                body = q["br"] == "flac"
                    ? Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"url\":[{\"url\":\"http://media.example.com/u1_f1.flac\",\"size\":1000},{\"url\":\"http://media.example.com/u1_f2.flac\",\"size\":2000}],\"type\":\"flac\"}}")
                    : Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"url\":\"http://media.example.com/u1_320.mp3\",\"type\":\"mp3\",\"size\":1234567,\"br\":320}}");
                break;
            case "/pic":
                body = q["id"] == "P2"
                    ? Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"url\":[{\"url\":\"http://media.example.com/p2_a.jpg\",\"size\":1},{\"url\":\"http://media.example.com/p2_b.jpg\",\"size\":2}]}}")
                    : Encoding.UTF8.GetBytes("{\"code\":0,\"data\":{\"url\":\"http://media.example.com/p1.jpg\",\"size\":300}}");
                break;
            case "/lyric":
                // 译文/罗马音时间戳故意偏移 30~50ms（模拟真实网易返回，验证容差合并）
                body = Encoding.UTF8.GetBytes(
                    "{\"code\":0,\"data\":{\"lrc\":\"[00:00.00]第一句\\n[00:05.00]第二句\\n[01:00.00]副歌\"," +
                    "\"tlyric\":\"[00:00.05]First line\\n[00:05.05]Second line\"," +
                    "\"rlyric\":\"[00:00.03]Daiichi ku\\n[00:05.03]Daini ku\"}}");
                break;
            default:
                ctx.Response.StatusCode = 404;
                body = Encoding.UTF8.GetBytes("not found");
                break;
        }
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }
});

// ── 测试用例 ──
int passed = 0, failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else { failed++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
}

try
{
    var client = new LxApiClient();
    client.SetServerUrl($"http://127.0.0.1:{Port}");

    // 1. ping
    Check("PingAsync", await client.PingAsync());

    // 2. 搜索 + interval 三种形态解析
    var songs = await client.SearchAsync("test", 1, 20, null);
    Check("SearchAsync 返回 3 首", songs is { Count: 3 });
    Check("interval=240(秒) 解析", songs![0].IntervalSeconds == 240);
    Check("interval=\"04:00\" 解析", songs[1].IntervalSeconds == 240, $"实际 {songs[1].IntervalSeconds}");
    Check("interval=123.5(小数) 解析", songs[2].IntervalSeconds == 123, $"实际 {songs[2].IntervalSeconds}");
    Check("source 透传", songs[0].Source == "netease" && songs[1].Source == "qq" && songs[2].Source == "bilibili");
    Check("pic_id/url_id 透传", songs[0].PicId == "P1" && songs[0].UrlId == "U1");

    // 3. 播放直链（url_id 替换 + 普通 URL）
    var url320 = await client.GetSongUrlAsync(songs[0], "320");
    Check("GetSongUrlAsync(320) 直链", url320 == "http://media.example.com/u1_320.mp3", url320);

    // 4. FLAC 分段直链取首段
    var urlFlac = await client.GetSongUrlAsync(songs[0], "flac");
    Check("GetSongUrlAsync(flac) 分段取首段", urlFlac == "http://media.example.com/u1_f1.flac", urlFlac);

    // 5. 封面（pic_id 替换 + 数组兜底）
    var pic = await client.GetPicUrlAsync(songs[0], 300);
    Check("GetPicUrlAsync 封面", pic == "http://media.example.com/p1.jpg", pic);
    var picArr = await client.GetPicUrlAsync(songs[2], 300);
    Check("GetPicUrlAsync 数组取首段", picArr == "http://media.example.com/p2_a.jpg", picArr);

    // 6. 三流歌词
    var lyric = await client.GetLyricsAsync(songs[0]);
    Check("GetLyricsAsync 三流非空", lyric is { Lrc.Length: > 0, TLrc.Length: > 0, RLrc.Length: > 0 });

    // 7. 歌词合并解析（译文 + 罗马音按时间戳挂行，含 ±100ms 容差）
    var parsed = LxLrcParser.Parse(lyric!.Value.Lrc!, lyric.Value.TLrc, lyric.Value.RLrc);
    Check("LxLrcParser 解析 3 行", parsed is { Lines.Count: 3 });
    Check("译文挂载（50ms 容差）", parsed!.Lines[0].Translation == "First line", parsed.Lines[0].Translation);
    Check("罗马音挂载（30ms 容差）", parsed.Lines[0].Roma == "Daiichi ku", parsed.Lines[0].Roma);
    Check("无歌词行不挂翻译", parsed.Lines[2].Translation is null, parsed.Lines[2].Translation ?? "(null)");

    // 7b. 网易系时间标签冒号变体 "[mm:ss:xx]" 归一化（对照官方 fixTimeLabel：末两位是百分秒）
    var colonLrc = LxLrcParser.Parse("[00:12:34]冒号变体\n[01:02:03]百分秒", "[00:17:00]译文差4.7秒不挂", null);
    Check("冒号变体 [00:12:34] → 12.34s", colonLrc is { Lines.Count: 2 } && colonLrc.Lines[0].Timestamp == TimeSpan.FromMilliseconds(12340),
        colonLrc?.Lines[0].Timestamp.ToString() ?? "null");
    Check("[01:02:03] → 62.03s", colonLrc!.Lines[1].Timestamp == TimeSpan.FromMilliseconds(62030),
        colonLrc.Lines[1].Timestamp.ToString());
    Check("超容差译文不误挂", colonLrc.Lines[0].Translation is null);

    // 7c. 超容差（300ms）不合并
    var farLrc = LxLrcParser.Parse("[00:00.00]原文", "[00:00.30]差300ms", null);
    Check("300ms 超容差不挂译文", farLrc!.Lines[0].Translation is null, farLrc.Lines[0].Translation ?? "(null)");

    // 8. code!=0 → null
    var err = await client.SearchAsync("error", 1, 20, null);
    Check("code=-1 返回 null", err == null);

    // 9. 未配置服务器 → 安全返回
    var empty = new LxApiClient();
    Check("未配置服务器 Ping=false", !await empty.PingAsync());
    Check("未配置服务器 Search=null", await empty.SearchAsync("x", 1, 20, null) == null);

    // ── 阶段 2：反射加载真实插件 DLL，验证入口类全链路 ──
    Console.WriteLine("\n[phase2] 加载真实插件程序集 CatClawMusic.Plugins.LxSource.dll");
    var coreAsm = typeof(CatClawMusic.Core.Models.OnlineSong).Assembly;
    AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        new AssemblyName(e.Name).Name == coreAsm.GetName().Name ? coreAsm : null;
    var pluginPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "bin", "Release", "net10.0", "CatClawMusic.Plugins.LxSource.dll"));
    var asm = System.Reflection.Assembly.LoadFrom(pluginPath);
    var pluginType = asm.GetType("CatClawMusic.Plugins.LxSource.LxMusicPlugin")!;
    var plugin = (CatClawMusic.Core.Interfaces.IPlugin)Activator.CreateInstance(pluginType)!;
    Check("插件元数据 PluginId=lxSource", plugin.PluginId == "lxSource", plugin.PluginId);
    Check("插件实现 IOnlineMusicPlugin", plugin is CatClawMusic.Core.Interfaces.IOnlineMusicPlugin);
    Check("插件实现 IViewContributorPlugin", plugin is CatClawMusic.Core.Interfaces.IViewContributorPlugin);
    Check("插件实现 ILyricsProviderPlugin", plugin is CatClawMusic.Core.Interfaces.ILyricsProviderPlugin);
    await plugin.InitializeAsync();

    var lxPlugin = (CatClawMusic.Core.Interfaces.IOnlineMusicPlugin)plugin;
    var lxLyricProvider = (CatClawMusic.Core.Interfaces.ILyricsProviderPlugin)plugin;
    // 通过公开方法配置服务器（dynamic 调用避免引插件程序集）
    ((dynamic)plugin).SaveConfig($"http://127.0.0.1:{Port}", 1, "netease");

    var onlineSongs = await lxPlugin.SearchAsync("test", 1, 5);
    Check("插件 SearchAsync 返回 3 首", onlineSongs is { Count: 3 });
    Check("复合 Id source:id", onlineSongs![0].Id == "netease:S1", onlineSongs[0].Id);
    Check("Platform=lx", onlineSongs[0].Platform == "lx");
    Check("DurationMs=240000", onlineSongs[0].DurationMs == 240000, onlineSongs[0].DurationMs.ToString());
    Check("封面已解析", onlineSongs[0].CoverUrl == "http://media.example.com/p1.jpg", onlineSongs[0].CoverUrl);
    Check("bilibili 曲目复合 Id", onlineSongs[2].Id == "bilibili:S3", onlineSongs[2].Id);
    Check("搜索失败返回 null", await lxPlugin.SearchAsync("error", 1, 5) == null);

    var playUrl = await lxPlugin.GetPlayUrlAsync(onlineSongs[0], 1);
    Check("插件 GetPlayUrlAsync(320)", playUrl == "http://media.example.com/u1_320.mp3", playUrl);

    // RemoteId 路由：宿主歌词兜底链的入口（形如 "lx:netease:S1"）
    var lrc = await lxLyricProvider.GetLyricsAsync(new CatClawMusic.Core.Models.Song
    {
        Title = "Test Song",
        Artist = "Tester",
        RemoteId = "lx:netease:S1",
    });
    Check("歌词兜底链命中 RemoteId 路由", lrc is { Lines.Count: 3 });
    Check("兜底链译文+罗马音挂载", lrc!.Lines[0].Translation == "First line" && lrc.Lines[0].Roma == "Daiichi ku",
        $"T={lrc.Lines[0].Translation} R={lrc.Lines[0].Roma}");
    var other = await lxLyricProvider.GetLyricsAsync(new CatClawMusic.Core.Models.Song
    {
        Title = "x",
        RemoteId = "netease:123",
    });
    Check("非 lx: 前缀不拦截", other == null);
}
finally
{
    listener.Stop();
}

Console.WriteLine($"\n结果：{passed} 通过 / {failed} 失败");
return failed == 0 ? 0 : 1;
