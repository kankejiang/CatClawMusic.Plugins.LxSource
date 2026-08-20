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
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
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

    /// <summary>在线 URL → 内存 Stream 封面（不落盘缓存，内存字典去重防重复下载）</summary>
    private sealed class OnlineUrlToStreamImageConverter : IValueConverter
    {
        public static readonly OnlineUrlToStreamImageConverter Instance = new();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, byte[]> MemCache = new();
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> Inflight = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url)) return "ic_music_note";
            return ImageSource.FromStream(ct => LoadAsync(url, ct));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static async Task<Stream> LoadAsync(string url, CancellationToken ct)
        {
            if (!MemCache.TryGetValue(url, out var bytes))
            {
                var task = Inflight.GetOrAdd(url, _ => DownloadAsync(url));
                try { bytes = await task.ConfigureAwait(false); }
                finally { Inflight.TryRemove(url, out _); }
                if (bytes is { Length: > 0 }) MemCache[url] = bytes;
            }
            return new MemoryStream(bytes ?? Array.Empty<byte>());
        }

        private static async Task<byte[]?> DownloadAsync(string url)
        {
            try { return await Http.GetByteArrayAsync(url).ConfigureAwait(false); }
            catch { return null; }
        }
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
