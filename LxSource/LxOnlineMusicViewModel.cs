using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

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

    // ── UI Tab 状态（搜索 / 歌单 / 排行榜）──

    /// <summary>当前主 Tab（搜索 / 歌单 / 排行榜）</summary>
    [ObservableProperty]
    private LxUiTab _currentTab = LxUiTab.Search;

    /// <summary>搜索页子 Tab（歌曲 / 歌单）</summary>
    [ObservableProperty]
    private LxSearchTab _currentSearchTab = LxSearchTab.Song;

    /// <summary>歌单页排序（最热 / 最新）</summary>
    [ObservableProperty]
    private LxPlaylistSort _playlistSort = LxPlaylistSort.Hot;

    /// <summary>主 Tab chips（搜索 / 歌单 / 排行榜）</summary>
    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _mainTabs = new();

    /// <summary>搜索子 Tab chips（歌曲 / 歌单）</summary>
    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _searchTabs = new();

    /// <summary>歌单排序 chips（最热 / 最新）</summary>
    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _playlistSortChips = new();

    /// <summary>歌单分类 chips（全部 + 各标签）</summary>
    [ObservableProperty]
    private ObservableCollection<LxCategoryChip> _playlistCategories = new();

    /// <summary>当前选中歌单分类（null=全部）</summary>
    [ObservableProperty]
    private LxCategoryChip? _selectedCategory;

    /// <summary>歌单列表（歌单 Tab：最热/最新 + 分类）</summary>
    [ObservableProperty]
    private ObservableCollection<OnlinePlaylist> _playlists = new();

    /// <summary>歌单搜索结果（搜索页「歌单」子 Tab）</summary>
    [ObservableProperty]
    private ObservableCollection<OnlinePlaylist> _searchPlaylistResults = new();

    /// <summary>排行榜列表（排行榜 Tab：酷我 25 榜单）</summary>
    [ObservableProperty]
    private ObservableCollection<OnlinePlaylist> _toplists = new();

    private bool _tabDataLoaded;

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
        RebuildSourceToggles();
        RebuildBoardChips();
        ScriptStatus = string.IsNullOrEmpty(ScriptUrl) && string.IsNullOrEmpty(ScriptFilePath)
            ? "未导入脚本" : "待加载";

        // ── UI Tab chips ──
        MainTabs.Add(new LxSourceChipItem("搜索", true));
        MainTabs.Add(new LxSourceChipItem("歌单", false));
        MainTabs.Add(new LxSourceChipItem("排行榜", false));
        SearchTabs.Add(new LxSourceChipItem("歌曲", true));
        SearchTabs.Add(new LxSourceChipItem("歌单", false));
        PlaylistSortChips.Add(new LxSourceChipItem("最热", true));
        PlaylistSortChips.Add(new LxSourceChipItem("最新", false));
    }

    private static string QualityLabel(int q) => q switch
    {
        0 => "128k",
        1 => "320k",
        _ => "FLAC",
    };

    // ── 榜单 ──

    /// <summary>当前内置源是否为咪咕（false=酷我）。搜索/榜单/封面据此分流。</summary>
    private bool IsMg => _plugin.BuiltinSource == "mg";

    /// <summary>榜单 chips：默认选中"热歌榜"并加载</summary>
    private void RebuildBoardChips()
    {
        var boards = IsMg ? LxMiguApi.GetBoards() : LxKuwoApi.GetBoards();
        BoardChips.Clear();
        foreach (var b in boards)
            BoardChips.Add(new LxSourceChipItem(b.Name, b.Id == (IsMg ? "mg__27186466" : "kw__16")));
        SelectedBoard = BoardChips.FirstOrDefault(c => c.IsSelected);
    }

    /// <summary>选择榜单 → 加载该榜单歌曲（按内置源分流：酷我旧接口每页 100 首 / 咪咕老列结构）</summary>
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
            var board = (IsMg ? LxMiguApi.GetBoards() : LxKuwoApi.GetBoards())
                .FirstOrDefault(b => b.Name == chip.Name);
            if (board == null) { Songs.Clear(); return; }
            var songs = IsMg
                ? await LxMiguApi.GetBoardSongsAsync(board.BangId)
                : await LxKuwoApi.GetBoardSongsAsync(board.BangId);
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

    /// <summary>后台并发（限 4）预取歌曲封面（酷我 rid_pic API）。后台线程只拉 URL，
    /// 不触碰 UI 属性；完成后统一回到 UI 线程赋值 CoverUrl 并重建 Songs 触发列表刷新。</summary>
    private async Task EnrichCoversAsync(IReadOnlyList<OnlineSong> songs)
    {
        try
        {
            var updates = new List<KeyValuePair<OnlineSong, string>>(songs.Count);
            await Task.WhenAll(songs.Select(async s =>
            {
                var url = await FetchCoverUrlAsync(s).ConfigureAwait(false);
                if (url != null)
                    lock (updates) updates.Add(new(s, url));
            })).ConfigureAwait(false);
            if (updates.Count == 0) return;
            await MainThread.InvokeOnMainThreadAsync(() => UpdateSongsWithCovers(songs, updates));
        }
        catch { /* 封面预取失败不影响列表 */ }
    }

    /// <summary>UI 线程重刷列表：先写回封面，再以原始列表重建 Songs（对象与 Songs 内为同一批实例）。</summary>
    private void UpdateSongsWithCovers(IReadOnlyList<OnlineSong> source, List<KeyValuePair<OnlineSong, string>> updates)
    {
        foreach (var (s, url) in updates) s.CoverUrl = url;
        Songs.Clear();
        foreach (var s in source) Songs.Add(s);
    }

    private static readonly SemaphoreSlim CoverGate = new(4, 4);

    /// <summary>后台拉取单首封面 URL（不修改任何可绑定属性，避免跨线程 PropertyChanged）。</summary>
    private static async Task<string?> FetchCoverUrlAsync(OnlineSong s)
    {
        if (s == null || !string.IsNullOrWhiteSpace(s.CoverUrl)) return null;
        await CoverGate.WaitAsync();
        try
        {
            if (s.Id != null && s.Id.StartsWith("kw:", StringComparison.OrdinalIgnoreCase))
                return await LxKuwoApi.GetPicAsync(s.Id[3..]).ConfigureAwait(false);
            if (s.Id != null && s.Id.StartsWith("mg:", StringComparison.OrdinalIgnoreCase)
                && s.Internal.TryGetValue("ResId", out var rid) && rid is string r && !string.IsNullOrEmpty(r))
                return await LxMiguApi.GetPicAsync(r).ConfigureAwait(false);
        }
        catch { }
        finally { CoverGate.Release(); }
        return null;
    }


    // ── 搜索 ──

    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = (SearchQuery ?? "").Trim();
        if (keyword.Length == 0) { ShowTip("输入要搜索的歌曲或歌单"); return; }
        // 搜索页「歌单」子 Tab → 走歌单搜索；「歌曲」子 Tab → 走歌曲搜索
        if (CurrentSearchTab == LxSearchTab.Playlist)
        {
            await SearchPlaylistAsync(keyword);
            return;
        }
        await SearchSongsAsync(keyword);
    }

    private async Task SearchSongsAsync(string keyword)
    {
        ListTitle = $"搜索「{keyword}」";
        IsBusy = true;
        try
        {
            var songs = IsMg
                ? await LxMiguApi.SearchAsync(keyword, 1, 30)
                : await LxKuwoApi.SearchAsync(keyword, 1, 30);
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

    private async Task SearchPlaylistAsync(string keyword)
    {
        ListTitle = $"搜索「{keyword}」· 歌单";
        IsBusy = true;
        try
        {
            var pls = await _plugin.SearchPlaylistsAsync(keyword, 1, 30);
            SearchPlaylistResults.Clear();
            if (pls == null)
            {
                ShowTip("歌单搜索失败，请检查网络");
                return;
            }
            foreach (var p in pls) SearchPlaylistResults.Add(p);
            if (pls.Count == 0) ShowTip("没有找到相关歌单");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── UI Tab 切换 ──

    /// <summary>切换主 Tab（搜索 / 歌单 / 排行榜）。参数 = tab chip。</summary>
    [RelayCommand]
    private void SwitchMainTab(LxSourceChipItem? chip)
    {
        if (chip == null) return;
        foreach (var c in MainTabs) c.IsSelected = ReferenceEquals(c, chip);
        var tab = chip.Name switch
        {
            "歌单" => LxUiTab.Playlist,
            "排行榜" => LxUiTab.Ranking,
            _ => LxUiTab.Search,
        };
        CurrentTab = tab;
        if (tab == LxUiTab.Playlist || tab == LxUiTab.Ranking)
            _ = EnsureTabDataAsync();
    }

    /// <summary>切换搜索页子 Tab（歌曲 / 歌单）。</summary>
    [RelayCommand]
    private void SwitchSearchTab(LxSourceChipItem? chip)
    {
        if (chip == null) return;
        foreach (var c in SearchTabs) c.IsSelected = ReferenceEquals(c, chip);
        CurrentSearchTab = chip.Name switch { "歌单" => LxSearchTab.Playlist, _ => LxSearchTab.Song };
        // 已有非空关键词 → 按当前子 Tab 重新搜索
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            _ = SearchAsync();
    }

    /// <summary>切换歌单页排序（最热 / 最新）。</summary>
    [RelayCommand]
    private void SwitchPlaylistSort(LxSourceChipItem? chip)
    {
        if (chip == null) return;
        foreach (var c in PlaylistSortChips) c.IsSelected = ReferenceEquals(c, chip);
        PlaylistSort = chip.Name switch { "最新" => LxPlaylistSort.New, _ => LxPlaylistSort.Hot };
        _ = LoadPlaylistsAsync();
    }

    /// <summary>选择歌单分类（全部 / 某标签）。</summary>
    [RelayCommand]
    private void SelectPlaylistCategory(LxCategoryChip? chip)
    {
        if (chip == null) return;
        foreach (var c in PlaylistCategories) c.IsSelected = ReferenceEquals(c, chip);
        SelectedCategory = chip;
        _ = LoadPlaylistsAsync();
    }

    /// <summary>歌单/排行榜 Tab 首次进入时懒加载分类 + 歌单列表 + 排行榜（只做一次）。</summary>
    private async Task EnsureTabDataAsync()
    {
        if (_tabDataLoaded) return;
        _tabDataLoaded = true;
        try
        {
            var catsTask = LoadPlaylistCategoriesAsync();
            var plTask = LoadPlaylistsAsync();
            var topTask = LoadToplistsAsync();
            await Task.WhenAll(catsTask, plTask, topTask);
        }
        catch { }
    }

    /// <summary>加载歌单分类（全部 + 各标签），失败保留「全部」。</summary>
    private async Task LoadPlaylistCategoriesAsync()
    {
        try
        {
            var groups = await LxKuwoApi.GetPlaylistCategoriesAsync();
            if (groups == null || groups.Count == 0) return;
            var keepSelected = SelectedCategory;
            PlaylistCategories.Clear();
            PlaylistCategories.Add(new LxCategoryChip("全部", null, keepSelected?.TagId == null));
            foreach (var g in groups)
                foreach (var t in g.Tags)
                    PlaylistCategories.Add(new LxCategoryChip(t.Name, t.Id, t.Id == keepSelected?.TagId));
            // 选中态：默认「全部」
            SelectedCategory = PlaylistCategories.FirstOrDefault(c => c.IsSelected) ?? PlaylistCategories.FirstOrDefault();
        }
        catch { }
    }

    /// <summary>加载歌单列表（按排序 + 分类）。</summary>
    private async Task LoadPlaylistsAsync()
    {
        IsBusy = true;
        try
        {
            var tagId = SelectedCategory?.TagId;
            List<OnlinePlaylist> list = PlaylistSort == LxPlaylistSort.New
                ? await _plugin.GetPlaylistsNewAsync(tagId, 1, 20)
                : await _plugin.GetPlaylistsAsync(tagId);
            Playlists.Clear();
            if (list != null)
            {
                foreach (var p in list) Playlists.Add(p);
                if (list.Count == 0) ShowTip(PlaylistSort == LxPlaylistSort.New ? "没有最新歌单" : "没有找到该分类歌单");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加载排行榜列表（酷我 25 榜单）。</summary>
    private async Task LoadToplistsAsync()
    {
        IsBusy = true;
        try
        {
            var tops = await _plugin.GetToplistsAsync();
            Toplists.Clear();
            foreach (var p in tops) Toplists.Add(p);
            if (tops.Count == 0) ShowTip("排行榜加载失败，请检查网络");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>打开歌单/榜单详情页（推入 Shell）。先取歌曲，再 push 详情页。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(OnlinePlaylist? playlist)
    {
        if (playlist == null) return;
        IsBusy = true;
        List<OnlineSong>? songs = null;
        try
        {
            songs = await _plugin.GetPlaylistSongsAsync(playlist, 1, 80);
        }
        finally
        {
            IsBusy = false;
        }
        if (songs == null) { ShowTip("歌单加载失败，请检查网络"); return; }
        // 页面构造 + 导航必须占用 UI 线程（本方法 await 续体现已回到 UI 线程）
        try
        {
            var page = await MainThread.InvokeOnMainThreadAsync(() =>
                new LxPlaylistDetailPage(playlist, songs!, _services, this));
            await LxNav.PushAsync(page);
        }
        catch { }
    }

    // ── 播放（点击/菜单播放：只取被点那一首的直链，单首入队播放）──

    /// <summary>公开：脚本是否就绪（详情页判断，避免暴露内部插件引用）。</summary>
    public bool ScriptReady => _plugin.ScriptReady;

    /// <summary>播放整列表（歌单详情「播放全部」）：逐首取直链入队，任一失败跳过不阻断。</summary>
    public async Task PlayAllSongsAsync(IReadOnlyList<OnlineSong> songs, string playName)
    {
        if (songs.Count == 0) return;
        if (!_plugin.ScriptReady) { ShowTip("请先在 ⚙ 设置导入音源脚本（用于解析播放直链）"); return; }
        // 逐首取直链较慢，点击「播放全部」立即反馈加载中
        ShowTip($"正在播放：{playName} · 加载中…");
        try
        {
            var queueSongs = new List<Song>(songs.Count);
            foreach (var os in songs)
            {
                var url = await _plugin.GetPlayUrlAsync(os, _plugin.Config.QualityLevel).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(url)) continue;
                queueSongs.Add(LxPlaybackHelper.ToQueueSong(os, url));
            }
            if (queueSongs.Count == 0) { ShowTip("暂时取不到播放链接（可能为 VIP 或源失效）"); return; }
            var queue = _services.GetRequiredService<PlayQueue>();
            var player = _services.GetRequiredService<IAudioPlayerService>();
            queue.SetSongs(queueSongs);
            queue.SelectSong(queueSongs[0].Id);
            await player.PlayAsync(queueSongs[0].FilePath);
            ShowTip($"正在播放：{playName}");
        }
        catch (Exception ex)
        {
            ShowTip($"播放失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PlaySongAsync(OnlineSong? song)
    {
        if (song == null) return;
        if (!_plugin.ScriptReady) { ShowTip("请先在 ⚙ 设置导入音源脚本（用于解析播放直链）"); return; }
        // 点击立即反馈：直链解析+缓冲期间先亮「正在播放·加载中」，用户能确认点击已生效
        ShowTip($"正在播放：{song.Title} · 加载中…");
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
        // 取直链期间先亮解析提示，避免菜单关闭后数秒无反馈
        ShowTip($"「{song.Title}」· 正在解析播放链接…");
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
            var dm = _services.GetService<IDownloadManager>();
            if (dm == null) { ShowTip("宿主下载管理器不可用"); return; }
            dm.EnqueueUrl(url, fileName);
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
            RebuildSourceToggles();
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
        _plugin.ClearScript(); // 删除持久化文件并清空配置
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

    /// <summary>根据脚本声明的源重建 chips（自动 + 各声明的源短码映射全名；禁用源过滤）</summary>
    public void RebuildSourceChips()
    {
        var selected = _plugin.Config.DefaultSource;
        SourceChips.Clear();
        SourceChips.Add(new LxSourceChipItem("自动", string.IsNullOrEmpty(selected)));
        if (_plugin.Script?.Sources != null)
        {
            foreach (var code in _plugin.Script.Sources.SourceCodes)
            {
                if (!_plugin.IsSourceEnabled(code)) continue;  // 已禁用的源不进入默认源选择
                var full = LxPlatformCodes.ToFull(code);
                if (string.IsNullOrEmpty(full)) full = code;
                SourceChips.Add(new LxSourceChipItem(full, string.Equals(full, selected, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    // ── 源启停（设置 sheet 内：每源一个开关 chip，IsSelected=启用）──

    /// <summary>源开关 chips（Name=源全名，IsSelected=启用）</summary>
    [ObservableProperty]
    private ObservableCollection<LxSourceChipItem> _sourceToggleChips = new();

    /// <summary>重建源开关 chips（脚本声明的全部源，禁用源显示为未选中）</summary>
    public void RebuildSourceToggles()
    {
        SourceToggleChips.Clear();
        if (_plugin.Script?.Sources == null) return;
        foreach (var code in _plugin.Script.Sources.SourceCodes)
        {
            var full = LxPlatformCodes.ToFull(code);
            if (string.IsNullOrEmpty(full)) full = code;
            SourceToggleChips.Add(new LxSourceChipItem(full, _plugin.IsSourceEnabled(code)));
        }
    }

    /// <summary>切换源启用/禁用（持久化）</summary>
    [RelayCommand]
    private void ToggleSource(LxSourceChipItem chip)
    {
        var full = chip.Name;
        var code = LxPlatformCodes.ToShort(full);
        if (string.IsNullOrEmpty(code)) return;
        var enable = !chip.IsSelected;
        chip.IsSelected = enable;
        _plugin.SetSourceEnabled(code, enable);
        RebuildSourceChips();
        ShowTip(enable ? $"已启用音源：{chip.Name}" : $"已禁用音源：{chip.Name}");
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

/// <summary>主 Tab（顶部主导航：搜索 / 歌单 / 排行榜）。</summary>
public enum LxUiTab { Search, Playlist, Ranking }

/// <summary>搜索页子 Tab（歌曲 / 歌单）。</summary>
public enum LxSearchTab { Song, Playlist }

/// <summary>歌单页排序（最热 / 最新）。</summary>
public enum LxPlaylistSort { Hot, New }

/// <summary>歌单分类 chip 项（Name=标签名，TagId=酷我标签id，null=「全部」）。</summary>
public partial class LxCategoryChip : ObservableObject
{
    public string Name { get; }
    public string? TagId { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LxCategoryChip(string name, string? tagId, bool isSelected)
    {
        Name = name;
        TagId = tagId;
        IsSelected = isSelected;
    }
}

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