using System.Collections.ObjectModel;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// 歌单 / 榜单详情页：顶部大封面 + 渐变遮罩 + 歌单名/描述 + 「播放全部」按钮 + 歌曲列表。
/// 样式对齐 lx-music-mobile 详情页；歌曲行复用 <see cref="LxUiKit.CreateSongItemTemplate"/>。
/// 点击歌曲 → 单首入队播放（复用主 VM 的 <see cref="LxOnlineMusicViewModel.PlaySongCommand"/>）。
/// </summary>
public sealed class LxPlaylistDetailPage : ContentPage
{
    private readonly OnlinePlaylist _playlist;
    private readonly ObservableCollection<OnlineSong> _songs;
    private readonly LxPlaylistDetailViewModel _dvm;

    public LxPlaylistDetailPage(OnlinePlaylist playlist, List<OnlineSong> songs,
        IServiceProvider services, LxOnlineMusicViewModel vm)
    {
        _playlist = playlist;
        _songs = new ObservableCollection<OnlineSong>(songs ?? new List<OnlineSong>());
        _dvm = new LxPlaylistDetailViewModel(vm, services);

        Title = playlist.Name;
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        Content = BuildContent();
    }

    private View BuildContent()
    {
        // ── 头部封面（大图 + 渐变 + 返回按钮）──
        var coverImage = new Image
        {
            Aspect = Aspect.AspectFill,
            HeightRequest = 220,
            VerticalOptions = LayoutOptions.Start,
            HorizontalOptions = LayoutOptions.Fill,
        };
        coverImage.Source = LxDetailCoverConverter.ToSource(_playlist.CoverUrl);
        var gradient = new BoxView { HeightRequest = 220, VerticalOptions = LayoutOptions.Start };
        gradient.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.2),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb("#00000000"), 0f),
                new GradientStop(Color.FromArgb("#E6000000"), 1f),
            },
        };

        // 返回按钮
        var backButton = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
        };
        backButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var backLabel = new Label { Text = "‹", FontSize = 18, VerticalOptions = LayoutOptions.Center };
        backLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        backButton.Content = backLabel;
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) => await PopAsyncSafe();
        backButton.GestureRecognizers.Add(backTap);

        var headFrame = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new() { Height = 220 } },
            Children = { coverImage, gradient, backButton },
        };

        // ── 标题 / 描述 ──
        var titleLabel = new Label
        {
            FontSize = 22,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            MaxLines = 2,
            Margin = new Thickness(20, 10, 20, 0),
        };
        titleLabel.Text = _playlist.Name;

        var metaLabel = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#CCFFFFFF"),
            MaxLines = 2,
            Margin = new Thickness(20, 6, 20, 12),
        };
        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_playlist.Description)) metaParts.Add(_playlist.Description);
        if (_playlist.SongCount > 0) metaParts.Add($"{_playlist.SongCount} 首");
        metaLabel.Text = string.Join(" · ", metaParts);

        // ── 播放全部 ──
        var playAllButton = new Border
        {
            Padding = new Thickness(0, 10),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(20, 0, 20, 10),
        };
        playAllButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        var playAllLabel = new Label
        {
            Text = "▶ 播放全部",
            TextColor = Colors.White,
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        playAllButton.Content = playAllLabel;
        var playTap = new TapGestureRecognizer();
        playTap.Tapped += async (_, _) => await PlayAllAsync();
        playAllButton.GestureRecognizers.Add(playTap);

        // ── 歌曲列表 ──
        var songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _songs,
            ItemTemplate = new DataTemplate(() => LxUiKit.CreateSongItemTemplate(null)),
            EmptyView = new Label
            {
                Text = "歌单暂无歌曲",
                FontSize = 12,
                TextColor = Color.FromArgb("#808080"),
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 40, 24, 0),
            },
        };
        songsView.Scrolled += (_, _) => LxUiKit.CancelAllLongPresses();
        songsView.SelectionChanged += async (s, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is OnlineSong song)
            {
                songsView.SelectedItem = null;
                await _dvm.PlaySongAsync(song);
            }
        };

        // ── 布局 ──
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },  // 0 头部
                new() { Height = GridLength.Auto },  // 1 标题/播放按钮
                new() { Height = GridLength.Star },  // 2 歌曲列表
            },
        };
        grid.Children.Add(headFrame.GridRow(0));
        grid.Children.Add(new VerticalStackLayout { Children = { titleLabel, metaLabel, playAllButton } }.GridRow(1));
        grid.Children.Add(songsView.GridRow(2));
        return grid;
    }

    private async Task PopAsyncSafe()
    {
        try { if (Shell.Current?.Navigation is { } nav) await nav.PopAsync(); }
        catch { }
    }

    /// <summary>播放全部：取直链 → 整列表入队播放。</summary>
    private async Task PlayAllAsync()
    {
        if (_songs.Count == 0) return;
        await _dvm.PlayAllAsync(_songs.ToList(), _playlist.Name);
    }
}

/// <summary>详情页自用轻量 VM：持主 VM 引用以复用播放命令与插件。</summary>
internal sealed class LxPlaylistDetailViewModel
{
    private readonly LxOnlineMusicViewModel _mainVm;

    public LxPlaylistDetailViewModel(LxOnlineMusicViewModel mainVm, IServiceProvider services)
    {
        _mainVm = mainVm;
    }

    public bool ScriptReady => _mainVm.ScriptReady;

    /// <summary>播放单曲（复用主 VM 的播放逻辑）。</summary>
    public Task PlaySongAsync(OnlineSong song) => _mainVm.PlaySongCommand.ExecuteAsync(song);

    /// <summary>播放整列表（播放全部）。</summary>
    public Task PlayAllAsync(IReadOnlyList<OnlineSong> songs, string playName)
        => _mainVm.PlayAllSongsAsync(songs, playName);
}

/// <summary>封面 URL → 图片源（空 → 占位图标）。</summary>
internal static class LxDetailCoverConverter
{
    public static ImageSource ToSource(string? url)
        => string.IsNullOrWhiteSpace(url) ? "ic_music_note" : ImageSource.FromUri(new Uri(url));
}