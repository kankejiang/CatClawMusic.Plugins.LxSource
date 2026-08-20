using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面 ViewModel：脚本导入（在线 URL / 本地文件）、音源选择、音质切换、播放。
/// <para>设计参考 lx-music-mobile（lyswhut/lx-music-mobile）主页 Main.tsx：
/// PagerView 多 page + DrawerNav 抽屉切换 nav_search/nav_songlist/nav_top/nav_love/nav_setting。</para>
/// <para>插件单页面限制下，把"设置入口独立 nav"演化为右上齿轮 → 底部 sheet；
/// 把"按音源能力渲染不同 view"演化为：根据脚本声明 actions 动态展示能力概览/搜索/歌单占位。</para>
/// </summary>
public partial class LxOnlineMusicViewModel : ObservableObject
{
    private readonly LxMusicPlugin _plugin;
    private readonly IServiceProvider _services;
    private CancellationTokenSource? _tipCts;

    [ObservableProperty]
    private string _scriptUrl = "";

    [ObservableProperty]
    private string _scriptFilePath = "";

    [ObservableProperty]
    private string _scriptStatus = "未导入脚本";

    [ObservableProperty]
    private string _qualityText = "320k";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _tipMessage = "";

    [ObservableProperty]
    private bool _hasTip;

    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>已加载脚本各源能力（用于能力概览卡渲染）。仅含脚本成功 inited 后的源。</summary>
    [ObservableProperty]
    private ObservableCollection<LxCapabilityItem> _capabilities = new();

    /// <summary>能力摘要文本（例：已加载 4 个源 · 4 项能力 / 当前脚本未声明任何源能力）</summary>
    [ObservableProperty]
    private string _capabilitySummary = "";

    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _sourceChips = new();

    /// <summary>脚本是否声明 musicSearch（任何源都行）。true 时主页面显示搜索入口。</summary>
    public bool HasSearchableScript => _plugin.ScriptReady
        && _plugin.Script?.Sources?.SourceCodes.Any(c => _plugin.Script!.Sources.Supports(c, "musicSearch")) == true;

    /// <summary>脚本是否声明歌单/排行榜类 action（topLists / playLists / getTopLists / getPlayLists 等）。
    /// 当前主页面未实现卡片网格 UI，仅作为扩展口返回标志位供后续卡片视图判定。</summary>
    public bool HasCatalogScript => _plugin.ScriptReady
        && _plugin.Script?.Sources?.SourceCodes.Any(c =>
            _plugin.Script!.Sources.Supports(c, "topLists")
            || _plugin.Script!.Sources.Supports(c, "playLists")
            || _plugin.Script!.Sources.Supports(c, "getTopLists")
            || _plugin.Script!.Sources.Supports(c, "getPlayLists")) == true;

    public LxOnlineMusicViewModel(LxMusicPlugin plugin, IServiceProvider services)
    {
        _plugin = plugin;
        _services = services;

        ScriptUrl = plugin.Config.ScriptUrl;
        ScriptFilePath = plugin.Config.ScriptFilePath;
        QualityText = QualityLabel(plugin.Config.QualityLevel);
        RebuildSourceChips();
        RebuildCapabilities();
        ScriptStatus = string.IsNullOrEmpty(ScriptUrl) && string.IsNullOrEmpty(ScriptFilePath)
            ? "未导入脚本" : "待加载";
    }

    private static string QualityLabel(int q) => q switch
    {
        0 => "128k",
        1 => "320k",
        _ => "FLAC",
    };

    /// <summary>根据脚本声明的源重建 chips（自动 + 各声明的源短码映射全名）</summary>
    public void RebuildSourceChips()
    {
        var selected = _plugin.Config.DefaultSource;
        SourceChips.Clear();
        SourceChips.Add(new LxSourceChipItem("自动", string.IsNullOrEmpty(selected)));
        if (_plugin.Script?.Sources != null)
        {
            foreach (var code in _plugin.Script.Sources.SourceCodes)
            {
                var full = LxPlatformCodes.ToFull(code);
                if (string.IsNullOrEmpty(full)) full = code;
                SourceChips.Add(new LxSourceChipItem(full, string.Equals(full, selected, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    /// <summary>重建能力概览（脚本 inited 后重新填充）。</summary>
    public void RebuildCapabilities()
    {
        Capabilities.Clear();
        if (!_plugin.ScriptReady || _plugin.Script?.Sources == null)
        {
            CapabilitySummary = "";
            return;
        }
        var sources = _plugin.Script.Sources;
        var allActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in sources.SourceCodes)
            foreach (var a in sources.ActionsBySource[code])
                allActions.Add(a);
        CapabilitySummary = $"已加载 {sources.SourceCodes.Count} 个源 · 声明 {allActions.Count} 项能力";
        foreach (var code in sources.SourceCodes)
        {
            var name = LxPlatformCodes.ToFull(code);
            if (string.IsNullOrEmpty(name)) name = code;
            var actions = sources.ActionsBySource[code]
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Capabilities.Add(new LxCapabilityItem(name, actions));
        }
    }

    /// <summary>轻提示（自动 3 秒消失）</summary>
    public void ShowTip(string message)
    {
        TipMessage = message;
        HasTip = true;
        _tipCts?.Cancel();
        var cts = _tipCts = new CancellationTokenSource();
        _ = AutoHideTipAsync(cts.Token);
    }

    private async Task AutoHideTipAsync(CancellationToken ct)
    {
        try { await Task.Delay(3000, ct); }
        catch { return; }
        if (!ct.IsCancellationRequested) HasTip = false;
    }

    /// <summary>页面出现时同步脚本状态 + 若已加载重建 chips 与能力</summary>
    public void OnAppearing()
    {
        if (_plugin.ScriptReady)
        {
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源";
            RebuildSourceChips();
            RebuildCapabilities();
        }
        else if (!string.IsNullOrWhiteSpace(_plugin.Config.ScriptFilePath) || !string.IsNullOrWhiteSpace(_plugin.Config.ScriptUrl))
        {
            ScriptStatus = "加载中…";
        }
    }

    // ── 设置 sheet ──

    /// <summary>打开设置 sheet</summary>
    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    /// <summary>关闭设置 sheet</summary>
    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    // ── 脚本导入 ──

    /// <summary>在线导入：下载 .js 并执行</summary>
    [RelayCommand]
    private async Task ImportOnlineAsync()
    {
        var url = (ScriptUrl ?? "").Trim();
        if (url.Length == 0) { ShowTip("请输入脚本地址"); return; }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        { ShowTip("地址需以 http:// 或 https:// 开头"); return; }
        ScriptStatus = "加载中…";
        var ok = await _plugin.LoadScriptAsync(url);
        if (ok)
        {
            _plugin.Config.ScriptUrl = url;
            _plugin.Config.ScriptFilePath = "";
            LxConfigStore.Save(_plugin.Config);
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源（在线）";
            RebuildSourceChips();
            RebuildCapabilities();
            ShowTip($"脚本加载成功，声明 {n} 个源");
        }
        else
        {
            ScriptStatus = "加载失败 ✗";
            ShowTip("脚本加载失败：" + (_plugin.Script?.LastError ?? "未知错误"));
        }
    }

    /// <summary>本地导入（由页面 FilePicker 选文件后调用）</summary>
    public async Task ImportLocalFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { ShowTip("未选择文件"); return; }
        ScriptStatus = "加载中…";
        var ok = await _plugin.LoadScriptFromFileAsync(filePath);
        if (ok)
        {
            _plugin.Config.ScriptFilePath = filePath;
            _plugin.Config.ScriptUrl = "";
            LxConfigStore.Save(_plugin.Config);
            ScriptFilePath = filePath;
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源（本地）";
            RebuildSourceChips();
            RebuildCapabilities();
            ShowTip($"脚本加载成功，声明 {n} 个源");
        }
        else
        {
            ScriptStatus = "加载失败 ✗";
            ShowTip("脚本加载失败：" + (_plugin.Script?.LastError ?? "未知错误"));
        }
    }

    /// <summary>清除脚本</summary>
    [RelayCommand]
    private Task ClearScriptAsync()
    {
        ScriptUrl = "";
        ScriptFilePath = "";
        _plugin.Config.ScriptUrl = "";
        _plugin.Config.ScriptFilePath = "";
        LxConfigStore.Save(_plugin.Config);
        _ = _plugin.LoadScriptAsync("");
        ScriptStatus = "未导入脚本";
        RebuildSourceChips();
        RebuildCapabilities();
        ShowTip("已清除脚本");
        return Task.CompletedTask;
    }

    // ── 音质与音源 ──

    /// <summary>循环切换音质（128k → 320k → FLAC），立即持久化</summary>
    [RelayCommand]
    private void CycleQuality()
    {
        var q = (_plugin.Config.QualityLevel + 1) % 3;
        _plugin.SaveConfig(q, _plugin.Config.DefaultSource, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        QualityText = QualityLabel(q);
        ShowTip($"音质已切换：{QualityText}");
    }

    /// <summary>设置指定音质档（0=128k 1=320k 2=FLAC），立即持久化。供底部 sheet 直接点选使用。</summary>
    [RelayCommand]
    private void SetQuality(string? label)
    {
        var q = label switch
        {
            "128k" => 0,
            "320k" => 1,
            "FLAC" => 2,
            _ => -1,
        };
        if (q < 0 || q == _plugin.Config.QualityLevel) return;
        _plugin.SaveConfig(q, _plugin.Config.DefaultSource, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        QualityText = QualityLabel(q);
        ShowTip($"音质已切换：{QualityText}");
    }

    /// <summary>选择音源（立即持久化）</summary>
    [RelayCommand]
    private void SelectSource(LxSourceChipItem chip)
    {
        foreach (var c in SourceChips) c.IsSelected = ReferenceEquals(c, chip);
        var source = chip.Name == "自动" ? "" : chip.Name;
        _plugin.SaveConfig(_plugin.Config.QualityLevel, source, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        ShowTip($"音源已切换：{chip.Name}");
    }

    // ── 播放（保留：未来搜索结果卡选中播放 / 歌单详情点歌）──

    [RelayCommand]
    private async Task PlaySongAsync(OnlineSong? song)
    {
        if (song == null) return;
        if (!_plugin.ScriptReady) { ShowTip("请先导入脚本"); return; }
        try
        {
            // 单首也走 LxPlaybackHelper（构造单元素队列并调脚本取播放直链）；
            // 未来搜索/歌单列表填满 Songs 时，调用方改为传入 Songs 即可走整列表。
            var played = await LxPlaybackHelper.PlayListAsync(_services, _plugin, new[] { song }, song);
            if (played == 0) ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）");
        }
        catch (Exception ex)
        {
            ShowTip($"播放失败：{ex.Message}");
        }
    }
}

/// <summary>播放队列构造辅助（脚本取播放直链 → 临时负 Id + RemoteId 路由入队）</summary>
public static class LxPlaybackHelper
{
    private static int _idSeq;

    public static Song ToQueueSong(OnlineSong os, string url) => new()
    {
        Id = -(System.Threading.Interlocked.Increment(ref _idSeq) + 1000),
        Title = os.Title,
        Artist = os.Artist,
        Album = os.Album,
        Duration = (int)(os.DurationMs / 1000),
        FilePath = url,
        RemoteId = $"{os.Platform}:{os.Id}",
        Source = SongSource.Local,
        AllArtists = os.Artist,
        CoverArtPath = os.CoverUrl,
    };

    /// <summary>取每首的播放直链并加入播放队列（RemoteId 路由由宿主歌词兜底链识别 lx: 源）。返回成功入队的数量。</summary>
    public static async Task<int> PlayListAsync(IServiceProvider services, LxMusicPlugin plugin,
        IReadOnlyList<OnlineSong> songs, OnlineSong start)
    {
        var queue = services.GetRequiredService<PlayQueue>();
        var player = services.GetRequiredService<IAudioPlayerService>();
        var temp = new List<Song>();
        foreach (var s in songs)
        {
            string? url = null;
            try { url = await plugin.GetPlayUrlAsync(s, plugin.Config.QualityLevel); } catch { }
            if (string.IsNullOrWhiteSpace(url)) continue;
            temp.Add(ToQueueSong(s, url));
        }
        if (temp.Count == 0) return 0;
        queue.SetSongs(temp);
        var target = temp.FirstOrDefault(s => s.RemoteId == $"{start.Platform}:{start.Id}") ?? temp[0];
        queue.SelectSong(target.Id);
        try { await player.PlayAsync(target.FilePath); } catch { }
        return temp.Count;
    }
}

/// <summary>音源能力项：name=源全名（网易云/QQ/...），actions=该源声明的 actions 列表。</summary>
public class LxCapabilityItem
{
    public string Name { get; }
    public IReadOnlyList<string> Actions { get; }

    public LxCapabilityItem(string name, IReadOnlyList<string> actions)
    {
        Name = name;
        Actions = actions;
    }
}

/// <summary>音源 chip 项</summary>
public partial class LxSourceChipItem : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LxSourceChipItem(string name, bool isSelected)
    {
        Name = name;
        IsSelected = isSelected;
    }
}