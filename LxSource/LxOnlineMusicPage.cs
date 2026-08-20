using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面：脚本导入（在线 URL / 本地文件）+ 音源选择 + 搜索 + 播放。
/// 全部 C# 代码构建 UI（不用 XAML，避免跨程序集编译问题）。
/// </summary>
public class LxOnlineMusicPage : ContentPage
{
    private readonly LxOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;
    private readonly CollectionView _songsView;

    public LxOnlineMusicPage(LxOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        Title = "LX 源音乐";
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 顶部：返回 + 标题 + 音质 ──
        var backButton = CreateBackButton();
        var titleLabel = new Label
        {
            Text = "LX 源音乐",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var qualityButton = new Border
        {
            Padding = new Thickness(10, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
        };
        qualityButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var qualityLabel = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        qualityLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        qualityLabel.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.QualityText));
        qualityButton.Content = qualityLabel;
        var qualityTap = new TapGestureRecognizer();
        qualityTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(LxOnlineMusicViewModel.CycleQualityCommand));
        qualityButton.GestureRecognizers.Add(qualityTap);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 12, 16, 8),
            Children = { backButton, titleLabel, qualityButton },
        };
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(qualityButton, 2);

        // ── 脚本导入卡 ──
        var statusLabel = new Label { FontSize = 11, MaxLines = 2, VerticalOptions = LayoutOptions.Center };
        statusLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        statusLabel.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.ScriptStatus));

        var cardTitle = new Label { Text = "音源脚本", FontSize = 13, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        cardTitle.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var scriptEntry = new Entry { Placeholder = "https://.../render_api.js（在线地址）" };
        scriptEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        scriptEntry.SetBinding(Entry.TextProperty, new Binding(nameof(LxOnlineMusicViewModel.ScriptUrl), mode: BindingMode.TwoWay));
        scriptEntry.ReturnType = ReturnType.Go;
        scriptEntry.Completed += async (_, _) => await _vm.ImportOnlineCommand.ExecuteAsync(null);

        var importOnlineButton = CreateActionButton("在线导入", _vm.ImportOnlineCommand, filled: true);
        var importLocalButton = CreateActionButton("本地导入", null);
        importLocalButton.GestureRecognizers.Add(MakeTapForLocalImport());
        var clearButton = CreateActionButton("清除", _vm.ClearScriptCommand);

        var scriptCard = new Border
        {
            Padding = new Thickness(14, 12),
            Margin = new Thickness(16, 0, 16, 8),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitionCollection
                        {
                            new() { Width = GridLength.Star },
                            new() { Width = GridLength.Auto },
                        },
                        Children = { cardTitle, statusLabel },
                    }.WithChildColumn(statusLabel, 1),
                    scriptEntry,
                    new HorizontalStackLayout { Spacing = 8, Children = { importOnlineButton, importLocalButton, clearButton } },
                },
            },
        };
        scriptCard.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        // ── 音源 chips（动态：自动 + 脚本声明的源）──
        var chipsLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 4, 16, 6) };
        BindableLayout.SetItemsSource(chipsLayout, _vm.SourceChips);
        var chipsScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 38,
            Content = chipsLayout,
        };

        // ── 搜索行 ──
        var searchEntry = new Entry { Placeholder = "搜索歌曲…" };
        searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        searchEntry.SetBinding(Entry.TextProperty, new Binding(nameof(LxOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.Completed += async (_, _) => await _vm.SearchCommand.ExecuteAsync(null);

        var searchButton = CreateActionButton("搜索", _vm.SearchCommand);
        var searchBorder = new Border
        {
            Padding = new Thickness(14, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(16, 0, 16, 4),
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        searchBorder.Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Children = { searchEntry, searchButton },
        }.WithChildColumn(searchButton, 1);

        // ── 歌曲列表 ──
        _songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            Margin = new Thickness(0, 6, 0, 0),
            EmptyView = new Label
            {
                Text = "导入脚本后搜索歌曲（脚本需声明 musicSearch），点击结果即可播放",
                FontSize = 12,
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 48, 24, 0),
            },
        };
        ((Label)_songsView.EmptyView).SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        _songsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(LxOnlineMusicViewModel.Songs));
        _songsView.ItemTemplate = new DataTemplate(LxUiKit.CreateSongItemTemplate);
        _songsView.SelectionChanged += OnSongSelected;

        // ── 加载指示器 ──
        var loadingIndicator = new ActivityIndicator
        {
            WidthRequest = 36,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
        };
        loadingIndicator.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");
        loadingIndicator.SetBinding(ActivityIndicator.IsRunningProperty, nameof(LxOnlineMusicViewModel.IsBusy));
        loadingIndicator.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(LxOnlineMusicViewModel.IsBusy));

        // ── 轻提示条 ──
        var tipLabel = new Label
        {
            FontSize = 12,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            MaxLines = 2,
            Padding = new Thickness(14, 8),
        };
        tipLabel.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.TipMessage));
        var tipBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Color.FromArgb("#CC000000"),
            Margin = new Thickness(24, 0, 24, 12),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Center,
            Content = tipLabel,
        };
        tipBorder.SetBinding(VisualElement.IsVisibleProperty, nameof(LxOnlineMusicViewModel.HasTip));

        // ── 组装页面 ──
        var contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // script card
                new() { Height = GridLength.Auto }, // source chips
                new() { Height = GridLength.Auto }, // search row
                new() { Height = GridLength.Star }, // content
            },
            Children = { headerGrid, scriptCard, chipsScroll, searchBorder, _songsView, loadingIndicator, tipBorder },
        };
        Grid.SetRow(scriptCard, 1);
        Grid.SetRow(chipsScroll, 2);
        Grid.SetRow(searchBorder, 3);
        Grid.SetRow(_songsView, 4);
        Grid.SetRow(loadingIndicator, 4);
        Grid.SetRow(tipBorder, 4);

        Content = contentGrid;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.OnAppearing();
    }

    /// <summary>本地导入：文件选择器</summary>
    private TapGestureRecognizer MakeTapForLocalImport()
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.WinUI] = new[] { ".js", ".txt" },
                        [DevicePlatform.Android] = new[] { "application/javascript", "text/plain", "application/octet-stream" },
                        [DevicePlatform.iOS] = new[] { "public.java-script", "public.plain-text" },
                        [DevicePlatform.MacCatalyst] = new[] { "public.java-script", "public.plain-text" },
                    }),
                });
                if (result != null)
                    await _vm.ImportLocalFileAsync(result.FullPath);
            }
            catch { /* user cancelled */ }
        };
        return tap;
    }

    /// <summary>点歌播放</summary>
    private async void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
        _songsView.SelectedItem = null;
        await _vm.PlaySongCommand.ExecuteAsync(song);
    }

    private Border CreateBackButton()
    {
        var btn = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        btn.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var label = new Label { Text = "‹", FontSize = 18, VerticalOptions = LayoutOptions.Center };
        label.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        btn.Content = label;
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                if (Shell.Current?.Navigation != null) await Shell.Current.Navigation.PopAsync();
            }
            catch { }
        };
        btn.GestureRecognizers.Add(tap);
        return btn;
    }

    private Border CreateActionButton(string text, System.Windows.Input.ICommand? command, bool filled = false)
    {
        var btn = new Border
        {
            Padding = new Thickness(14, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            VerticalOptions = LayoutOptions.Center,
        };
        if (filled)
            btn.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        else
            btn.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            TextColor = filled ? Colors.White : null,
            VerticalOptions = LayoutOptions.Center,
        };
        if (!filled) label.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        btn.Content = label;
        if (command != null)
        {
            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(".", source: command));
            btn.GestureRecognizers.Add(tap);
        }
        return btn;
    }
}

/// <summary>小工具：链式设置 Grid 列</summary>
internal static class PageGridExtensions
{
    public static Grid WithChildColumn(this Grid grid, View child, int column)
    {
        Grid.SetColumn(child, column);
        return grid;
    }
}
