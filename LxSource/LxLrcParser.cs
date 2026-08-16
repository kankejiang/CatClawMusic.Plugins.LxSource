using System.Globalization;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// LRC 解析器：原文 + 译文（tlyric）+ 罗马音（rlyric）三流按时间戳合并，
/// 与 lx-music 音源的 lrc/tlyric/romalrc 三流模型一致。
/// </summary>
public static class LxLrcParser
{
    /// <summary>解析三流歌词为结构化 LrcLyrics；原文为空返回 null</summary>
    public static LrcLyrics? Parse(string lrc, string? tlyric, string? rlyric)
    {
        var main = ParseRaw(lrc);
        if (main == null || main.Count == 0) return null;

        var trans = ParseStream(tlyric);
        var roma = ParseStream(rlyric);

        var lines = new List<LrcLyricLine>();
        foreach (var (ts, text) in main)
        {
            var line = new LrcLyricLine { Timestamp = ts, Text = text };
            if (trans != null && trans.TryGetValue(ts, out var t) && !string.IsNullOrWhiteSpace(t))
                line.Translation = t;
            if (roma != null && roma.TryGetValue(ts, out var r) && !string.IsNullOrWhiteSpace(r))
                line.Roma = r;
            lines.Add(line);
        }
        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return new LrcLyrics { Lines = lines };
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
