using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面 ViewModel —— 架构照搬 lx-music-mobile（lyswhut/lx-music-mobile）：
/// 内容数据（搜索/排行榜）直连酷我公开 API（LxKuwoApi），播放直链由用户自定义源
/// 脚本（musicUrl action）解析。设置入口为右上齿轮 → 底部 sheet。
/// <para>页面结构（对齐 lx 主页）：</para>
/// <para>· 搜索框（酷我搜索）</para>
/// <para>· 榜单横向 chips（热歌榜/飙升榜/新歌榜…，lx kw leaderboard 硬编码榜单）</para>
/// <para>· 歌曲列表（榜单歌曲或搜索结果；点歌整列表入队播放）</para>
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

    [ObservableProperty]
    private string _searchQuery = "";

    /// <summary>歌曲列表（榜单歌曲或搜索结果）</summary>
    [ObservableProperty]
    private ObservableCollection<OnlineSong> _songs = new();

    /// <summary>榜单 chips（酷我榜单，照搬 lx kw boardList）</summary>
    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _boardChips = new();

    /// <summary>当前选中榜单（null=未选，显示空状态）</summary>
    [ObservableProperty]
    private LxSourceChipItem? _selectedBoard;

    /// <summary>当前列表标题（榜单名 / "搜索结果" / 空提示）</summary>
    [ObservableProperty]
    private string _listTitle = "";

    /// <summary>音源 chips（自动 + 脚本声明的源；sheet 内使用）</summary>
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
        RebuildBoardChips();
        ScriptStatus = string.IsNullOrEmpty(ScriptUrl) && string.IsNullOrEmpty(ScriptFilePath)
            ? "未导入脚本" : "待加载";
    }

    private static string QualityLabel(int q) => q switch
    {
        0 => "128k",
        1 => "320k",
        _ => "FLAC",
    };

    // ── 榜单 ──

    /// <summary>榜单 chips：默认选中"热歌榜"并加载</summary>
    private void RebuildBoardChips()
    {
        var boards = LxKuwoApi.GetBoards();
        BoardChips.Clear();
        foreach (var b in boards)
            BoardChips.Add(new LxSourceChipItem(b.Name, b.Id == "kw__16"));
        SelectedBoard = BoardChips.FirstOrDefault(c => c.IsSelected);
    }

    /// <summary>选择榜单 → 加载该榜单歌曲（酷我旧接口，每页 100 首）</summary>
    [RelayCommand]
    private async Task SelectBoardAsync(LxSourceChipItem? chip)
    {
        if (chip == null) return;
        foreach (var c in BoardChips) c.IsSelected = ReferenceEquals(c, chip);
        SelectedBoard = chip;
        ListTitle = chip.Name;
        IsBusy = true;
        try
        {
            var board = LxKuwoApi.GetBoards().FirstOrDefault(b => b.Name == chip.Name);
            if (board == null) { Songs.Clear(); return; }
            var songs = await LxKuwoApi.GetBoardSongsAsync(board.BangId);
            Songs.Clear();
            if (songs == null)
            {
                ShowTip("榜单加载失败，请检查网络");
                return;
            }
            foreach (var s in songs) Songs.Add(s);
            EnrichCoversAsync(songs).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>后台并发（限 4）预取歌曲封面（酷我 rid_pic API），完成后重建 Songs 触发列表刷新。</summary>
    private async Task EnrichCoversAsync(IReadOnlyList<OnlineSong> songs)
    {
        try
        {
            var tasks = songs.Select(LoadCoverAsync).ToArray();
            await Task.WhenAll(tasks);
            // 封面就绪 → 重建集合触发 CollectionView 刷新
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Songs.Clear();
                foreach (var s in songs) Songs.Add(s);
            });
        }
        catch { /* 封面预取失败不影响列表 */ }
    }

    private static readonly SemaphoreSlim CoverGate = new(4, 4);

    private static async Task LoadCoverAsync(OnlineSong s)
    {
        if (s == null || !string.IsNullOrWhiteSpace(s.CoverUrl)) return;
        if (s.Id == null || !s.Id.StartsWith("kw:", StringComparison.OrdinalIgnoreCase)) return;
        var songmid = s.Id[3..];
        await CoverGate.WaitAsync();
        try
        {
            var url = await LxKuwoApi.GetPicAsync(songmid);
            if (!string.IsNullOrWhiteSpace(url)) s.CoverUrl = url;
        }
        catch { }
        finally { CoverGate.Release(); }
    }


    // ── 搜索 ──

    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = (SearchQuery ?? "").Trim();
        if (keyword.Length == 0) { ShowTip("输入要搜索的歌曲"); return; }
        ListTitle = $"搜索「{keyword}」";
        IsBusy = true;
        try
        {
            var songs = await LxKuwoApi.SearchAsync(keyword, 1, 30);
            Songs.Clear();
            if (songs == null)
            {
                ShowTip("搜索失败，请检查网络");
                return;
            }
            foreach (var s in songs) Songs.Add(s);
            EnrichCoversAsync(songs).ContinueWith(_ => { }, TaskScheduler.Default);
            if (songs.Count == 0) ShowTip("没有找到相关歌曲");
            // 搜索后清掉榜单选中态（列表语义已变）
            foreach (var c in BoardChips) c.IsSelected = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 播放（点击/菜单播放：只取被点那一首的直链，单首入队播放）──

    [RelayCommand]
    private async Task PlaySongAsync(OnlineSong? song)
    {
        if (song == null) return;
        if (!_plugin.ScriptReady) { ShowTip("请先在 ⚙ 设置导入音源脚本（用于解析播放直链）"); return; }
        try
        {
            var url = await _plugin.GetPlayUrlAsync(song, _plugin.Config.QualityLevel);
            if (string.IsNullOrWhiteSpace(url)) { ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）"); return; }
            var queue = _services.GetRequiredService<PlayQueue>();
            var player = _services.GetRequiredService<IAudioPlayerService>();
            var qSong = LxPlaybackHelper.ToQueueSong(song, url);
            queue.SetSongs(new List<Song> { qSong });
            queue.SelectSong(qSong.Id);
            await player.PlayAsync(qSong.FilePath);
            ShowTip($"正在播放：{song.Title}");
        }
        catch (Exception ex)
        {
            ShowTip($"播放失败：{ex.Message}");
        }
    }

    // ── 歌曲操作菜单（长按弹出：播放 / 下一首播放 / 下载）──

    /// <summary>长按菜单是否打开</summary>
    [ObservableProperty]
    private bool _isSongMenuOpen;

    /// <summary>长按选中的歌曲</summary>
    [ObservableProperty]
    private OnlineSong? _menuSong;

    /// <summary>下载音质选择弹窗是否打开</summary>
    [ObservableProperty]
    private bool _isQualityPickerOpen;

    /// <summary>待下载歌曲（音质选择确定后执行）</summary>
    [ObservableProperty]
    private OnlineSong? _downloadSong;

    [RelayCommand]
    private void OpenSongMenu(OnlineSong? song)
    {
        if (song == null) return;
        MenuSong = song;
        IsSongMenuOpen = true;
    }

    [RelayCommand]
    private void CloseSongMenu() => IsSongMenuOpen = false;

    /// <summary>菜单：播放（当前歌 → 整列表入队播放）</summary>
    [RelayCommand]
    private async Task PlayNowAsync()
    {
        var song = MenuSong;
        CloseSongMenu();
        if (song != null) await PlaySongAsync(song);
    }

    /// <summary>菜单：下一首播放（取直链 → PlayQueue.AddNext 插入当前位置之后）</summary>
    [RelayCommand]
    private async Task PlayNextAsync()
    {
        var song = MenuSong;
        CloseSongMenu();
        if (song == null) return;
        if (!_plugin.ScriptReady) { ShowTip("请先在 ⚙ 设置导入音源脚本"); return; }
        try
        {
            var url = await _plugin.GetPlayUrlAsync(song, _plugin.Config.QualityLevel);
            if (string.IsNullOrWhiteSpace(url)) { ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）"); return; }
            var queue = _services.GetRequiredService<PlayQueue>();
            queue.AddNext(LxPlaybackHelper.ToQueueSong(song, url));
            ShowTip($"「{song.Title}」已插入下一首");
        }
        catch (Exception ex)
        {
            ShowTip($"下一首播放失败：{ex.Message}");
        }
    }

    /// <summary>菜单：下载（先选音质）</summary>
    [RelayCommand]
    private void OpenDownloadPicker()
    {
        DownloadSong = MenuSong;
        CloseSongMenu();
        if (DownloadSong != null) IsQualityPickerOpen = true;
    }

    [RelayCommand]
    private void CancelQualityPicker() => IsQualityPickerOpen = false;

    /// <summary>下载确认：按所选音质取直链 → 宿主下载管理器入队（下载中心可见）</summary>
    [RelayCommand]
    private async Task ConfirmDownloadAsync(string? qualityLabel)
    {
        var song = DownloadSong;
        IsQualityPickerOpen = false;
        DownloadSong = null;
        if (song == null) return;
        if (!_plugin.ScriptReady) { ShowTip("请先在 ⚙ 设置导入音源脚本"); return; }
        try
        {
            var q = qualityLabel switch { "128k" => 0, "320k" => 1, _ => 2 };
            var url = await _plugin.GetPlayUrlAsync(song, q);
            if (string.IsNullOrWhiteSpace(url)) { ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）"); return; }
            var ext = q == 2 ? "flac" : "mp3";
            var fileName = $"{SanitizeFileName(song.Title)} - {SanitizeFileName(song.Artist)}.{ext}";
            var dm = _services.GetService<CatClawMusic.Core.Interfaces.IDownloadManager>();
            if (dm == null) { ShowTip("宿主下载管理器不可用"); return; }
            var id = dm.EnqueueUrl(url, fileName);
            ShowTip($"已开始下载「{fileName}」，可在宿主下载中心查看");
        }
        catch (Exception ex)
        {
            ShowTip($"下载失败：{ex.Message}");
        }
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "未知";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }

    // ── 设置 sheet ──

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    // ── 脚本导入（sheet 内）──

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
        HandleScriptLoaded(ok, "在线");
    }

    /// <summary>本地导入（由页面 FilePicker 选文件后调用）</summary>
    public async Task ImportLocalFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { ShowTip("未选择文件"); return; }
        ScriptStatus = "加载中…";
        var ok = await _plugin.LoadScriptFromFileAsync(filePath);
        HandleScriptLoaded(ok, "本地");
    }

    private void HandleScriptLoaded(bool ok, string mode)
    {
        if (ok)
        {
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源（{mode}）";
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
        ShowTip("已清除脚本");
        return Task.CompletedTask;
    }

    // ── 音质与音源（sheet 内）──

    /// <summary>循环切换音质（128k → 320k → FLAC）</summary>
    [RelayCommand]
    private void CycleQuality()
    {
        var q = (_plugin.Config.QualityLevel + 1) % 3;
        _plugin.SaveConfig(q, _plugin.Config.DefaultSource, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        QualityText = QualityLabel(q);
        ShowTip($"音质已切换：{QualityText}");
    }

    /// <summary>设置指定音质档（0=128k 1=320k 2=FLAC）</summary>
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

    /// <summary>选择音源（脚本解析播放直链时优先用的源）</summary>
    [RelayCommand]
    private void SelectSource(LxSourceChipItem chip)
    {
        foreach (var c in SourceChips) c.IsSelected = ReferenceEquals(c, chip);
        var source = chip.Name == "自动" ? "" : chip.Name;
        _plugin.SaveConfig(_plugin.Config.QualityLevel, source, _plugin.Config.ScriptUrl, _plugin.Config.ScriptFilePath);
        ShowTip($"音源已切换：{chip.Name}");
    }

    // ── 通用 ──

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

    /// <summary>页面出现时：同步脚本状态 + 首次进入自动加载默认榜单</summary>
    public void OnAppearing()
    {
        if (_plugin.ScriptReady)
        {
            var n = _plugin.Script?.Sources?.SourceCodes.Count ?? 0;
            ScriptStatus = $"已加载 · {n} 个源";
            RebuildSourceChips();
        }
        // 首次进入且列表为空 → 加载默认榜单（热歌榜）
        if (Songs.Count == 0 && SelectedBoard != null && string.IsNullOrEmpty(ListTitle))
            _ = SelectBoardAsync(SelectedBoard);
    }
}

/// <summary>播放队列构造辅助（脚本取播放直链 → 临时负 Id + RemoteId 路由入队）。</summary>
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
}

/// <summary>音源/榜单 chip 项</summary>
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