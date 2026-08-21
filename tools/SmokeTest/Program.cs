using System.Net;
using System.Reflection;
using System.Text;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Plugins.LxSource;

// ── 模拟 lx-music-api-server：本机 HTTP 服务，按协议返回固定 JSON ──
const int Port = 18765;
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
listener.Start();
Console.WriteLine($"[mock] lx-music-api-server 模拟服务已启动: http://127.0.0.1:{Port}");

// lx-music 自定义源脚本（render_api 风格），回调 mock /url 端点
const string ScriptJs = """
const { EVENT_NAMES, request, on, send, utils, env, version } = globalThis.lx;
const httpFetch = (url, options = { method: 'GET' }) => new Promise((resolve, reject) => {
    request(url, options, (err, resp) => { if (err) return reject(err); resolve(resp); });
});
const handleGetMusicUrl = async (source, musicInfo, quality) => {
    const songId = musicInfo.hash ?? musicInfo.songmid;
    const r = await httpFetch(`http://127.0.0.1:__PORT__/url/${source}/${songId}/${quality}`, {
        method: 'GET', headers: { 'Content-Type': 'application/json' },
    });
    if (!r.body || isNaN(Number(r.body.code))) throw new Error('unknown error');
    return r.body.url;
};
const musicSources = {};
['wy','kw','kg','tx','mg'].forEach(s => { musicSources[s] = { name: s, type: 'music', actions: ['musicUrl'], qualitys: ['128k','320k'] }; });
on(EVENT_NAMES.request, ({ action, source, info }) => {
    if (action === 'musicUrl') return handleGetMusicUrl(source, info.musicInfo, info.type);
    return Promise.reject('action not support');
});
send(EVENT_NAMES.inited, { status: true, sources: musicSources });
""";

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
        if (path.StartsWith("/url/", StringComparison.OrdinalIgnoreCase))
        {
            // render_api 风格：/url/{source}/{songmid}/{quality} → {code:0,url:...}
            var seg = path.Split('/');
            var src = seg.Length > 2 ? seg[2] : "wy";
            var quality = seg.Length > 4 ? seg[4] : "320k";
            var mediaUrl = quality == "128k" ? $"http://media.example.com/{src}_128.mp3" : $"http://media.example.com/{src}_320.mp3";
            body = Encoding.UTF8.GetBytes($"{{\"code\":0,\"url\":\"{mediaUrl}\",\"source\":\"{src}\"}}");
        }
        else switch (path)
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
            case "/script.js":
                // lx-music 自定义源脚本（render_api 风格，回调 mock /url 端点）
                body = Encoding.UTF8.GetBytes(ScriptJs.Replace("__PORT__", Port.ToString()));
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

    // ── 阶段 2：反射加载真实插件 DLL（脚本模式），验证入口类全链路 ──
    Console.WriteLine("\n[phase2] 加载真实插件程序集 + 脚本模式（嵌入 Jint 经 AssemblyResolve 加载）");
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
    dynamic dplugin = plugin;

    // 加载 mock 脚本（验证嵌入 Jint 在插件 DLL 上下文经 AssemblyResolve 加载）
    bool scriptOk = await dplugin.LoadScriptAsync($"http://127.0.0.1:{Port}/script.js");
    Check("插件加载脚本源", scriptOk);
    Check("插件 ScriptReady", (bool)dplugin.ScriptReady);
    Check("ILyricsProviderPlugin.IsAvailable", ((CatClawMusic.Core.Interfaces.ILyricsProviderPlugin)plugin).IsAvailable);

    // SearchAsync：mock 脚本不支持 musicSearch → 返回 null
    var searchResult = await lxPlugin.SearchAsync("test", 1, 5);
    Check("脚本不支持 musicSearch 返回 null", searchResult == null);

    // GetPlayUrlAsync：构造 netease 歌曲测试（搜索不可用，直接构造 OnlineSong）
    var testSong = new CatClawMusic.Core.Models.OnlineSong
    {
        Id = "netease:S1",
        Platform = "lx",
        Title = "Test",
        Artist = "Tester",
        DurationMs = 240000,
        Internal = new Dictionary<string, object> { ["Source"] = "netease", ["RawId"] = "S1" },
    };
    var scriptPlayUrl = await lxPlugin.GetPlayUrlAsync(testSong, 1);
    Check("播放直链走脚本（wy_320）", scriptPlayUrl == "http://media.example.com/wy_320.mp3", scriptPlayUrl);

    // 不支持的源 → null（bilibili 不在脚本声明里）
    var unsupportedSong = new CatClawMusic.Core.Models.OnlineSong
    {
        Id = "bilibili:X9", Platform = "lx", Title = "x", Artist = "y",
        Internal = new Dictionary<string, object> { ["Source"] = "bilibili", ["RawId"] = "X9" },
    };
    var unsupportedUrl = await lxPlugin.GetPlayUrlAsync(unsupportedSong, 1);
    Check("脚本不支持的源返回 null", unsupportedUrl == null);

    // RemoteId 路由：lx: 前缀命中路由（脚本不支持 lyric → null）；非 lx: 前缀直接 null
    var lrcRouted = await lxLyricProvider.GetLyricsAsync(new CatClawMusic.Core.Models.Song
    { Title = "Test", Artist = "Tester", RemoteId = "lx:netease:S1" });
    Check("lx: 前缀命中路由（脚本无 lyric → null）", lrcRouted == null);
    var other = await lxLyricProvider.GetLyricsAsync(new CatClawMusic.Core.Models.Song
    { Title = "x", RemoteId = "netease:123" });
    Check("非 lx: 前缀不拦截", other == null);

    // ── 阶段 3：Jint 脚本引擎 + lx 自定义源 .js（验证 musicUrl 全链路）──
    Console.WriteLine("\n[phase3] Jint 脚本引擎：lx 自定义源 .js");
    var mockJs = $$"""
const { EVENT_NAMES, request, on, send, utils, env, version } = globalThis.lx;
const httpFetch = (url, options = { method: 'GET' }) => new Promise((resolve, reject) => {
    request(url, options, (err, resp) => { if (err) return reject(err); resolve(resp); });
});
const handleGetMusicUrl = async (source, musicInfo, quality) => {
    const songId = musicInfo.hash ?? musicInfo.songmid;
    const r = await httpFetch(`http://127.0.0.1:{{Port}}/url/${source}/${songId}/${quality}`, {
        method: 'GET', headers: { 'Content-Type': 'application/json' },
    });
    if (!r.body || isNaN(Number(r.body.code))) throw new Error('unknown error');
    return r.body.url;
};
const musicSources = {};
['wy','kw','kg','tx','mg'].forEach(s => { musicSources[s] = { name: s, type: 'music', actions: ['musicUrl'], qualitys: ['128k','320k'] }; });
on(EVENT_NAMES.request, ({ action, source, info }) => {
    if (action === 'musicUrl') return handleGetMusicUrl(source, info.musicInfo, info.type);
    return Promise.reject('action not support');
});
send(EVENT_NAMES.inited, { status: true, sources: musicSources });
""";
    var host = new LxScriptHost();
    Check("脚本加载+inited", await host.RunAsync(mockJs));
    Check("脚本声明 5 个源", host.Sources is { SourceCodes.Count: 5 }, host.Sources?.SourceCodes.Count.ToString());
    Check("脚本支持 wy/musicUrl", host.Supports("wy", "musicUrl"));
    Check("脚本不支持 wy/musicSearch", !host.Supports("wy", "musicSearch"));

    var scriptUrl = await host.GetMusicUrlAsync("wy", "S1", "Test", "Tester", 240, "320k");
    Check("脚本 musicUrl 解析（wy 320k）", scriptUrl == "http://media.example.com/wy_320.mp3", scriptUrl);
    var scriptUrl128 = await host.GetMusicUrlAsync("kw", "K9", "Test", "Tester", 100, "128k");
    Check("脚本 musicUrl 解析（kw 128k）", scriptUrl128 == "http://media.example.com/kw_128.mp3", scriptUrl128);

    // 平台码映射
    Check("netease→wy", LxPlatformCodes.ToShort("netease") == "wy");
    Check("tx→qq", LxPlatformCodes.ToFull("tx") == "qq");
    Check("bilibili 未知码直传", LxPlatformCodes.ToShort("bilibili") == "bilibili");

    // ── 阶段 4：歌单/榜单/歌单搜索 数据链路（酷我公开 API，无需脚本）──
    Console.WriteLine("\n[phase4] 歌单/榜单/歌单搜索 数据链路（酷我公开 API，无签名）");

    // 4.1 酷我榜单列表硬编码 25 个（GetBoards）
    var boards = LxKuwoApi.GetBoards();
    Check("酷我榜单列表非空", boards != null && boards.Count >= 20,
        boards?.Count.ToString() ?? "null");
    Check("榜单含热歌榜(bangId=16)", boards?.Any(b => b.BangId == "16") == true);
    Check("榜单含飙升榜(bangId=93)", boards?.Any(b => b.BangId == "93") == true);

    // 4.2 热歌榜(bangId=16)前两页能取到歌曲（榜单的 GetPlaylistSongs 走 GetBoardSongsAsync）
    //     id 格式 "kw__<bangId>"，Id 是榜单唯一主键，BangId 是接口参数。
    var hotBoard = boards!.First(b => b.BangId == "16");
    Check("热歌榜 id 含 bangId", hotBoard.Id.Contains("16"));
    // 模拟 IOnlineMusicPlugin.GetPlaylistSongsAsync：榜单 Id 里含 "bang" 走 GetBoardSongsAsync。
    var hotSongs = await LxKuwoApi.GetBoardSongsAsync(hotBoard.BangId, 1, 16);
    Check("热歌榜前 16 条非空", hotSongs is { Count: >= 10 }, hotSongs?.Count.ToString() ?? "null");
    if (hotSongs is { Count: > 0 })
    {
        var first = hotSongs[0];
        Check("榜单歌曲 Id 格式 kw:xxx", first.Id.StartsWith("kw:", StringComparison.OrdinalIgnoreCase), first.Id);
        Check("榜单歌曲 RawId 存在",
            first.Internal != null && first.Internal.TryGetValue("RawId", out var raw) && raw is string rawStr && rawStr.Length > 0);
    }

    // 4.3 插件 IOnlineMusicPlugin.GetToplistsAsync：返回榜单作为 OnlinePlaylist（Platform=lx）
    var lxPluginToplists = await lxPlugin.GetToplistsAsync();
    Check("插件 GetToplistsAsync 返回非空", lxPluginToplists is { Count: >= 20 }, lxPluginToplists?.Count.ToString() ?? "null");
    if (lxPluginToplists is { Count: > 0 })
    {
        var top = lxPluginToplists[0];
        Check("榜单 Platform=lx", top.Platform == "lx", top.Platform);
        Check("榜单 Id 不空", top.Id.Length > 0, top.Id);
        Check("榜单 Name 不空", top.Name.Length > 0, top.Name);
    }

    // 4.4 歌单列表：酷我默认分类 + 排序（最新/最热）。GetPlaylistsAsync(category, sort)。
    //     先不带参数（默认分类 + 最热）：要求拿到一页歌单（>=3）。
    const int PAGE_SIZE = 12;
    var defaultPlaylists = await lxPlugin.GetPlaylistsAsync(null);
    Check("插件 GetPlaylistsAsync(null) 返回 >= 3 个", defaultPlaylists is { Count: >= 3 },
        defaultPlaylists?.Count.ToString() ?? "null");
    if (defaultPlaylists is { Count: > 0 })
    {
        var p = defaultPlaylists[0];
        Check("歌单 Name 不空", p.Name.Length > 0, p.Name);
        Check("歌单 CoverUrl 不空（含 http）", !string.IsNullOrEmpty(p.CoverUrl) && p.CoverUrl!.StartsWith("http"), p.CoverUrl);
        Check("歌单 Platform=lx", p.Platform == "lx", p.Platform);
        Check("歌单 Id 不空（酷我歌单格式 pl<数字>）", p.Id.Length > 0, p.Id);
    }

    // 4.5 歌单内歌曲：挑上面第一个歌单取前 8 条。
    if (defaultPlaylists is { Count: > 0 })
    {
        var firstPl = defaultPlaylists[0];
        var firstSongs = await lxPlugin.GetPlaylistSongsAsync(firstPl, 1, 8);
        Check($"歌单「{firstPl.Name}」内歌曲 >= 3", firstSongs is { Count: >= 3 },
            firstSongs?.Count.ToString() ?? "null");
    }

    // 4.6 歌单搜索：IOnlineMusicPlugin.SearchPlaylistsAsync("黄诗扶") —— 新接口（默认实现返回空，要实装后通过）。
    //     先调用，要求至少拿到 1 个歌单，歌单名含「黄诗扶」或作者名。
    var kwSearch = typeof(LxKuwoApi).GetMethod("SearchPlaylistsAsync");
    Check("LxKuwoApi 已公开 SearchPlaylistsAsync", kwSearch != null);
    var kwSearchTask = (Task<List<OnlinePlaylist>?>?)kwSearch?.Invoke(null, new object?[] { "黄诗扶", 1, 12 });
    var kwPlaylists = kwSearchTask != null ? await kwSearchTask : null;
    Check("酷我歌单搜索「黄诗扶」至少 1 个", kwPlaylists is { Count: >= 1 },
        kwPlaylists?.Count.ToString() ?? "null");

    var pluginSpMethod = typeof(IOnlineMusicPlugin).GetMethod("SearchPlaylistsAsync");
    if (pluginSpMethod != null)
    {
        Task<List<OnlinePlaylist>?>? plTask = null;
        try
        {
            // 优先走接口默认方法（DIM）
            plTask = (Task<List<OnlinePlaylist>?>?)pluginSpMethod.Invoke(lxPlugin, new object?[] { "黄诗扶", 1, 12 });
        }
        catch { }
        // DIM 在某些「插件程序集编译时还没有该接口方法」的 AssemblyLoad 场景下
        // 会直接命中接口默认 null 实现；此时回退到类自身的同名公共方法（如果存在）
        List<OnlinePlaylist>? plSearch = null;
        if (plTask != null) plSearch = await plTask;
        if (plSearch == null || plSearch.Count == 0)
        {
            var clsM = lxPlugin.GetType().GetMethod("SearchPlaylistsAsync",
                new[] { typeof(string), typeof(int), typeof(int) });
            if (clsM != null)
            {
                var fallback = (Task<List<OnlinePlaylist>?>?)clsM.Invoke(lxPlugin, new object?[] { "黄诗扶", 1, 12 });
                if (fallback != null) plSearch = await fallback;
            }
        }
        Check("插件 SearchPlaylistsAsync(黄诗扶) >= 1 个", plSearch is { Count: >= 1 },
            plSearch?.Count.ToString() ?? "null");
    }
    else
    {
        // 若 Core 尚未加接口：记录警告但继续（后续改 Core 后会重新生效）
        Console.WriteLine("  [warn] IOnlineMusicPlugin 暂未公开 SearchPlaylistsAsync，宿主 Core 更新后会补齐此测试");
    }

    // ── 阶段 5：真实混淆脚本加载（长青SVIP v1.2.0，若文件存在）──
    var realScriptPath = @"C:\Users\lvjin\AppData\Local\Temp\长青SVIP音源(二改修复版) v1.2.0.js";
    if (File.Exists(realScriptPath))
    {
        Console.WriteLine("\n[phase5] 真实混淆脚本加载（长青SVIP v1.2.0）");
        var realHost = new LxScriptHost();
        var realOk = await realHost.LoadFromFileAsync(realScriptPath);
        // 脚本有 checkUpdate：可能 inited 正常，或检测到新版发 updateAlert
        Check("混淆脚本 inited 或有更新提示", realOk || realHost.HasUpdateAlert, realHost.LastError);
        if (realOk)
        {
            Check("混淆脚本声明 4 源", realHost.Sources is { SourceCodes.Count: 4 },
                realHost.Sources?.SourceCodes.Count.ToString());
            Check("混淆脚本支持 wy/musicUrl", realHost.Supports("wy", "musicUrl"));
            Check("混淆脚本支持 kg/musicUrl", realHost.Supports("kg", "musicUrl"));
            Check("混淆脚本支持 tx/musicUrl", realHost.Supports("tx", "musicUrl"));
            Check("混淆脚本支持 kw/musicUrl", realHost.Supports("kw", "musicUrl"));
        }
        else if (realHost.HasUpdateAlert)
        {
            Console.WriteLine($"  (脚本检测到新版：{realHost.UpdateMessage} → 跳过源声明检查)");
        }
    }
    else
    {
        Console.WriteLine("\n[phase4] 跳过（未找到长青SVIP 脚本文件）");
    }
}
finally
{
    listener.Stop();
}

Console.WriteLine($"\n结果：{passed} 通过 / {failed} 失败");
return failed == 0 ? 0 : 1;
