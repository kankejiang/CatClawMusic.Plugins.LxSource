using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面 ViewModel：脚本导入（在线 URL / 本地文件）、音源选择、搜索、播放。
/// 脚本声明源后，源 chips 动态重建（自动 + 脚本声明的各源）。
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

        ScriptUrl = plugin.Config.ScriptUrl;
        ScriptFilePath = plugin.Config.ScriptFilePath;
        QualityText = QualityLabel(plugin.Config.QualityLevel);
        RebuildSourceChips();
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

    /// <summary>页面出现时同步脚本状态 + 若已加载重建源 chips</summary>
    public void OnAppearing()
    {
        if (_plugin.ScriptReady)
        {
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源";
            RebuildSourceChips();
        }
        else if (!string.IsNullOrWhiteSpace(_plugin.Config.ScriptFilePath) || !string.IsNullOrWhiteSpace(_plugin.Config.ScriptUrl))
        {
            ScriptStatus = "加载中…";
        }
    }

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
        Songs.Clear();
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

    /// <summary>选择音源（立即持久化）</summary>
    [RelayCommand]
    private void SelectSource(LxSourceChipItem chip)
    {
        foreach (var c in SourceChips) c.IsSelected = ReferenceEquals(c, chip);
        var source = chip.Name == "自动" ? "" : chip.Name;
        _plugin.SaveConfig(_plugin.Config.QualityLevel, source, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        ShowTip($"音源已切换：{chip.Name}");
    }

    // ── 搜索与播放 ──

    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = (SearchQuery ?? "").Trim();
        if (keyword.Length == 0) { ShowTip("输入要搜索的歌曲"); return; }
        if (!_plugin.ScriptReady) { ShowTip("请先导入脚本"); return; }
        IsBusy = true;
        try
        {
            var result = await _plugin.SearchAsync(keyword, 1, 30);
            Songs.Clear();
            if (result == null) { ShowTip("当前脚本不支持搜索（仅解析播放直链）"); return; }
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

    [RelayCommand]
    private async Task PlaySongAsync(OnlineSong? song)
    {
        if (song == null || Songs.Count == 0) return;
        if (!_plugin.ScriptReady) { ShowTip("请先导入脚本"); return; }
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

/// <summary>播放队列构造辅助（临时负 Id + RemoteId 路由）</summary>
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
