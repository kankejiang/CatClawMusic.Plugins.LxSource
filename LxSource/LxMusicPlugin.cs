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

    /// <summary>脚本内容持久化文件（{数据目录}/scripts/lx_source.js）：
    /// 导入成功后把脚本原文写到此私有文件，重启后从这里恢复，不依赖原始临时路径/在线 URL。</summary>
    private static readonly string ScriptPersistPath = Path.Combine(LxConfigStore.DataDir, "scripts", "lx_source.js");

    /// <summary>整页 VM 插件级单例（配置与搜索状态跨页面保留）</summary>
    private static LxOnlineMusicViewModel? _sharedVm;

    /// <summary>封面并发上限（保护脚本 API 免被搜索页 N 个 pic 请求打爆限流）</summary>
    private static readonly SemaphoreSlim CoverGate = new(4, 4);

    public string PluginId => "lxSource";
    public string Name => "LX 源音乐";
    public string Version => "0.4.0";
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
        else PersistScript();
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
        else PersistScript();
        return ok;
    }

    /// <summary>把已加载脚本原文写入私有持久文件，并把配置指向它（重启即可恢复）。</summary>
    private void PersistScript()
    {
        var code = _script?.ScriptCode;
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            var dir = Path.GetDirectoryName(ScriptPersistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ScriptPersistPath, code);
            _config.ScriptFilePath = ScriptPersistPath;
            _config.ScriptUrl = ""; // 已本地持久化，无需再依赖在线 URL
            LxConfigStore.Save(_config);
        }
        catch
        {
            // 持久化失败不阻塞（本次仍可用，仅重启后需重导）
        }
    }

    /// <summary>清除脚本：删除持久化文件并清空配置。</summary>
    public void ClearScript()
    {
        _script?.Dispose();
        _script = null;
        try { if (File.Exists(ScriptPersistPath)) File.Delete(ScriptPersistPath); } catch { }
        _config.ScriptUrl = "";
        _config.ScriptFilePath = "";
        LxConfigStore.Save(_config);
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

    /// <summary>获取播放直链（仅脚本 musicUrl action；不支持或被禁用则 null）。
    /// quality 语义：>=0 直接用（0=128k 1=320k 2=FLAC）；负数使用配置档位。
    /// 音质按脚本声明降级 + 失败逐级重试（VIP 歌曲 flac 常取不到，自动降到 320k/128k）。</summary>
    public async Task<string?> GetPlayUrlAsync(OnlineSong song, int quality = -1)
    {
        if (!ScriptReady) return null;
        var s = ToLxSong(song);
        if (s == null || string.IsNullOrWhiteSpace(s.Source)) return null;
        var code = LxPlatformCodes.ToShort(s.Source);
        if (!IsSourceEnabled(code)) return null;
        if (!_script!.Supports(code, "musicUrl")) return null;
        var q = quality >= 0 ? quality : _config.QualityLevel;

        foreach (var level in ResolveQualityOrder(code, q))
        {
            var url = await _script.GetMusicUrlAsync(code, s.Id, s.Name, s.Artist, s.IntervalSeconds, level);
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }
        return null;
    }

    // ── 源启停（脚本声明多源时单独启用/关闭）──

    /// <summary>源是否启用（未在 DisabledSources 中视为启用）。code 为短码（kw/kg/tx/wy/mg）。</summary>
    public bool IsSourceEnabled(string code) =>
        !string.IsNullOrEmpty(code) && !_config.DisabledSources.Contains(code, StringComparer.OrdinalIgnoreCase);

    /// <summary>设置源启用/禁用（立即持久化）。</summary>
    public void SetSourceEnabled(string code, bool enabled)
    {
        if (string.IsNullOrEmpty(code)) return;
        var list = _config.DisabledSources;
        if (enabled) list.RemoveAll(x => x.Equals(code, StringComparison.OrdinalIgnoreCase));
        else if (!list.Contains(code, StringComparer.OrdinalIgnoreCase)) list.Add(code);
        LxConfigStore.Save(_config);
    }

    /// <summary>音质尝试顺序：从请求档开始，flac → 320k → 128k 逐级降级，
    /// 跳过脚本未声明的音质（脚本 qualitys 未声明时返回原始档位）。</summary>
    private List<string> ResolveQualityOrder(string code, int q)
    {
        var want = LxQualityFor(q);
        var qualitys = _script?.Sources?.Qualitys(code);
        var all = new[] { "flac", "320k", "128k" };
        var startIdx = Array.IndexOf(all, want);
        if (startIdx < 0) startIdx = 0;
        var order = new List<string>();
        for (var i = startIdx; i < all.Length; i++)
        {
            if (qualitys == null || qualitys.Count == 0 || qualitys.Contains(all[i]))
                order.Add(all[i]);
        }
        return order.Count > 0 ? order : new List<string> { want };
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

    public async Task<List<OnlinePlaylist>> GetPlaylistsAsync(string? category = null)
    {
        // 若未加载脚本，也不影响：歌单、榜单、搜索全部走内置酷我 API
        // category == null：推荐歌单（默认最热）；否则：分类标签id
        var r = await LxKuwoApi.GetPlaylistsAsync(category, page: 1, pageSize: 20, sort: "hot").ConfigureAwait(false);
        // Fail-safe：若 API 偶发失败，返回空列表而不是 null，接口契约是 Task<List<OnlinePlaylist>>
        return r ?? new List<OnlinePlaylist>();
    }

    /// <summary>最新歌单（非接口方法，UI 里「最新/最热」切换时调用）。</summary>
    public async Task<List<OnlinePlaylist>> GetPlaylistsNewAsync(string? category = null, int page = 1, int pageSize = 20)
    {
        var r = await LxKuwoApi.GetPlaylistsAsync(category, page, pageSize, sort: "new").ConfigureAwait(false);
        return r ?? new List<OnlinePlaylist>();
    }

    /// <summary>歌单搜索（对应图里搜索页的「歌单」tab，插件侧扩展方法，宿主会用反射/适配调用）。</summary>
    public async Task<List<OnlinePlaylist>?> SearchPlaylistsAsync(string keyword, int page = 1, int pageSize = 20)
        => await LxKuwoApi.SearchPlaylistsAsync(keyword, page, pageSize).ConfigureAwait(false);

    /// <summary>榜单（排行榜页：飙升榜/新歌榜/热歌榜/抖音热歌榜…）。
    /// 项 Id="kw__&lt;bangId&gt;"，<see cref="GetPlaylistSongsAsync"/> 据此切换到 kbangserver 拉榜单歌曲。</summary>
    public async Task<List<OnlinePlaylist>> GetToplistsAsync()
    {
        var boards = LxKuwoApi.GetBoards();
        // 并行拉 25 个榜单头信息（每个请求极小 ~1KB），比串行快 10x+
        var tasks = new Task<(LxBoardItem Board, (string Name, string? Cover, string? Info, int Num)? Head)>[boards.Count];
        for (var i = 0; i < boards.Count; i++)
        {
            var b = boards[i];
            tasks[i] = WrapHead(b);
        }
        static async Task<(LxBoardItem Board, (string Name, string? Cover, string? Info, int Num)? Head)> WrapHead(LxBoardItem bb)
        {
            try { return (bb, await LxKuwoApi.GetBoardHeadAsync(bb.BangId).ConfigureAwait(false)); }
            catch { return (bb, null); }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var list = new List<OnlinePlaylist>(tasks.Length);
        foreach (var t in tasks)
        {
            var (b, head) = t.Result;
            list.Add(new OnlinePlaylist
            {
                Id = b.Id,
                Platform = "lx",
                Name = head.HasValue && !string.IsNullOrEmpty(head.Value.Name) ? head.Value.Name : b.Name,
                Description = head.HasValue && !string.IsNullOrEmpty(head.Value.Info) ? head.Value.Info : $"酷我音乐 · {b.Name}",
                CoverUrl = head.HasValue ? head.Value.Cover : null,
                SongCount = head.HasValue && head.Value.Num > 0 ? head.Value.Num : 300,
            });
        }
        return list;
    }

    public async Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 50)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id)) return null;
        return await LxKuwoApi.GetPlaylistSongsAsync(playlist.Id, page, pageSize).ConfigureAwait(false);
    }

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
            // Internal 已有 Source（酷我搜索/榜单歌曲）：id 优先取裸 RawId；
            // 没有 RawId 时从 "source:id" 复合 id 拆出冒号后部分。
            // ⚠ 不能直接用 os.Id——它带 "kw:" 前缀，酷我 API/脚本会把整个当 songmid 请求 → 取不到播放链接。
            if (os.Internal.TryGetValue("RawId", out var rid) && rid is string rawStr && !string.IsNullOrWhiteSpace(rawStr))
            {
                id = rawStr;
            }
            else
            {
                var idx = os.Id.IndexOf(':');
                if (idx > 0) id = os.Id[(idx + 1)..];
            }
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
