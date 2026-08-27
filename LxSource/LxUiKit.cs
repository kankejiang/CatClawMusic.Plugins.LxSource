using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Input;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LX 插件共享 UI：歌曲行模板 / 音源 chip 模板 / 封面内存流转换器。
/// 全部 C# 代码构建（不用 XAML，避免跨程序集编译问题）。
/// </summary>
public static class LxUiKit
{
    /// <summary>宿主主题主色（"PrimaryColor" 资源），取不到时回落默认紫色。</summary>
    public static Color GetPrimaryColor()
    {
        if (Application.Current?.Resources.TryGetValue("PrimaryColor", out var v) == true && v is Color c)
            return c;
        return Color.FromArgb("#7B68EE");
    }

    /// <summary>长按歌曲 → 歌曲操作菜单（PointerGestureRecognizer + 500ms 计时；MAUI 无内置 LongPress）。
    /// 按下后移动超阈值（滚动/拖拽）或列表滚动（Scrolled）均取消计时。</summary>
    public static View CreateSongItemTemplate(ICommand? longPressCommand = null)
    {
        var coverBorder = new Border
        {
            WidthRequest = 40,
            HeightRequest = 40,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            StrokeThickness = 0,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 40, HeightRequest = 40 };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl), converter: OnlineUrlToImageSourceConverter.Instance) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var titleLabel = new Label { FontSize = 14, FontFamily = "OpenSansSemibold", MaxLines = 1 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        titleLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Title));

        var artistLabel = new Label { FontSize = 11, MaxLines = 1 };
        artistLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        artistLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Artist));

        var durationLabel = new Label { FontSize = 11, VerticalOptions = LayoutOptions.Center };
        durationLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        durationLabel.SetBinding(Label.TextProperty, new Binding(nameof(OnlineSong.DurationMs), converter: DurationConverter.Instance));

        var root = new Grid
        {
            Padding = new Thickness(14, 8),
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 12,
            Children =
            {
                coverBorder,
                new VerticalStackLayout
                {
                    Spacing = 2,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { titleLabel, artistLabel },
                }.GridColumn(1),
                durationLabel.GridColumn(2),
            },
        };

        // 长按 → 歌曲操作菜单（PointerGestureRecognizer + 500ms 计时；MAUI 无内置 LongPress）
        if (longPressCommand != null)
        {
            var pointer = new PointerGestureRecognizer();
            var cts = new CancellationTokenSource();
            Point? pressedPos = null;
            const double moveThreshold = 12;  // 移动超过该像素视为滚动/拖拽，取消长按
            pointer.PointerPressed += (_, e) =>
            {
                cts.Cancel();
                RemoveActivePress(cts);
                cts = new CancellationTokenSource();
                AddActivePress(cts);
                // 屏幕坐标兜底（元素未附加时 GetPosition(root) 可能返回 null）
                pressedPos = e.GetPosition(root) ?? e.GetPosition(null);
                var ct = cts.Token;
                _ = Task.Delay(500, ct).ContinueWith(_ =>
                {
                    if (ct.IsCancellationRequested) return;
                    RemoveActivePress(cts);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (longPressCommand.CanExecute(root.BindingContext))
                            longPressCommand.Execute(root.BindingContext);
                    });
                }, ct);
            };
            // 拖动/滚动时手指移动 → 超阈值取消计时
            pointer.PointerMoved += (_, e) =>
            {
                if (cts.IsCancellationRequested || pressedPos is not { } p0) return;
                var p1 = e.GetPosition(root) ?? e.GetPosition(null);
                if (p1 is not { } p) return;
                if (Math.Abs(p.X - p0.X) > moveThreshold || Math.Abs(p.Y - p0.Y) > moveThreshold)
                    cts.Cancel();
            };
            pointer.PointerReleased += (_, _) =>
            {
                cts.Cancel();
                RemoveActivePress(cts);
            };
            root.GestureRecognizers.Add(pointer);
        }
        return root;
    }

    // ── 长按计时注册表：列表滚动（Scrolled）时统一取消，防止滚动误触发 ──

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CancellationTokenSource, byte> ActivePresses = new();

    private static void AddActivePress(CancellationTokenSource cts) => ActivePresses[cts] = 0;

    private static void RemoveActivePress(CancellationTokenSource cts) => ActivePresses.TryRemove(cts, out _);

    /// <summary>取消所有正在计时的长按（由列表 Scrolled 事件调用，滚动即视为非长按）。</summary>
    public static void CancelAllLongPresses()
    {
        foreach (var cts in ActivePresses.Keys) cts.Cancel();
        ActivePresses.Clear();
    }

    /// <summary>音源 chip 模板（Tap 命令绑定到指定源，参数为 chip 项本身）</summary>
    public static DataTemplate CreateChipTemplate(object commandSource, string commandPropertyName)
    {
        return new DataTemplate(() =>
        {
            var chip = new Border
            {
                Padding = new Thickness(10, 5),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
            };
            chip.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            var chipLabel = new Label { FontSize = 11, VerticalOptions = LayoutOptions.Center };
            chipLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
            chipLabel.SetBinding(Label.TextProperty, nameof(LxSourceChipItem.Name));
            chip.Content = chipLabel;
            chip.Triggers.Add(new DataTrigger(typeof(Border))
            {
                Binding = new Binding(nameof(LxSourceChipItem.IsSelected)),
                Value = true,
                Setters = { new Setter { Property = Border.BackgroundColorProperty, Value = GetPrimaryColor() } },
            });
            chipLabel.Triggers.Add(new DataTrigger(typeof(Label))
            {
                Binding = new Binding(nameof(LxSourceChipItem.IsSelected)),
                Value = true,
                Setters = { new Setter { Property = Label.TextColorProperty, Value = Colors.White } },
            });
            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(commandPropertyName, source: commandSource));
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            chip.GestureRecognizers.Add(tap);
            return chip;
        });
    }

    /// <summary>歌单/榜单卡片模板（2 列网格）：16:9 圆角封面图 + 右上歌曲数徽标 + 名称两行。
    /// Tap 命令绑定到指定源，参数为 <see cref="OnlinePlaylist"/> 项本身。</summary>
    public static DataTemplate CreatePlaylistCardTemplate(object commandSource, string commandPropertyName)
    {
        return new DataTemplate(() =>
        {
            var coverImage = new Image
            {
                Aspect = Aspect.AspectFill,
                HeightRequest = 100,
                WidthRequest = 100,
            };
            coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl),
                converter: OnlineUrlToImageSourceConverter.Instance) { TargetNullValue = "ic_music_note" });

            var countBadge = new Label
            {
                FontSize = 9,
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb("#A6000000"),
                Padding = new Thickness(6, 2),
                VerticalOptions = LayoutOptions.Start,
                HorizontalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 4, 4, 0),
            };
            countBadge.SetBinding(Label.TextProperty,
                new Binding(nameof(OnlinePlaylist.SongCount)) { StringFormat = "{0}首" });

            var coverBorder = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                HeightRequest = 100,
            };
            coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            coverBorder.Content = new Grid { Children = { coverImage, countBadge } };

            var nameLabel = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
            nameLabel.SetBinding(Label.TextProperty, nameof(OnlinePlaylist.Name));

            var card = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(6),
                Margin = new Thickness(5, 6),
            };
            card.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            card.Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children = { coverBorder, nameLabel },
            };

            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(commandPropertyName, source: commandSource));
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            card.GestureRecognizers.Add(tap);
            return card;
        });
    }

    // ── 值转换器 ──

    /// <summary>DurationMs（long，毫秒）→ "m:ss" / "h:mm:ss"</summary>
    private sealed class DurationConverter : IValueConverter
    {
        public static readonly DurationConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            long ms = value switch
            {
                long l => l,
                int i => i,
                _ => 0,
            };
            if (ms <= 0) return "";
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>在线 URL → UriImageSource（平台图片加载器负责下载与缓存）。
    /// 非法/空 URL 回落占位图标；http 自动升 https（酷我/咪咕 CDN 均支持）。
    /// 注意不能用 ImageSource.FromStream：Android Release 构建存在流图片不显示的已知 bug
    /// （dotnet/maui #25283/#30734），且大图无采样易触发 too-large-bitmap 崩溃。</summary>
    private sealed class OnlineUrlToImageSourceConverter : IValueConverter
    {
        public static readonly OnlineUrlToImageSourceConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url)) return "ic_music_note";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "ic_music_note";
            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                // 酷我封面貌似 URL 自带显式 :80 端口（如 http://img4.kuwo.cn:80/...）。
                // 直接改 Scheme 会得到 https://host:80 —— https 连 80 端口必然超时，
                // 造成大量图片加载失败（"Unable to load image stream"/TaskCanceled）与卡顿。
                // 必须把端口重置为默认（-1 → https 默认 443）。
                var ub = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
                uri = ub.Uri;
            }
            return ImageSource.FromUri(uri);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>小工具：链式设置 Grid.Column（让集合初始化器里的子视图也能设列号）</summary>
internal static class GridColumnExtensions
{
    public static T GridColumn<T>(this T view, int column) where T : BindableObject
    {
        Grid.SetColumn(view, column);
        return view;
    }
}

/// <summary>跨环境页面导航。Android 始终在 Shell 内，常规压/弹栈即可；
/// Windows 桌面宿主是 Window(DesktopBlankPage)（无 Shell，直接取 Shell.Current 会抛
/// InvalidOperationException），须反射调用宿主 DesktopNavigation.PushEmbed/PopOrClose
/// 把页面嵌入主区域（宿主自己的二级页同样走这条路），反射不可用再回落 MainPage 模态。</summary>
internal static class LxNav
{
    /// <summary>打开页面：Shell 环境压栈；桌面嵌入主区域；都不可用则模态兜底。</summary>
    public static async Task PushAsync(Page page)
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(() => PushCoreAsync(page));
            return;
        }
        await PushCoreAsync(page);
    }

    /// <summary>返回：页面在模态栈则弹模态；Shell 环境弹导航栈；桌面关闭嵌入页。</summary>
    public static async Task PopAsync(Page page)
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(() => PopCoreAsync(page));
            return;
        }
        await PopCoreAsync(page);
    }

    private static async Task PushCoreAsync(Page page)
    {
        if (TryGetShell()?.Navigation is { } nav)
        {
            try { await nav.PushAsync(page); } catch { }
            return;
        }
        if (TryHostNavigate("PushEmbed", page)) return;
        var root = Application.Current?.MainPage;
        if (root != null)
        {
            try { await root.Navigation.PushModalAsync(page); } catch { }
        }
    }

    private static async Task PopCoreAsync(Page page)
    {
        try
        {
            var root = Application.Current?.MainPage;
            if (root?.Navigation.ModalStack.Contains(page) == true)
            {
                await root.Navigation.PopModalAsync();
                return;
            }
        }
        catch { }
        if (TryGetShell()?.Navigation is { } nav && nav.NavigationStack.Count > 1)
        {
            try { await nav.PopAsync(); } catch { }
            return;
        }
        TryHostNavigate("PopOrClose", null);
    }

    private static Shell? TryGetShell()
    {
        try { return Shell.Current; }
        catch { return null; }
    }

    /// <summary>反射调用宿主 CatClawMusic.Maui 的 DesktopNavigation 静态方法。
    /// 插件只引用 CatClawMusic.Core，拿不到宿主 MAUI 层类型，用反射桥接。</summary>
    private static bool TryHostNavigate(string methodName, Page? page)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "CatClawMusic.Maui", StringComparison.Ordinal));
            var method = asm?.GetType("CatClawMusic.Maui.Helpers.DesktopNavigation")?
                .GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null) return false;
            method.Invoke(null, page != null ? new object?[] { page } : null);
            return true;
        }
        catch { return false; }
    }
}
