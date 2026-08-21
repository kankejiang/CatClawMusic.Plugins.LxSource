using System.Globalization;
using System.Text.RegularExpressions;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LRC 解析器：原文 + 译文（tlyric）+ 罗马音（rlyric）三流按时间戳合并，
/// 与 lx-music 音源的 lrc/tlyric/romalrc 三流模型一致。
/// <para>
/// 兼容 lx-music 生态的歌词细节（对照 lx-music-mobile musicSdk）：
/// 1. 网易系时间标签冒号变体 "[mm:ss:xx]" 归一化为 "[mm:ss.xx]"（官方 fixTimeLabel 同款）；
/// 2. 译文/罗马音与原文时间戳相差 100ms 内视为同一条（官方 fixTimeTag 同款容差）。
/// </para>
/// </summary>
public static class LxLrcParser
{
    /// <summary>三流合并容差（毫秒）：官方 fixTimeTag 用 &lt;100ms 判定同一条</summary>
    private const double MergeToleranceMs = 100;

    /// <summary>解析三流歌词为结构化 LrcLyrics；原文为空返回 null</summary>
    public static LrcLyrics? Parse(string lrc, string? tlyric, string? rlyric)
    {
        var main = ParseRaw(lrc);
        if (main == null || main.Count == 0) return null;

        var trans = ParseStream(tlyric);
        var roma = ParseStream(rlyric);

        var lines = new List<LrcLyricLine>(main.Count);
        foreach (var (ts, text) in main)
        {
            var line = new LrcLyricLine { Timestamp = ts, Text = text };
            if (trans != null && TryGet(trans, ts, out var t) && !string.IsNullOrWhiteSpace(t))
                line.Translation = t;
            if (roma != null && TryGet(roma, ts, out var r) && !string.IsNullOrWhiteSpace(r))
                line.Roma = r;
            lines.Add(line);
        }
        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return new LrcLyrics { Lines = lines };
    }

    /// <summary>
    /// 时间戳容差匹配：精确命中优先；未命中时在 ±100ms 内找最近的一条。
    /// 网易 tlyric/romalrc 与 lrc 的时间戳常有几毫秒到几十毫秒的偏差，
    /// 精确匹配会导致译文/罗马音整条丢失（宿主歌词链同为精确合并，此处先兜住）。
    /// </summary>
    private static bool TryGet(Dictionary<TimeSpan, string> dict, TimeSpan ts, out string? text)
    {
        text = null;
        if (dict.TryGetValue(ts, out var exact))
        {
            text = exact;
            return true;
        }
        TimeSpan? best = null;
        double bestDiff = double.MaxValue;
        foreach (var (key, value) in dict)
        {
            var diff = Math.Abs((key - ts).TotalMilliseconds);
            if (diff <= MergeToleranceMs && diff < bestDiff)
            {
                bestDiff = diff;
                best = key;
            }
        }
        if (best != null)
        {
            text = dict[best.Value];
            return true;
        }
        return false;
    }

    /// <summary>解析单份 LRC 流为时间戳字典（重复时间戳以首个为准）</summary>
    private static Dictionary<TimeSpan, string>? ParseStream(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var raw = ParseRaw(text);
        if (raw == null) return null;
        var dict = new Dictionary<TimeSpan, string>();
        foreach (var (ts, t) in raw)
            if (!dict.ContainsKey(ts)) dict[ts] = t;
        return dict;
    }

    /// <summary>解析单份 LRC 文本为 (时间戳, 文本) 列表（同一行多时间戳展开为多行）</summary>
    private static List<(TimeSpan Ts, string Text)>? ParseRaw(string lrc)
    {
        // 网易系时间标签冒号变体归一化（官方 fixTimeLabel 同款）：
        // "[mm:ss:xx]" / "[mm:ss:xxx]"（冒号代替小数点）→ "[mm:ss.xx]"；"[mm:ss.xx0]" → "[mm:ss.xx]"
        lrc = Regex.Replace(lrc, @"\[(\d{2}:\d{2}):(\d{2,3})]", "[$1.$2]");
        lrc = Regex.Replace(lrc, @"\[(\d{2}:\d{2}\.\d{2})0]", "[$1]");

        var result = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var timestamps = new List<TimeSpan>();
            while (line.StartsWith("[", StringComparison.Ordinal))
            {
                var close = line.IndexOf(']');
                if (close < 0) break;
                var tsStr = line.Substring(1, close - 1).Trim();
                // 支持 mm:ss.xx / mm:ss.xxx / mm:ss
                if (TimeSpan.TryParseExact(tsStr, new[] { @"mm\:ss\.fff", @"mm\:ss\.ff", @"mm\:ss" },
                        CultureInfo.InvariantCulture, out var ts))
                    timestamps.Add(ts);
                line = line.Substring(close + 1);
            }
            var text = line.Trim();
            if (timestamps.Count > 0 && !string.IsNullOrEmpty(text))
                foreach (var ts in timestamps)
                    result.Add((ts, text));
        }
        return result.Count > 0 ? result : null;
    }
}
