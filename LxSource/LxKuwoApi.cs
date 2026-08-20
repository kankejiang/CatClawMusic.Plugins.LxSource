using System.Text.Json;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>榜单项（照搬 lx-music-mobile kw leaderboard boardList）。</summary>
public class LxBoardItem
{
    public string Id { get; }
    public string Name { get; }
    public string BangId { get; }

    public LxBoardItem(string id, string name, string bangId)
    {
        Id = id;
        Name = name;
        BangId = bangId;
    }
}

/// <summary>
/// 酷我(kw)内置源 API —— 照搬 lx-music-mobile（lyswhut/lx-music-mobile）的
/// src/utils/musicSdk/kw 实现：内容数据（搜索/排行榜）直连酷我公开 API，
/// 播放直链由用户自定义源脚本（musicUrl action）解析 —— 与 lx 架构一致。
/// <para>API 均无签名：</para>
/// <para>· 搜索  http://search.kuwo.cn/r.s（client=kt，mobi=1，JSON）</para>
/// <para>· 榜单  http://kbangserver.kuwo.cn/ksong.s（旧接口，返回 musiclist）</para>
/// <para>榜单列表为 lx 硬编码的 43 个（kw__93 飙升榜 … kw__151 腾讯音乐人原创榜）。</para>
/// </summary>
public static class LxKuwoApi
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static LxKuwoApi()
    {
        // 部分接口对 UA 敏感（酷我老接口按 UA 返回不同结构）
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/104.0.0.0 Mobile Safari/537.36");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "http://www.kuwo.cn/");
    }

    /// <summary>榜单列表（照搬 lx kw boardList 硬编码 43 个）。</summary>
    public static List<LxBoardItem> GetBoards() => new()
    {
        new("kw__93", "飙升榜", "93"),
        new("kw__17", "新歌榜", "17"),
        new("kw__16", "热歌榜", "16"),
        new("kw__158", "抖音热歌榜", "158"),
        new("kw__292", "铃声榜", "292"),
        new("kw__284", "热评榜", "284"),
        new("kw__290", "ACG新歌榜", "290"),
        new("kw__278", "古风音乐榜", "278"),
        new("kw__264", "Vlog音乐榜", "264"),
        new("kw__242", "电音榜", "242"),
        new("kw__187", "流行趋势榜", "187"),
        new("kw__186", "ACG神曲榜", "186"),
        new("kw__185", "最强翻唱榜", "185"),
        new("kw__26", "经典怀旧榜", "26"),
        new("kw__104", "华语榜", "104"),
        new("kw__182", "粤语榜", "182"),
        new("kw__22", "欧美榜", "22"),
        new("kw__184", "韩语榜", "184"),
        new("kw__183", "日语榜", "183"),
        new("kw__64", "影视金曲榜", "64"),
        new("kw__176", "DJ嗨歌榜", "176"),
        new("kw__12", "Billboard榜", "12"),
        new("kw__49", "iTunes音乐榜", "49"),
        new("kw__15", "日本公信榜", "15"),
        new("kw__151", "腾讯音乐人原创榜", "151"),
    };

    /// <summary>搜索歌曲（酷我公开 API，无签名）。返回 null 表示失败。</summary>
    public static async Task<List<OnlineSong>?> SearchAsync(string keyword, int page = 1, int limit = 30)
    {
        var url = "http://search.kuwo.cn/r.s" +
                  $"?client=kt&all={Uri.EscapeDataString(keyword)}&pn={page - 1}&rn={limit}" +
                  "&uid=794762570&ver=kwplayer_ar_9.2.2.1&vipver=1&show_copyright_off=1&newver=1" +
                  "&ft=music&cluster=0&strategy=2012&encoding=utf8&rformat=json&vermerge=1&mobi=1&issubtitle=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("abslist", out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            return list.EnumerateArray()
                .Select(FromSearchItem)
                .Where(s => s != null)
                .Cast<OnlineSong>()
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取榜单歌曲（酷我旧接口，无签名，每页 100）。返回 null 表示失败。</summary>
    public static async Task<List<OnlineSong>?> GetBoardSongsAsync(string bangId, int page = 1, int limit = 100)
    {
        var url = "http://kbangserver.kuwo.cn/ksong.s" +
                  $"?from=pc&fmt=json&pn={page - 1}&rn={limit}&type=bang&data=content&id={bangId}" +
                  "&show_copyright_off=0&pcmp4=1&isbang=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("musiclist", out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            return list.EnumerateArray()
                .Select(FromBoardItem)
                .Where(s => s != null)
                .Cast<OnlineSong>()
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取歌曲封面（lx kw pic.js 同款 API，返回纯文本 URL；失败返回 null）。</summary>
    public static async Task<string?> GetPicAsync(string songmid)
    {
        try
        {
            var url = $"http://artistpicserver.kuwo.cn/pic.web?corp=kuwo&type=rid_pic&pictype=500&size=500&rid={songmid}";
            var body = await Http.GetStringAsync(url).ConfigureAwait(false);
            body = body.Trim();
            return body.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? body : null;
        }
        catch
        {
            return null;
        }
    }

    // ── 解析 ──

    private static OnlineSong? FromSearchItem(JsonElement info)
    {
        try
        {
            string Get(string k) => info.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var songmid = Get("MUSICRID").Replace("MUSIC_", "");
            if (string.IsNullOrEmpty(songmid)) return null;
            var name = DecodeName(Get("SONGNAME"));
            var artist = FormatSinger(DecodeName(Get("ARTIST")));
            _ = int.TryParse(Get("DURATION"), out var dur);
            return new OnlineSong
            {
                Id = $"kw:{songmid}",
                Platform = "lx",
                PlatformName = "酷我",
                Title = name,
                Artist = artist,
                Album = DecodeName(Get("ALBUM")),
                DurationMs = dur * 1000L,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "kw",
                    ["RawId"] = songmid,
                },
            };
        }
        catch { return null; }
    }

    private static OnlineSong? FromBoardItem(JsonElement item)
    {
        try
        {
            string Get(string k) => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var songmid = Get("id");
            if (string.IsNullOrEmpty(songmid)) return null;
            _ = int.TryParse(Get("duration"), out var dur);
            OnlineSong s = new()
            {
                Id = $"kw:{songmid}",
                Platform = "lx",
                PlatformName = "酷我",
                Title = DecodeName(Get("name")),
                Artist = FormatSinger(DecodeName(Get("artist"))),
                Album = DecodeName(Get("album")),
                DurationMs = dur * 1000L,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "kw",
                    ["RawId"] = songmid,
                },
            };
            // 榜单接口直接带封面（小图，替换成大图尺寸）
            var pic = Get("pic");
            if (!string.IsNullOrEmpty(pic)) s.CoverUrl = pic.Replace("/120/", "/500/");
            return s;
        }
        catch { return null; }
    }

    // ── lx 工具函数对齐 ──

    /// <summary>lx decodeName：HTML 实体 + %XX 解码（酷我接口返回转义名）。</summary>
    private static string DecodeName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw;
        // 酷我偶发 %XX 编码（如 %2C）——先做 URL 解码
        if (s.Contains('%')) { try { s = Uri.UnescapeDataString(s); } catch { } }
        return s
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");
    }

    /// <summary>lx formatSinger：多歌手分隔符 & → 、</summary>
    private static string FormatSinger(string raw) => raw.Replace("&", "、");
}