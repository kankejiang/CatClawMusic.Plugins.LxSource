using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐插件：内嵌 Jint 引擎运行 lx-music 自定义源 .js 脚本，支持在线导入与本地导入。
/// 脚本声明源（kg/tx/wy/kw 等）与 action（musicUrl/musicSearch/lyric/pic），插件按声明分发。
/// <para>
/// 实现 <see cref="IViewContributorPlugin"/>：贡献「LX 源音乐」入口页面（脚本导入 + 搜索 + 播放），
/// <see cref="ILyricsProviderPlugin"/>：宿主歌词兜底链按 RemoteId（"lx:source:id"）路由。
/// </para>
/// </summary>
public class LxMusicPlugin : IOnlineMusicPlugin, IViewContributorPlugin, ILyricsProviderPlugin
{
    private LxConfig _config = new();
    private LxScriptHost? _script;

    /// <summary>整页 VM 插件级单例（配置与搜索状态跨页面保留）</summary>
    private static LxOnlineMusicViewModel? _sharedVm;

    /// <summary>封面并发上限（保护脚本 API 免被搜索页 N 个 pic 请求打爆限流）</summary>
    private static readonly SemaphoreSlim CoverGate = new(4, 4);

    public string PluginId => "lxSource";
    public string Name => "LX 源音乐";
    public string Version => "0.3.0";
    public string Author => "CatClawMusic";
    public string Description => "内嵌 Jint 引擎运行 lx-music 自定义源 .js 脚本（在线/本地导入）：支持网易云/QQ/酷我/酷狗等，播放直链/歌词（原文+翻译+罗马音）/封面/多音质";
    public List<string> Capabilities => new() { "search", "play", "lyrics", "roma", "quality", "script" };

    /// <summary>来源平台标识（RemoteId 前缀 "lx:"）</summary>
    public string PlatformName => "lx";

    // ── IViewContributorPlugin ──

    public string EntryTitle => "LX 源音乐";
    public string EntryIcon => "🎵";

    public object CreateEntryPage(IServiceProvider services)
    {
        var vm = GetSharedVm(services);
        return new LxOnlineMusicPage(vm, services);
    }

    private LxOnlineMusicViewModel GetSharedVm(IServiceProvider services)
    {
        if (_sharedVm != null) return _sharedVm;
        _sharedVm = new LxOnlineMusicViewModel(this, services);
        return _sharedVm;
    }

    // ── 生命周期与配置 ──

    public Task InitializeAsync()
    {
        _config = LxConfigStore.Load();
        // 恢复上次导入的脚本（本地优先，文件不在则试在线地址）
        if (!string.IsNullOrWhiteSpace(_config.ScriptFilePath) && File.Exists(_config.ScriptFilePath))
            _ = LoadScriptFromFileAsync(_config.ScriptFilePath);
        else if (!string.IsNullOrWhiteSpace(_config.ScriptUrl))
            _ = LoadScriptAsync(_config.ScriptUrl);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        _sharedVm = null;
        _script?.Dispose();
        _script = null;
        return Task.CompletedTask;
    }

    public LxConfig Config => _config;

    /// <summary>脚本宿主（UI 查询加载状态/源能力用）</summary>
    public LxScriptHost? Script => _script;

    /// <summary>脚本源是否就绪（已加载并声明了源）</summary>
    public bool ScriptReady => _script?.IsLoaded == true;

    /// <summary>在线导入：下载 .js 并执行</summary>
    public async Task<bool> LoadScriptAsync(string jsUrl)
    {
        if (string.IsNullOrWhiteSpace(jsUrl))
        {
            _script?.Dispose();
            _script = null;
            return false;
        }
        _script ??= new LxScriptHost();
        var ok = await _script.LoadAsync(jsUrl);
        if (!ok) _script = null;
        return ok;
    }

    /// <summary>本地导入：读取本地 .js 文件并执行</summary>
    public async Task<bool> LoadScriptFromFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _script?.Dispose();
            _script = null;
            return false;
        }
        _script ??= new LxScriptHost();
        var ok = await _script.LoadFromFileAsync(filePath);
        if (!ok) _script = null;
        return ok;
    }

    /// <summary>保存配置（音质 + 默认源 + 脚本地址/路径）</summary>
    public void SaveConfig(int qualityLevel, string defaultSource, string scriptUrl, string scriptFilePath)
    {
        _config.QualityLevel = Math.Clamp(qualityLevel, 0, 2);
        _config.DefaultSource = defaultSource ?? "";
        _config.ScriptUrl = (scriptUrl ?? "").Trim();
        _config.ScriptFilePath = (scriptFilePath ?? "").Trim();
        LxConfigStore.Save(_config);
    }

    /// <summary>音质档位 → lx 自定义源脚本 quality 字符串（0=128k 1=320k 2=FLAC）</summary>
    public static string LxQualityFor(int quality) => quality switch
    {
        0 => "128k",
        1 => "320k",
        _ => "flac",
    };

    // ── IOnlineMusicPlugin ──

    /// <summary>搜索歌曲（结果带封面；仅脚本 musicSearch action）。脚本不支持则返回 null</summary>
    public async Task<List<OnlineSong>?> SearchAsync(string keyword, int page = 1, int pageSize = 20)
    {
        if (!ScriptReady) return null;
        var sources = _script!.Sources!;
        var wantShort = LxPlatformCodes.ToShort(_config.DefaultSource);
        var canSearch = string.IsNullOrEmpty(_config.DefaultSource)
            ? sources.SourceCodes.Any(c => sources.Supports(c, "musicSearch"))
            : sources.Supports(wantShort, "musicSearch");
        if (!canSearch) return null;

        var codes = string.IsNullOrEmpty(_config.DefaultSource)
            ? sources.SourceCodes.Where(c => sources.Supports(c, "musicSearch")).ToList()
            : new List<string> { wantShort };
        var all = new List<OnlineSong>();
        foreach (var code in codes)
        {
            var r = await _script.SearchAsync(keyword, page, pageSize, code);
            if (r == null) continue;
            foreach (var it in r) all.Add(ToOnlineSongFromScript(it, code));
        }
        if (all.Count == 0) return all;
        await ResolveCoversAsync(all);
        return all;
    }

    /// <summary>获取播放直链（仅脚本 musicUrl action；不支持则 null）</summary>
    public async Task<string?> GetPlayUrlAsync(OnlineSong song, int quality = 0)
    {
        if (!ScriptReady) return null;
        var s = ToLxSong(song);
        if (s == null || string.IsNullOrWhiteSpace(s.Source)) return null;
        var code = LxPlatformCodes.ToShort(s.Source);
        if (!_script!.Supports(code, "musicUrl")) return null;
        var q = quality > 0 ? quality : _config.QualityLevel;
        return await _script.GetMusicUrlAsync(code, s.Id, s.Name, s.Artist, s.IntervalSeconds, LxQualityFor(q));
    }

    /// <summary>获取歌词（原文 + 译文 + 罗马音三流，仅脚本 lyric action）</summary>
    public async Task<(string? Lrc, string? TLrc, string? RLrc)?> GetLyricsWithRomaAsync(OnlineSong song)
    {
        if (!ScriptReady) return null;
        var s = ToLxSong(song);
        if (s == null || string.IsNullOrWhiteSpace(s.Source)) return null;
        var code = LxPlatformCodes.ToShort(s.Source);
        if (!_script!.Supports(code, "lyric")) return null;
        return await _script.GetLyricAsync(code, s.Id, s.Name, s.Artist, s.IntervalSeconds);
    }

    /// <summary>获取歌词（原文 + 译文；接口要求）</summary>
    public async Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(OnlineSong song)
    {
        var r = await GetLyricsWithRomaAsync(song);
        return r == null ? null : (r.Value.Lrc, r.Value.TLrc);
    }

    public Task<List<OnlinePlaylist>> GetPlaylistsAsync(string? category = null)
        => Task.FromResult(new List<OnlinePlaylist>());

    public Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 50)
        => Task.FromResult<List<OnlineSong>?>(null);

    // ── ILyricsProviderPlugin：宿主歌词兜底链（RemoteId "lx:source:id" 路由）──

    /// <summary>歌词服务可用（脚本已加载且支持 lyric）</summary>
    public bool IsAvailable => ScriptReady;

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

    /// <summary>脚本 musicSearch 结果 → OnlineSong（Id 用 "全名:rawId"）</summary>
    private static OnlineSong ToOnlineSongFromScript(LxScriptSearchItem it, string sourceCode)
    {
        var full = LxPlatformCodes.ToFull(sourceCode);
        return new OnlineSong
        {
            Id = $"{full}:{it.Id}",
            Platform = "lx",
            PlatformName = "LX 源音乐",
            Title = it.Name,
            Artist = it.Artist,
            Album = it.Album,
            DurationMs = it.IntervalSeconds * 1000,
            Internal = new Dictionary<string, object>
            {
                ["Source"] = full,
                ["RawId"] = it.Id,
            },
        };
    }

    /// <summary>
    /// OnlineSong → LxSong（提取 source + raw id 供脚本 musicUrl/lyric/pic 调用）。
    /// Id 兼容复合 "source:id" 或裸 "id"（无 Internal 时按冒号拆分）。
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
        return new LxSong
        {
            Id = id,
            Source = source,
            Name = os.Title ?? "",
            Artist = os.Artist ?? "",
            Album = os.Album ?? "",
            IntervalSeconds = os.DurationMs > 0 ? os.DurationMs / 1000 : 0,
        };
    }

    /// <summary>并发解析搜索结果封面（脚本 pic action，全局 ~4.5s 超时）</summary>
    private async Task ResolveCoversAsync(List<OnlineSong> list)
    {
        if (list.Count == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
        var tasks = list.Select(os => ResolveCoverAsync(os, cts.Token)).ToArray();
        try { await Task.WhenAll(tasks); } catch { }
    }

    private async Task ResolveCoverAsync(OnlineSong os, CancellationToken ct)
    {
        if (!ScriptReady) return;
        var s = ToLxSong(os);
        if (s == null || string.IsNullOrWhiteSpace(s.Source)) return;
        var code = LxPlatformCodes.ToShort(s.Source);
        if (!_script!.Supports(code, "pic")) return;
        await CoverGate.WaitAsync(ct);
        try
        {
            var url = await _script.GetPicUrlAsync(code, s.Id, s.Name, s.Artist, s.IntervalSeconds, ct);
            if (!string.IsNullOrWhiteSpace(url)) os.CoverUrl = url;
        }
        catch { }
        finally { CoverGate.Release(); }
    }
}
