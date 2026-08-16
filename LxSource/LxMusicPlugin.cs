using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐插件：兼容 lx-music-api-server 协议，一个可配置的服务器地址即可接入任意
/// LX 音乐源（网易云 / QQ / 酷我 / 酷狗 / 咪咕 / 哔哩哔哩 等），覆盖搜索 / 播放直链 /
/// 歌词（原文 + 译文 + 罗马音三流）/ 封面 / 多音质。
/// <para>
/// 同时实现 <see cref="IViewContributorPlugin"/>：向宿主贡献完整的"LX 源音乐"入口页面
/// （服务器配置 + 搜索 + 播放），以及 <see cref="ILyricsProviderPlugin"/>：让宿主歌词
/// 兜底链能消费 lx 在线歌词（RemoteId 形如 "lx:source:id"）。
/// </para>
/// </summary>
public class LxMusicPlugin : IOnlineMusicPlugin, IViewContributorPlugin, ILyricsProviderPlugin
{
    private readonly LxApiClient _client = new();
    private LxConfig _config = new();

    /// <summary>整页 VM 插件级单例（配置与搜索状态跨页面保留）</summary>
    private static LxOnlineMusicViewModel? _sharedVm;

    /// <summary>封面并发上限（保护公共镜像服务器免被搜索页 N 个 /pic 请求打爆限流）</summary>
    private static readonly SemaphoreSlim CoverGate = new(4, 4);

    public string PluginId => "lxSource";
    public string Name => "LX 源音乐";
    public string Version => "0.1.0";
    public string Author => "CatClawMusic";
    public string Description => "兼容 lx-music-api-server 协议：一个服务器地址接入任意 LX 音乐源（网易云/QQ/酷我/酷狗/咪咕/哔哩哔哩等），搜索/播放/歌词（原文+翻译+罗马音）/封面/多音质";
    public List<string> Capabilities => new() { "search", "play", "lyrics", "roma", "quality" };

    /// <summary>来源平台标识（RemoteId 前缀 "lx:"）</summary>
    public string PlatformName => "lx";

    // ── IViewContributorPlugin ──

    /// <summary>发现页入口显示标题</summary>
    public string EntryTitle => "LX 源音乐";

    /// <summary>发现页入口图标（emoji 兼容）</summary>
    public string EntryIcon => "🎵";

    /// <summary>创建入口页面实例（宿主 Push 到导航栈）</summary>
    public object CreateEntryPage(IServiceProvider services)
    {
        var vm = GetSharedVm(services);
        return new LxOnlineMusicPage(vm, services);
    }

    /// <summary>获取插件级单例 VM（入口页面共用一份配置与搜索状态）</summary>
    private LxOnlineMusicViewModel GetSharedVm(IServiceProvider services)
    {
        if (_sharedVm != null) return _sharedVm;
        _sharedVm = new LxOnlineMusicViewModel(this, services);
        return _sharedVm;
    }

    // ── 生命周期与配置（插件 UI 调用）──

    public Task InitializeAsync()
    {
        _config = LxConfigStore.Load();
        _client.SetServerUrl(_config.ServerUrl);
        return Task.CompletedTask;
    }

    /// <summary>关闭：释放插件级单例 VM（禁用后重启用会按新实例重建，避免悬挂旧引用）</summary>
    public Task ShutdownAsync()
    {
        _sharedVm = null;
        return Task.CompletedTask;
    }

    /// <summary>当前配置（插件 UI 读写）</summary>
    public LxConfig Config => _config;

    /// <summary>HTTP 客户端（连接测试用）</summary>
    public LxApiClient Client => _client;

    /// <summary>保存配置并立即生效（校验由 UI 完成）</summary>
    public void SaveConfig(string serverUrl, int qualityLevel, string defaultSource)
    {
        _config.ServerUrl = (serverUrl ?? "").Trim().TrimEnd('/');
        _config.QualityLevel = Math.Clamp(qualityLevel, 0, 2);
        _config.DefaultSource = defaultSource ?? "";
        _client.SetServerUrl(_config.ServerUrl);
        LxConfigStore.Save(_config);
    }

    /// <summary>音质档位 → lx 协议 br 参数（0=128k 1=320k 2=FLAC）</summary>
    public static string BrForQuality(int quality) => quality switch
    {
        0 => "128",
        1 => "320",
        _ => "flac",
    };

    // ── IOnlineMusicPlugin ──

    /// <summary>搜索歌曲（结果带封面；失败返回 null）</summary>
    public async Task<List<OnlineSong>?> SearchAsync(string keyword, int page = 1, int pageSize = 20)
    {
        var songs = await _client.SearchAsync(keyword, page, pageSize, _config.DefaultSource);
        if (songs == null) return null;
        var list = new List<OnlineSong>(songs.Count);
        foreach (var s in songs) list.Add(ToOnlineSong(s));
        await ResolveCoversAsync(list);
        return list;
    }

    /// <summary>获取播放直链（音质档位：0 默认/1 高品/2 无损）</summary>
    public async Task<string?> GetPlayUrlAsync(OnlineSong song, int quality = 0)
    {
        var s = ToLxSong(song);
        if (s == null) return null;
        return await _client.GetSongUrlAsync(s, BrForQuality(quality > 0 ? quality : _config.QualityLevel));
    }

    /// <summary>获取歌词（原文 + 译文）</summary>
    public async Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(OnlineSong song)
    {
        var r = await GetLyricsWithRomaAsync(song);
        return r == null ? null : (r.Value.Lrc, r.Value.TLrc);
    }

    /// <summary>获取歌词（原文 + 译文 + 罗马音三流，lx-music 音源同款）</summary>
    public async Task<(string? Lrc, string? TLrc, string? RLrc)?> GetLyricsWithRomaAsync(OnlineSong song)
    {
        var s = ToLxSong(song);
        if (s == null) return null;
        return await _client.GetLyricsAsync(s);
    }

    /// <summary>lx 协议无歌单能力（lx-music-api-server 无歌单端点）</summary>
    public Task<List<OnlinePlaylist>> GetPlaylistsAsync(string? category = null)
        => Task.FromResult(new List<OnlinePlaylist>());

    public Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 50)
        => Task.FromResult<List<OnlineSong>?>(null);

    // ── ILyricsProviderPlugin：宿主歌词兜底链（RemoteId "lx:source:id" 路由）──

    /// <summary>歌词服务可用（已配置服务器）</summary>
    public bool IsAvailable => _client.HasServer;

    /// <summary>
    /// 宿主兜底链调用：仅处理 RemoteId 形如 "lx:source:id" 的歌曲，
    /// 拉取三流歌词并按时间戳合并为结构化 <see cref="LrcLyrics"/>。
    /// </summary>
    public async Task<LrcLyrics?> GetLyricsAsync(Song song)
    {
        if (song?.RemoteId == null || !song.RemoteId.StartsWith("lx:", StringComparison.OrdinalIgnoreCase))
            return null;
        var onlineId = song.RemoteId.Length > 3 ? song.RemoteId[3..] : "";
        if (string.IsNullOrWhiteSpace(onlineId)) return null;
        var os = new OnlineSong
        {
            Id = onlineId,
            Platform = "lx",
            Title = song.Title ?? "",
            Artist = song.Artist ?? "",
            Album = song.Album ?? "",
        };
        var pair = await GetLyricsWithRomaAsync(os);
        if (pair == null || string.IsNullOrWhiteSpace(pair.Value.Lrc)) return null;
        return LxLrcParser.Parse(pair.Value.Lrc, pair.Value.TLrc, pair.Value.RLrc);
    }

    // ── 模型转换 ──

    /// <summary>LxSong → OnlineSong（Id 用复合形式 "source:id"，歌词/直链路由需要）</summary>
    private static OnlineSong ToOnlineSong(LxSong s) => new()
    {
        Id = string.IsNullOrWhiteSpace(s.Source) ? s.Id : $"{s.Source}:{s.Id}",
        Platform = "lx",
        PlatformName = "LX 源音乐",
        Title = s.Name,
        Artist = s.Artist,
        Album = s.Album,
        DurationMs = s.IntervalSeconds * 1000,
        Internal = new Dictionary<string, object>
        {
            ["Source"] = s.Source,
            ["RawId"] = s.Id,
            ["PicId"] = s.PicId,
            ["UrlId"] = s.UrlId,
        },
    };

    /// <summary>
    /// OnlineSong → LxSong。
    /// Id 兼容两种形态：复合 "source:id"（本插件搜索结果）或裸 "id"（宿主从
    /// RemoteId 构造时只有 "source:id" 复合形式；无 Internal 时按冒号拆分）。
    /// </summary>
    private static LxSong? ToLxSong(OnlineSong os)
    {
        if (os == null || string.IsNullOrWhiteSpace(os.Id)) return null;
        var source = "";
        var id = os.Id;
        if (os.Internal != null && os.Internal.TryGetValue("Source", out var src) && src is string srcStr && !string.IsNullOrWhiteSpace(srcStr))
        {
            source = srcStr;
        }
        else
        {
            var idx = os.Id.IndexOf(':');
            if (idx > 0)
            {
                source = os.Id[..idx];
                id = os.Id[(idx + 1)..];
            }
        }
        string Get(string key) => os.Internal != null && os.Internal.TryGetValue(key, out var v) && v is string str ? str : "";
        return new LxSong
        {
            Id = id,
            Source = source,
            Name = os.Title ?? "",
            Artist = os.Artist ?? "",
            Album = os.Album ?? "",
            PicId = Get("PicId"),
            UrlId = Get("UrlId"),
            IntervalSeconds = os.DurationMs > 0 ? os.DurationMs / 1000 : 0,
        };
    }

    /// <summary>并发解析搜索结果封面（全局 ~4.5s 超时，失败保留占位图）</summary>
    private async Task ResolveCoversAsync(List<OnlineSong> list)
    {
        if (list.Count == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
        var tasks = list.Select(os => ResolveCoverAsync(os, cts.Token)).ToArray();
        try { await Task.WhenAll(tasks); } catch { /* 个别失败忽略 */ }
    }

    private async Task ResolveCoverAsync(OnlineSong os, CancellationToken ct)
    {
        var s = ToLxSong(os);
        if (s == null) return;
        await CoverGate.WaitAsync(ct);
        try
        {
            var url = await _client.GetPicUrlAsync(s, 300, ct);
            if (!string.IsNullOrWhiteSpace(url)) os.CoverUrl = url;
        }
        catch
        {
        }
        finally
        {
            CoverGate.Release();
        }
    }
}
