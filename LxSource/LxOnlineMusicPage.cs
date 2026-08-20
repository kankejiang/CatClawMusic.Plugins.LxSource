using System.Globalization;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面 —— 布局照搬 lx-music-mobile（lyswhut/lx-music-mobile）主页：
/// 搜索框（酷我搜索）+ 榜单横向 chips（热歌榜/飙升榜/新歌榜…）+ 歌曲列表（点歌入队播放，
/// 播放直链由 ⚙ 设置导入的自定义源脚本解析）。设置入口：右上角 ⚙ → 底部 sheet。
/// <para>全部 C# 代码构建 UI（不用 XAML，避免跨程序集编译问题）。</para>
/// </summary>
public class LxOnlineMusicPage : ContentPage
{
    private readonly LxOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;
    private CollectionView _songsView = null!;
    private static readonly double SheetHiddenY = 480;

    public LxOnlineMusicPage(LxOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        Title = "LX 源音乐";
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        var mainContent = BuildMainContent();
        var sheetContainer = BuildSettingsSheet();
        var songMenuContainer = BuildSongMenuOverlay();

        Content = new Grid { Children = { mainContent, sheetContainer, songMenuContainer } };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.OnAppearing();
    }

    /// <summary>主内容：header + 搜索框 + 榜单 chips + 歌曲列表 + loading/tip</summary>
    private Grid BuildMainContent()
    {
        // ── header：返回 + 标题 + ⚙ ──
        var header = BuildHeader();

        // ── 搜索框（酷我搜索）──
        var searchEntry = new Entry { Placeholder = "搜索歌曲 / 歌手 / 专辑…" };
        searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        searchEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "TextHintColor");
        searchEntry.SetBinding(Entry.TextProperty, new Binding(nameof(LxOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.Completed += async (_, _) => await _vm.SearchCommand.ExecuteAsync(null);

        var searchButton = new Label
        {
            Text = "🔍",
            FontSize = 15,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        searchButton.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var searchTap = new TapGestureRecognizer();
        searchTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(LxOnlineMusicViewModel.SearchCommand));
        searchButton.GestureRecognizers.Add(searchTap);

        var searchBorder = new Border
        {
            Padding = new Thickness(14, 4, 6, 4),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Margin = new Thickness(16, 0, 16, 6),
            VerticalOptions = LayoutOptions.Start,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new() { Width = GridLength.Star },
                    new() { Width = GridLength.Auto },
                },
                ColumnSpacing = 6,
                Children = { searchEntry, searchButton },
            }.WithChildColumn(searchButton, 1),
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        // ── 榜单 chips（横向滚动）──
        var boardChipsLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 2, 16, 4) };
        BindableLayout.SetItemsSource(boardChipsLayout, _vm.BoardChips);
        BindableLayout.SetItemTemplate(boardChipsLayout, LxUiKit.CreateChipTemplate(_vm, nameof(LxOnlineMusicViewModel.SelectBoardCommand)));
        var boardChipsScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Start,
            Content = boardChipsLayout,
        };

        // ── 列表标题 + 歌曲列表 ──
        var listTitleLabel = new Label
        {
            FontSize = 13,
            FontFamily = "OpenSansSemibold",
            Padding = new Thickness(20, 6, 20, 4),
            VerticalOptions = LayoutOptions.Start,
        };
        listTitleLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        listTitleLabel.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.ListTitle));

        _songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            VerticalOptions = LayoutOptions.Fill,
            EmptyView = new Label
            {
                Text = "加载中…",
                FontSize = 12,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 40, 24, 0),
            },
        };
        ((Label)_songsView.EmptyView).SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        _songsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(LxOnlineMusicViewModel.Songs));
        // 长按 → 歌曲操作菜单（命令源为 VM，参数为歌曲项）
        _songsView.ItemTemplate = new DataTemplate(() =>
            LxUiKit.CreateSongItemTemplate(_vm.OpenSongMenuCommand));
        _songsView.SelectionChanged += OnSongSelected;

        // ── loading / tip ──
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

        // 布局：Grid 行（header / 搜索框 / 榜单chips / 标题 / 列表*），loading/tip 覆盖
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },  // 0 header
                new() { Height = GridLength.Auto },  // 1 搜索框
                new() { Height = GridLength.Auto },  // 2 榜单 chips
                new() { Height = GridLength.Auto },  // 3 列表标题
                new() { Height = GridLength.Star },  // 4 歌曲列表
            },
            Children =
            {
                header.GridRow(0),
                searchBorder.GridRow(1),
                boardChipsScroll.GridRow(2),
                listTitleLabel.GridRow(3),
                _songsView.GridRow(4),
                loadingIndicator.GridRow(4),
                tipBorder.GridRowSpan(5),
            },
        };
        return grid;
    }

    // ── header ──

    private Grid BuildHeader()
    {
        var backButton = CreateBackButton();
        var titleLabel = new Label
        {
            Text = "LX 源音乐",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var settingsButton = CreateSettingsButton();

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 12, 16, 8),
            VerticalOptions = LayoutOptions.Start,
            Children = { backButton, titleLabel, settingsButton },
        }.WithChildColumn(titleLabel, 1).WithChildColumn(settingsButton, 2);
    }

    // ── 设置 sheet（半屏底部弹出：脚本导入 + 默认源 + 音质）──

    private Grid BuildSettingsSheet()
    {
        var sheet = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            Padding = new Thickness(0),
            VerticalOptions = LayoutOptions.End,
            HeightRequest = SheetHiddenY,
            TranslationY = SheetHiddenY,
        };
        sheet.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LxOnlineMusicViewModel.IsSettingsOpen))
            {
                var target = _vm.IsSettingsOpen ? 0.0 : SheetHiddenY;
                _ = sheet.TranslateTo(0, target, 220u, Easing.CubicOut);
            }
        };

        var dragBar = new BoxView
        {
            HeightRequest = 4,
            WidthRequest = 36,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 8, 0, 8),
            Color = Color.FromArgb("#8A808080"),
        };

        var closeLabel = new Label { Text = "✕", FontSize = 16 };
        closeLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var closeButton = new Border
        {
            Padding = new Thickness(12, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, -8, 12, 0),
            Content = closeLabel,
        };
        closeButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var closeTap = new TapGestureRecognizer();
        closeTap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(nameof(LxOnlineMusicViewModel.CloseSettingsCommand)));
        closeButton.GestureRecognizers.Add(closeTap);

        var sheetTitle = new Label
        {
            Text = "音源脚本设置",
            FontSize = 15,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(20, 4, 20, 4),
        };
        sheetTitle.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var sheetStatus = new Label
        {
            FontSize = 11,
            MaxLines = 2,
            Margin = new Thickness(20, 0, 20, 8),
        };
        sheetStatus.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        sheetStatus.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.ScriptStatus));

        var urlEntry = new Entry { Placeholder = "https://.../render_api.js（在线地址）" };
        urlEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        urlEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "TextHintColor");
        urlEntry.SetBinding(Entry.TextProperty, new Binding(nameof(LxOnlineMusicViewModel.ScriptUrl), mode: BindingMode.TwoWay));
        urlEntry.ReturnType = ReturnType.Go;
        urlEntry.Completed += async (_, _) => await _vm.ImportOnlineCommand.ExecuteAsync(null);

        var importOnlineButton = CreateActionButton("在线导入", _vm.ImportOnlineCommand, filled: true);
        var importLocalButton = CreateActionButton("本地导入", null);
        importLocalButton.GestureRecognizers.Add(MakeTapForLocalImport());
        var clearButton = CreateActionButton("清除", _vm.ClearScriptCommand);

        var fileLabel = new Label { FontSize = 10, MaxLines = 1, Margin = new Thickness(20, 0, 20, 8) };
        fileLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        fileLabel.SetBinding(Label.TextProperty,
            new Binding(nameof(LxOnlineMusicViewModel.ScriptFilePath))
            { Converter = NonEmptyPrefixConverter.Instance, ConverterParameter = "本地脚本：" });

        var sourceLabel = new Label
        {
            Text = "默认音源（脚本解析直链时优先使用）",
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(20, 4, 20, 4),
        };
        sourceLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var sourceChipsLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(20, 0, 20, 12) };
        BindableLayout.SetItemsSource(sourceChipsLayout, _vm.SourceChips);
        BindableLayout.SetItemTemplate(sourceChipsLayout, LxUiKit.CreateChipTemplate(_vm, nameof(LxOnlineMusicViewModel.SelectSourceCommand)));

        var qualityLabel = new Label
        {
            Text = "默认音质",
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(20, 4, 20, 4),
        };
        qualityLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var qualityRow = new HorizontalStackLayout
        {
            Spacing = 6,
            Padding = new Thickness(20, 0, 20, 24),
            Children = { CreateQualityChip("128k"), CreateQualityChip("320k"), CreateQualityChip("FLAC") },
        };

        var sheetContent = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitionCollection
                        {
                            new() { Width = GridLength.Star },
                            new() { Width = GridLength.Auto },
                        },
                        Children =
                        {
                            new VerticalStackLayout { HorizontalOptions = LayoutOptions.Center, Children = { dragBar } },
                            closeButton,
                        },
                    }.WithChildColumn(closeButton, 1),
                    sheetTitle,
                    sheetStatus,
                    new VerticalStackLayout
                    {
                        Spacing = 8,
                        Padding = new Thickness(20, 0, 20, 12),
                        Children =
                        {
                            urlEntry,
                            new HorizontalStackLayout { Spacing = 8, Children = { importOnlineButton, importLocalButton, clearButton } },
                        },
                    },
                    fileLabel,
                    sourceLabel,
                    new ScrollView
                    {
                        Orientation = ScrollOrientation.Horizontal,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                        Content = sourceChipsLayout,
                    },
                    qualityLabel,
                    qualityRow,
                },
            },
        };
        sheet.Content = sheetContent;

        var overlay = new BoxView
        {
            Color = Color.FromArgb("#80000000"),
            IsVisible = false,
        };
        overlay.SetBinding(VisualElement.IsVisibleProperty, nameof(LxOnlineMusicViewModel.IsSettingsOpen));
        var overlayTap = new TapGestureRecognizer();
        overlayTap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(nameof(LxOnlineMusicViewModel.CloseSettingsCommand)));
        overlay.GestureRecognizers.Add(overlayTap);

        var container = new Grid
        {
            Children = { overlay, sheet },
        };
        // 关闭时容器穿透触摸（否则全屏 Grid 拦截主页面所有点击）
        container.SetBinding(VisualElement.InputTransparentProperty,
            new Binding(nameof(LxOnlineMusicViewModel.IsSettingsOpen))
            { Converter = InverseBoolConverter.Instance });
        return container;
    }

    // ── 歌曲操作覆盖层：长按菜单 sheet + 下载音质选择弹窗 ──

    private Grid BuildSongMenuOverlay()
    {
        const double menuHiddenY = 320;

        // 菜单 sheet
        var menuSheet = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            Padding = new Thickness(0),
            VerticalOptions = LayoutOptions.End,
            HeightRequest = menuHiddenY,
            TranslationY = menuHiddenY,
        };
        menuSheet.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        // 菜单歌曲标题
        var songLabel = new Label
        {
            FontSize = 13,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
            Padding = new Thickness(24, 2, 24, 2),
        };
        songLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        songLabel.SetBinding(Label.TextProperty,
            new Binding(nameof(LxOnlineMusicViewModel.MenuSong))
            { Converter = SongTitleConverter.Instance });

        // 菜单项
        var menuItems = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(16, 8, 16, 16) };
        menuItems.Children.Add(CreateMenuRow("▶ 播放", nameof(LxOnlineMusicViewModel.PlayNowCommand)));
        menuItems.Children.Add(CreateMenuRow("⏭ 下一首播放", nameof(LxOnlineMusicViewModel.PlayNextCommand)));
        menuItems.Children.Add(CreateMenuRow("⬇ 下载", nameof(LxOnlineMusicViewModel.OpenDownloadPickerCommand)));

        menuSheet.Content = new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                new BoxView
                {
                    HeightRequest = 4,
                    WidthRequest = 36,
                    HorizontalOptions = LayoutOptions.Center,
                    Margin = new Thickness(0, 8, 0, 8),
                    Color = Color.FromArgb("#8A808080"),
                },
                songLabel,
                menuItems,
            },
        };

        // 下载音质选择弹窗（居中卡片）
        var pickerCard = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Padding = new Thickness(18, 14),
            Margin = new Thickness(36, 0, 36, 0),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            IsVisible = false,
        };
        pickerCard.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        pickerCard.SetBinding(VisualElement.IsVisibleProperty, nameof(LxOnlineMusicViewModel.IsQualityPickerOpen));

        var pickerTitle = new Label
        {
            Text = "选择下载音质",
            FontSize = 15,
            FontFamily = "OpenSansSemibold",
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 2, 0, 10),
        };
        pickerTitle.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var qualityRow = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                CreatePickerButton("128k"),
                CreatePickerButton("320k"),
                CreatePickerButton("无损"),
            },
        };

        var pickerCancel = CreateActionButton("取消", _vm.CancelQualityPickerCommand);
        pickerCancel.HorizontalOptions = LayoutOptions.Center;
        pickerCancel.Margin = new Thickness(0, 12, 0, 0);

        pickerCard.Content = new VerticalStackLayout
        {
            Spacing = 0,
            Children = { pickerTitle, qualityRow, pickerCancel },
        };

        // 遮罩（共用：任一弹层打开时显示并拦截）
        var overlay = new BoxView
        {
            Color = Color.FromArgb("#80000000"),
            IsVisible = false,
        };
        var overlayTap = new TapGestureRecognizer();
        overlayTap.Tapped += async (_, _) =>
        {
            _vm.CloseSongMenuCommand.Execute(null);
            _vm.CancelQualityPickerCommand.Execute(null);
        };
        overlay.GestureRecognizers.Add(overlayTap);

        var container = new Grid
        {
            InputTransparent = true,
            Children = { overlay, menuSheet, pickerCard },
        };

        // 输入穿透 + 遮罩显隐 + 动画：由 VM 状态驱动
        void SyncOverlay()
        {
            var open = _vm.IsSongMenuOpen || _vm.IsQualityPickerOpen;
            overlay.IsVisible = open;
            menuSheet.InputTransparent = !_vm.IsSongMenuOpen;
            pickerCard.InputTransparent = !_vm.IsQualityPickerOpen;
            _ = menuSheet.TranslateTo(0, _vm.IsSongMenuOpen ? 0 : menuHiddenY, 200u, Easing.CubicOut);
            container.InputTransparent = !open;  // 关闭时穿透，不拦截主页面
        }
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(LxOnlineMusicViewModel.IsSongMenuOpen)
                or nameof(LxOnlineMusicViewModel.IsQualityPickerOpen))
            {
                SyncOverlay();
            }
        };
        return container;
    }

    /// <summary>长按菜单行：图标+文字，点击执行指定命令</summary>
    private Border CreateMenuRow(string text, string commandPropertyName)
    {
        var row = new Border
        {
            Padding = new Thickness(16, 13),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
        };
        row.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var label = new Label { Text = text, FontSize = 14 };
        label.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        row.Content = label;
        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(commandPropertyName));
        row.GestureRecognizers.Add(tap);
        return row;
    }

    /// <summary>音质选择按钮（128k/320k/无损 → ConfirmDownloadCommand 参数）</summary>
    private Border CreatePickerButton(string label)
    {
        var btn = CreateActionButton(label, _vm.ConfirmDownloadCommand);
        // ConfirmDownloadCommand 参数 = 音质档标签
        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(nameof(LxOnlineMusicViewModel.ConfirmDownloadCommand)));
        tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("Source") { Source = label });
        btn.GestureRecognizers.Clear();
        btn.GestureRecognizers.Add(tap);
        return btn;
    }

    // ── 事件 ──

    /// <summary>点歌播放（整列表入队）</summary>
    private async void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
        _songsView.SelectedItem = null;
        await _vm.PlaySongCommand.ExecuteAsync(song);
    }

    // ── 小部件 ──

    private Border CreateQualityChip(string label)
    {
        var chip = new Border
        {
            Padding = new Thickness(14, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        chip.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var chipLabel = new Label { Text = label, FontSize = 12, FontFamily = "OpenSansSemibold" };
        chipLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        chipLabel.Triggers.Add(new DataTrigger(typeof(Label))
        {
            Binding = new Binding(nameof(LxOnlineMusicViewModel.QualityText)),
            Value = label,
            Setters = { new Setter { Property = Label.TextColorProperty, Value = Colors.White } },
        });
        chip.Triggers.Add(new DataTrigger(typeof(Border))
        {
            Binding = new Binding(nameof(LxOnlineMusicViewModel.QualityText)),
            Value = label,
            Setters = { new Setter { Property = Border.BackgroundColorProperty, Value = LxUiKit.GetPrimaryColor() } },
        });
        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(nameof(LxOnlineMusicViewModel.SetQualityCommand)));
        tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("Source") { Source = label });
        chip.GestureRecognizers.Add(tap);
        chip.Content = chipLabel;
        return chip;
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

    private Border CreateSettingsButton()
    {
        var btn = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        btn.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var label = new Label { Text = "⚙", FontSize = 16, VerticalOptions = LayoutOptions.Center };
        label.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        btn.Content = label;
        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(nameof(LxOnlineMusicViewModel.OpenSettingsCommand)));
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
}

/// <summary>OnlineSong → "标题 - 歌手"（长按菜单歌曲标题显示）。</summary>
internal sealed class SongTitleConverter : IValueConverter
{
    public static readonly SongTitleConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is OnlineSong s ? $"{s.Title} - {s.Artist}" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → !bool（InputTransparent 反转：sheet 打开=false 可交互，关闭=true 穿透）。</summary>
internal sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串非空 → 加前缀（用于显示本地脚本路径）。</summary>
internal sealed class NonEmptyPrefixConverter : IValueConverter
{
    public static readonly NonEmptyPrefixConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        var prefix = parameter as string ?? "";
        return string.IsNullOrEmpty(s) ? "" : prefix + s;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>小工具：链式设置 Grid 列/行</summary>
internal static class PageGridExtensions
{
    public static Grid WithChildColumn(this Grid grid, View child, int column)
    {
        Grid.SetColumn(child, column);
        return grid;
    }

    public static T GridRow<T>(this T view, int row) where T : BindableObject
    {
        Grid.SetRow(view, row);
        return view;
    }

    public static T GridRowSpan<T>(this T view, int span) where T : BindableObject
    {
        Grid.SetRowSpan(view, span);
        return view;
    }
}