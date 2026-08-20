using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面 ViewModel：服务器配置（地址/测试/保存）、音源选择、搜索、播放。
/// 播放链路复用宿主 PlayQueue + IAudioPlayerService，直链解析整队入队后从点击曲目开播。
/// </summary>
public partial class LxOnlineMusicViewModel : ObservableObject
{
    /// <summary>可选音源（"自动" = 不指定，用服务器默认；其余为 lx-music-api-server 支持的源）</summary>
    public static readonly string[] SourceOptions =
        { "自动", "netease", "qq", "kuwo", "kugou", "migu", "bilibili", "joox", "youtube", "yandex" };

    private readonly LxMusicPlugin _plugin;
    private readonly IServiceProvider _services;
    private CancellationTokenSource? _tipCts;
    private bool _pinging;

    [ObservableProperty]
    private string _serverUrl = "";

    [ObservableProperty]
    private string _serverStatus = "未配置服务器";

    [ObservableProperty]
    private string _scriptUrl = "";

    [ObservableProperty]
    private string _scriptStatus = "未配置脚本源";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _qualityText = "320k";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _tipMessage = "";

    [ObservableProperty]
    private bool _hasTip;

    [ObservableProperty]
    private ObservableCollection<OnlineSong> _songs = new();

    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _sourceChips = new();

    public LxOnlineMusicViewModel(LxMusicPlugin plugin, IServiceProvider services)
    {
        _plugin = plugin;
        _services = services;

        ServerUrl = plugin.Config.ServerUrl;
        ScriptUrl = plugin.Config.ScriptUrl;
        QualityText = QualityLabel(plugin.Config.QualityLevel);
        foreach (var name in SourceOptions)
        {
            var selected = string.Equals(name, plugin.Config.DefaultSource, StringComparison.OrdinalIgnoreCase)
                || (name == "自动" && string.IsNullOrEmpty(plugin.Config.DefaultSource));
            SourceChips.Add(new LxSourceChipItem(name, selected));
        }
        ServerStatus = string.IsNullOrWhiteSpace(ServerUrl) ? "未配置服务器" : "已配置 · 待验证";
        ScriptStatus = string.IsNullOrWhiteSpace(ScriptUrl) ? "未配置脚本源" : "待加载";
    }

    private static string QualityLabel(int q) => q switch
    {
        0 => "128k",
        1 => "320k",
        _ => "FLAC",
    };

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

    /// <summary>页面出现时自动 ping（已配置服务器才执行）+ 同步脚本状态</summary>
    public async Task AutoPingAsync()
    {
        // 脚本状态同步（InitializeAsync 可能已后台加载完成）
        if (_plugin.ScriptReady)
        {
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源";
        }
        else if (!string.IsNullOrWhiteSpace(_plugin.Config.ScriptUrl))
        {
            ScriptStatus = "加载中…";
        }

        if (!_plugin.Client.HasServer || _pinging) return;
        _pinging = true;
        ServerStatus = "连接中…";
        var ok = await _plugin.Client.PingAsync();
        ServerStatus = ok ? "已连接 ✓" : "连接失败 ✗";
        _pinging = false;
    }

    // ── 配置 ──

    /// <summary>保存服务器地址与音源选择（立即生效并持久化；同时保存脚本源地址）</summary>
    [RelayCommand]
    private void SaveConfig()
    {
        var url = (ServerUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) { ShowTip("请输入服务器地址"); return; }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowTip("地址需以 http:// 或 https:// 开头");
            return;
        }
        var source = SourceChips.FirstOrDefault(c => c.IsSelected)?.Name ?? "";
        if (source == "自动") source = "";
        var script = (ScriptUrl ?? "").Trim();
        _plugin.SaveConfig(url, _plugin.Config.QualityLevel, source, script);
        ServerStatus = "已配置 · 待验证";
        ScriptStatus = string.IsNullOrWhiteSpace(script) ? "未配置脚本源" : "待加载";
        ShowTip("已保存 ✓");
    }

    /// <summary>加载/重载 .js 脚本源（拉取+执行；成功后播放直链优先走脚本）</summary>
    [RelayCommand]
    private async Task LoadScriptAsync()
    {
        var script = (ScriptUrl ?? "").Trim();
        if (script.Length == 0)
        {
            await ClearScriptAsync();
            return;
        }
        if (!script.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !script.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowTip("脚本地址需以 http:// 或 https:// 开头");
            return;
        }
        ScriptStatus = "加载中…";
        var ok = await _plugin.LoadScriptAsync(script);
        if (ok)
        {
            _plugin.Config.ScriptUrl = script;
            LxConfigStore.Save(_plugin.Config);
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源";
            ShowTip($"脚本加载成功，声明 {n} 个源");
        }
        else
        {
            ScriptStatus = "加载失败 ✗";
            ShowTip("脚本加载失败：" + (_plugin.Script?.LastError ?? "未知错误"));
        }
    }

    /// <summary>清除脚本源（清空字段 + 释放脚本宿主 + 持久化）</summary>
    [RelayCommand]
    private async Task ClearScriptAsync()
    {
        ScriptUrl = "";
        _plugin.Config.ScriptUrl = "";
        LxConfigStore.Save(_plugin.Config);
        await _plugin.LoadScriptAsync("");
        ScriptStatus = "未配置脚本源";
        ShowTip("已清除脚本源");
    }

    /// <summary>测试连接（未保存也能测，仅改客户端地址；失败时恢复已保存地址避免污染搜索）</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var url = (ServerUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0
            || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            ShowTip("请先填写有效的服务器地址");
            return;
        }
        var savedUrl = _plugin.Config.ServerUrl;
        var changed = !string.Equals(url, savedUrl, StringComparison.OrdinalIgnoreCase);
        _plugin.Client.SetServerUrl(url);
        ServerStatus = "连接中…";
        var ok = await _plugin.Client.PingAsync();
        if (!ok && changed) _plugin.Client.SetServerUrl(savedUrl);
        ServerStatus = ok ? "已连接 ✓" : "连接失败 ✗";
        ShowTip(ok ? "服务器连接正常" : "连接失败，请检查地址与服务器状态");
    }

    /// <summary>循环切换音质（128k → 320k → FLAC），立即持久化</summary>
    [RelayCommand]
    private void CycleQuality()
    {
        var q = (_plugin.Config.QualityLevel + 1) % 3;
        _plugin.SaveConfig(_plugin.Config.ServerUrl, q, _plugin.Config.DefaultSource, _plugin.Config.ScriptUrl);
        QualityText = QualityLabel(q);
        ShowTip($"音质已切换：{QualityText}");
    }

    /// <summary>选择音源（立即持久化）</summary>
    [RelayCommand]
    private void SelectSource(LxSourceChipItem chip)
    {
        foreach (var c in SourceChips) c.IsSelected = ReferenceEquals(c, chip);
        var source = chip.Name == "自动" ? "" : chip.Name;
        _plugin.SaveConfig(_plugin.Config.ServerUrl, _plugin.Config.QualityLevel, source, _plugin.Config.ScriptUrl);
        ShowTip($"音源已切换：{chip.Name}");
    }

    // ── 搜索与播放 ──

    /// <summary>搜索歌曲（结果含封面，列表即点即播）</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = (SearchQuery ?? "").Trim();
        if (keyword.Length == 0) { ShowTip("输入要搜索的歌曲"); return; }
        if (!_plugin.Client.HasServer) { ShowTip("请先配置并测试服务器地址"); return; }
        IsBusy = true;
        try
        {
            var result = await _plugin.SearchAsync(keyword, 1, 30);
            Songs.Clear();
            if (result == null) { ShowTip("搜索失败（服务器无响应）"); return; }
            foreach (var s in result) Songs.Add(s);
            if (result.Count == 0) ShowTip("没有找到相关歌曲");
        }
        catch (Exception ex)
        {
            ShowTip($"搜索失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>点击歌曲：整队解析直链入队，从该曲开始播放</summary>
    [RelayCommand]
    private async Task PlaySongAsync(OnlineSong? song)
    {
        if (song == null || Songs.Count == 0) return;
        if (!_plugin.Client.HasServer) { ShowTip("请先配置服务器"); return; }
        try
        {
            var played = await LxPlaybackHelper.PlayListAsync(_services, _plugin, Songs, song);
            if (played == 0) ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）");
        }
        catch (Exception ex)
        {
            ShowTip($"播放失败：{ex.Message}");
        }
    }
}

/// <summary>播放队列构造辅助（与网易云插件同模式：临时负 Id + RemoteId 路由）</summary>
public static class LxPlaybackHelper
{
    private static int _idSeq;

    /// <summary>在线歌曲 → 带唯一负 Id 的临时 Song（队列索引/去重需要唯一 Id）</summary>
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
        // 封面：宿主 LoadCoverAsync 检测 http/https 会自动下载并缓存，播放页即可显示封面
        CoverArtPath = os.CoverUrl,
    };

    /// <summary>
    /// 通用"列表播放"：整队解析直链入队，从 <paramref name="start"/> 开始播放。
    /// </summary>
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

/// <summary>音源 chip 项（选中态驱动 chip 高亮）</summary>
public partial class LxSourceChipItem : ObservableObject
{
    /// <summary>显示名称（"自动" 或源标识）</summary>
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LxSourceChipItem(string name, bool isSelected)
    {
        Name = name;
        IsSelected = isSelected;
    }
}
