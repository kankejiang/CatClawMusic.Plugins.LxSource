using System.Globalization;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 源音乐页面：右上齿轮打开底部 sheet（音源脚本导入 + 默认源 + 音质）；
/// 主页面展示已加载脚本的源能力概览卡。
/// <para>设计参考 lx-music-mobile（lyswhut/lx-music-mobile）主页：
/// 设置作为独立入口 + 主页面按音源能力动态渲染内容。</para>
/// <para>全部 C# 代码构建 UI（不用 XAML，避免跨程序集编译问题）。</para>
/// </summary>
public class LxOnlineMusicPage : ContentPage
{
    private readonly LxOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;
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

        // ── 顶部：返回 + 标题 + 齿轮设置入口 ──
        var headerGrid = BuildHeader();

        // ── 主页面内容（音源 chips + 能力概览卡 + 加载指示器 + 轻提示）──
        var mainContent = BuildMainContent();

        // ── 底部设置 sheet（齿轮打开：脚本导入 + 默认源 + 音质）──
        var sheetContainer = BuildSettingsSheet();

        // ── 组装：主页面在底层，sheet 容器覆盖在上 ──
        var root = new Grid
        {
            Children = { mainContent, sheetContainer },
        };
        Content = root;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.OnAppearing();
    }

    // ── 顶部 header ──

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

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 12, 16, 8),
            VerticalOptions = LayoutOptions.Start,  // 顶部 header 不参与剩余空间分配
            Children = { backButton, titleLabel, settingsButton },
        };
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(settingsButton, 2);
        return grid;
    }

    /// <summary>主页面：音源 chips + 能力概览卡 + 加载指示器 + 轻提示</summary>
    private Grid BuildMainContent()
    {
        // ── 音源 chips ──
        var chipsLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 4, 16, 6) };
        BindableLayout.SetItemsSource(chipsLayout, _vm.SourceChips);
        BindableLayout.SetItemTemplate(chipsLayout, LxUiKit.CreateChipTemplate(_vm, nameof(LxOnlineMusicViewModel.SelectSourceCommand)));
        var chipsScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 38,
            Content = chipsLayout,
        };

        // ── 能力概览卡 ──
        var overviewView = BuildCapabilityOverview();

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

        // 主内容栈：header + chips + 概览（最后一个 VerticalOptions=Fill 占满剩余空间）
        var stack = new VerticalStackLayout
        {
            Spacing = 0,
            Children = { BuildHeader(), chipsScroll, overviewView },
        };

        var contentGrid = new Grid
        {
            Children = { stack, loadingIndicator, tipBorder },
        };
        chipsScroll.VerticalOptions = LayoutOptions.Start;
        overviewView.VerticalOptions = LayoutOptions.Fill;
        return contentGrid;
    }

    /// <summary>能力概览卡：根据 _vm.Capabilities 渲染每源的名称 + actions 小 chips。
    /// 布局：Grid 两行（summary 自动高 + cardScroll 占满剩余可滚动），空状态覆盖层叠在上面。</summary>
    private View BuildCapabilityOverview()
    {
        var summaryLabel = new Label
        {
            FontSize = 12,
            MaxLines = 2,
            Padding = new Thickness(20, 16, 20, 6),
        };
        summaryLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        summaryLabel.SetBinding(Label.TextProperty, nameof(LxOnlineMusicViewModel.CapabilitySummary));

        var cardList = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(16, 4, 16, 16),
        };
        BindableLayout.SetItemsSource(cardList, _vm.Capabilities);
        BindableLayout.SetItemTemplate(cardList, new DataTemplate(() =>
        {
            var nameLabel = new Label
            {
                FontSize = 14,
                FontFamily = "OpenSansSemibold",
            };
            nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
            nameLabel.SetBinding(Label.TextProperty, nameof(LxCapabilityItem.Name));

            // action 小 chips（横向排列；actions 不会很多，简单处理）
            var actionsHost = new HorizontalStackLayout { Spacing = 4 };
            actionsHost.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(LxCapabilityItem.Actions)));
            BindableLayout.SetItemTemplate(actionsHost, new DataTemplate(() =>
            {
                var chipBorder = new Border
                {
                    Padding = new Thickness(8, 3),
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                };
                chipBorder.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
                var chipLabel = new Label { FontSize = 10, TextColor = Colors.White };
                chipLabel.SetBinding(Label.TextProperty, ".");
                chipBorder.Content = chipLabel;
                return chipBorder;
            }));

            var cardBorder = new Border
            {
                Padding = new Thickness(14, 12),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
            };
            cardBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            cardBorder.Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children = { nameLabel, actionsHost },
            };
            return cardBorder;
        }));

        // cardScroll 放在 Grid Star 行：ScrollView 有明确高度约束才能滚动，避免
        // 嵌在 VerticalStackLayout 里内容超高被裁剪。
        var cardScroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = cardList,
            VerticalOptions = LayoutOptions.Fill,
        };

        // 空状态：未导入脚本
        var emptyLabel = new Label
        {
            Text = "未导入音源脚本\n点击右上角 ⚙ 设置导入",
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            MaxLines = 3,
            Margin = new Thickness(24, 48, 24, 0),
        };
        emptyLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        var emptyOverlay = new Grid
        {
            Children = { emptyLabel },
        };
        // CapabilitySummary 为空（未导入脚本/无声明源）→ 显示空状态提示
        emptyOverlay.SetBinding(VisualElement.IsVisibleProperty,
            new Binding(nameof(LxOnlineMusicViewModel.CapabilitySummary))
            { Converter = StringNullOrEmptyToBoolConverter.Instance });

        var container = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Star },
            },
            Children =
            {
                summaryLabel.GridRow(0),
                cardScroll.GridRow(1),
                emptyOverlay.GridRowSpan(2),
            },
        };
        return container;
    }

    // ── 设置 sheet（半屏底部弹出）──

    private Grid BuildSettingsSheet()
    {
        // 半屏 sheet 面板（顶角圆角，从屏幕外 TranslationY 弹出）
        var sheet = new Border
        {
            BackgroundColor = Color.FromArgb("#F214171F"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            Padding = new Thickness(0),
            VerticalOptions = LayoutOptions.End,
            HeightRequest = SheetHiddenY,
            TranslationY = SheetHiddenY,
        };
        sheet.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        // 打开/关闭动画（IsSettingsOpen 变化 → TranslateTo 平滑过渡）
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LxOnlineMusicViewModel.IsSettingsOpen))
            {
                var target = _vm.IsSettingsOpen ? 0.0 : SheetHiddenY;
                _ = sheet.TranslateTo(0, target, 220u, Easing.CubicOut);
            }
        };

        // sheet 顶部拖动条 + 关闭
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

        var sheetHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            Children = { new VerticalStackLayout { HorizontalOptions = LayoutOptions.Center, Children = { dragBar } }, closeButton },
        };
        Grid.SetColumn(closeButton, 1);

        // sheet 标题 + 状态
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

        // URL 输入 + 导入按钮
        var urlEntry = new Entry { Placeholder = "https://.../render_api.js（在线地址）" };
        urlEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        urlEntry.SetBinding(Entry.TextProperty, new Binding(nameof(LxOnlineMusicViewModel.ScriptUrl), mode: BindingMode.TwoWay));
        urlEntry.ReturnType = ReturnType.Go;
        urlEntry.Completed += async (_, _) => await _vm.ImportOnlineCommand.ExecuteAsync(null);

        var importOnlineButton = CreateActionButton("在线导入", _vm.ImportOnlineCommand, filled: true);
        var importLocalButton = CreateActionButton("本地导入", null);
        importLocalButton.GestureRecognizers.Add(MakeTapForLocalImport());
        var clearButton = CreateActionButton("清除", _vm.ClearScriptCommand);

        var urlBlock = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(20, 0, 20, 12),
            Children =
            {
                urlEntry,
                new HorizontalStackLayout { Spacing = 8, Children = { importOnlineButton, importLocalButton, clearButton } },
            },
        };

        // 当前脚本文件路径（如果有）
        var fileLabel = new Label { FontSize = 10, MaxLines = 1, Margin = new Thickness(20, 0, 20, 8) };
        fileLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        fileLabel.SetBinding(Label.TextProperty,
            new Binding(nameof(LxOnlineMusicViewModel.ScriptFilePath))
            { Converter = NonEmptyPrefixConverter.Instance, ConverterParameter = "本地脚本：" });

        // 默认源
        var sourceLabel = new Label
        {
            Text = "默认音源",
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(20, 8, 20, 4),
        };
        sourceLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var sourceChipsLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(20, 0, 20, 12) };
        BindableLayout.SetItemsSource(sourceChipsLayout, _vm.SourceChips);
        BindableLayout.SetItemTemplate(sourceChipsLayout, LxUiKit.CreateChipTemplate(_vm, nameof(LxOnlineMusicViewModel.SelectSourceCommand)));

        // 音质 chips（128k/320k/FLAC）
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
            Children =
            {
                CreateQualityChip("128k"),
                CreateQualityChip("320k"),
                CreateQualityChip("FLAC"),
            },
        };

        // sheet 内容（可滚动，避免高度不够时溢出）
        var sheetContent = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    sheetHeader,
                    sheetTitle,
                    sheetStatus,
                    urlBlock,
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

        // 遮罩（半透黑），点击关闭 sheet
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
        Grid.SetRowSpan(overlay, 1);
        // 关键：sheet 未打开时容器必须穿透触摸（否则全屏 Grid 会拦截主页面所有点击，
        // 齿轮/音源 chips 全部失效）；打开时再接收输入。
        container.SetBinding(VisualElement.InputTransparentProperty,
            new Binding(nameof(LxOnlineMusicViewModel.IsSettingsOpen))
            { Converter = InverseBoolConverter.Instance });
        return container;
    }

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
        // 选中样式（与 SourceChips 类似）
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

    // ── 通用工具 ──

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

/// <summary>Bool → TranslationY 转换器（sheet 打开/关闭：true→0，false→隐藏值）。</summary>
internal sealed class SheetOpenToTranslationConverter : IValueConverter
{
    public static readonly SheetOpenToTranslationConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hidden = parameter is double d ? d : 480.0;
        return value is bool b && b ? 0.0 : hidden;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串为 null/空 → true（用于能力概览卡空状态显示）。</summary>
internal sealed class StringNullOrEmptyToBoolConverter : IValueConverter
{
    public static readonly StringNullOrEmptyToBoolConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → !bool（用于 InputTransparent 反转：sheet 打开=false 可交互，关闭=true 穿透）。</summary>
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