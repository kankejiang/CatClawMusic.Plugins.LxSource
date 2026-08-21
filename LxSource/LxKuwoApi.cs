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

/// <summary>歌单标签（酷我歌单分类里的单个可选标签）。</summary>
public class LxPlaylistTag
{
    public string Name { get; }
    public string Id { get; }
    public string? Img { get; }

    public LxPlaylistTag(string name, string id, string? img = null)
    {
        Name = name;
        Id = id;
        Img = img;
    }
}

/// <summary>歌单分类大组（酷我歌单 getTagList 顶层节点：name + data 标签数组）。</summary>
public class LxPlaylistCategory
{
    public string Name { get; }
    public List<LxPlaylistTag> Tags { get; }

    public LxPlaylistCategory(string name, List<LxPlaylistTag> tags)
    {
        Name = name;
        Tags = tags;
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

    /// <summary>榜单头信息（酷我 kbangserver 的 name/pic/info/num 字段，1 条请求就能拿封面/简介/歌曲总数）。
    /// 返回 null 表示失败。</summary>
    public static async Task<(string Name, string? Cover, string? Info, int Num)?> GetBoardHeadAsync(string bangId)
    {
        var url = "http://kbangserver.kuwo.cn/ksong.s" +
                  $"?from=pc&fmt=json&pn=0&rn=1&type=bang&data=content&id={bangId}" +
                  "&show_copyright_off=0&pcmp4=1&isbang=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var pic = root.TryGetProperty("pic", out var p) ? p.GetString() : null;
            // 优先更大尺寸的 v9_pic2，如果存在则替换 /120/ → /500/
            if (root.TryGetProperty("v9_pic2", out var vp) && vp.ValueKind == JsonValueKind.String)
            {
                var vpStr = vp.GetString();
                if (!string.IsNullOrEmpty(vpStr))
                {
                    pic = vpStr;
                    if (pic.Contains("/120/")) pic = pic.Replace("/120/", "/500/");
                }
            }
            var info = root.TryGetProperty("info", out var i) ? i.GetString() : null;
            var num = root.TryGetProperty("num", out var nm) &&
                      int.TryParse(nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "0" : nm.ToString(), out var nni)
                ? nni : 300;
            return (name, pic, info, num);
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

    // ── 歌单 / 歌单搜索 / 推荐歌单 / 歌单分类 ──
    // 全部走 wapi.kuwo.cn 公开 JSON 接口，无签名无 CSRF 校验（UA + Referer 即可）。
    // 参考 lx-music-mobile kw sdk（同 GetBoards 的照搬策略）。

    /// <summary>歌单分类（酷我 H5 getTagList）。
    /// 顶层数组里每一项是「分类大组」（如 语种/心情/场景/曲风流派），
    /// 每大组的 data 里是实际标签，标签 id 用于 <see cref="GetPlaylistsAsync(string,int,int,string)"/> 的 category 参数。
    /// <para>返回 null 表示失败。</para></summary>
    public static async Task<List<LxPlaylistCategory>?> GetPlaylistCategoriesAsync()
    {
        var url = "https://wapi.kuwo.cn/api/www/playlist/getTagList?httpsStatus=1&plat=www&https=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<LxPlaylistCategory>();
            var list = new List<LxPlaylistCategory>();
            foreach (var grp in arr.EnumerateArray())
            {
                var name = grp.TryGetProperty("name", out var gN) ? gN.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name) || !grp.TryGetProperty("data", out var subs) || subs.ValueKind != JsonValueKind.Array)
                    continue;
                var tags = new List<LxPlaylistTag>();
                foreach (var t in subs.EnumerateArray())
                {
                    var tn = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var ti = t.TryGetProperty("id", out var idv) ? idv.ToString() : "";
                    var timg = t.TryGetProperty("img", out var iv) ? iv.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(tn) || string.IsNullOrEmpty(ti)) continue;
                    tags.Add(new LxPlaylistTag(tn, ti, timg));
                }
                if (tags.Count > 0) list.Add(new LxPlaylistCategory(name, tags));
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>推荐歌单列表（酷我 PC getRcmPlayList / getTagPlayList）。
    /// <list type="bullet">
    /// <item>category 为 null 或空：走「推荐」getRcmPlayList，sort="new" / "hot"，对应图里的「最新/最热」。</item>
    /// <item>category 有值：走「分类歌单」getTagPlayList（id=标签id），sort 同上。</item>
    /// </list>
    /// 返回 null 表示失败。
    /// </summary>
    public static async Task<List<OnlinePlaylist>?> GetPlaylistsAsync(string? category = null, int page = 1, int pageSize = 12, string sort = "hot")
    {
        string url;
        if (string.IsNullOrEmpty(category))
        {
            // 推荐歌单（首页「歌单」tab 最新/最热）
            var order = string.Equals(sort, "new", StringComparison.OrdinalIgnoreCase) ? "new" : "hot";
            url = "https://wapi.kuwo.cn/api/pc/classify/playlist/getRcmPlayList" +
                  $"?pn={page}&rn={pageSize}&order={order}&httpsStatus=1&plat=www&https=1";
        }
        else
        {
            // 指定分类（标签id）的歌单列表
            url = "https://wapi.kuwo.cn/api/pc/classify/playlist/getTagPlayList" +
                  $"?loginId=&id={Uri.EscapeDataString(category)}&pn={page}&rn={pageSize}&httpsStatus=1&plat=www&https=1";
        }
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var wrapper) ||
                !wrapper.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            return arr.EnumerateArray().Select(FromPlaylistItem).Where(p => p != null).Cast<OnlinePlaylist>().ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>歌单搜索（酷我 searchPlayListBykeyWord，对应图里搜索页的「歌单」tab）。失败返回 null。</summary>
    public static async Task<List<OnlinePlaylist>?> SearchPlaylistsAsync(string keyword, int page = 1, int pageSize = 12)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<OnlinePlaylist>();
        var url = "https://wapi.kuwo.cn/api/www/search/searchPlayListBykeyWord" +
                  $"?key={Uri.EscapeDataString(keyword)}&pn={page}&rn={pageSize}&httpsStatus=1&plat=www&https=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var wrapper) ||
                !wrapper.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>(arr.GetArrayLength());
            foreach (var it in arr.EnumerateArray())
            {
                var p = FromPlaylistSearchItem(it);
                if (p != null) list.Add(p);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>歌单内歌曲（酷我 playListInfo musicList 字段 + 解析 rid → OnlineSong）。失败返回 null。
    /// <para>playlist.Id=酷我歌单pid（或榜单id），若 Id 以 kw__ 开头视为榜单走 GetBoardSongsAsync。</para></summary>
    public static async Task<List<OnlineSong>?> GetPlaylistSongsAsync(string playlistId, int page = 1, int pageSize = 50)
    {
        if (string.IsNullOrEmpty(playlistId)) return new List<OnlineSong>();
        // 榜单（id = "kw__<bangId>"，来自 GetBoards）：走 kbangserver
        if (playlistId.StartsWith("kw__", StringComparison.OrdinalIgnoreCase))
        {
            var bangId = playlistId.Substring(4);
            if (string.IsNullOrEmpty(bangId)) bangId = "16";
            return await GetBoardSongsAsync(bangId, page, pageSize).ConfigureAwait(false);
        }
        // 普通歌单
        var url = "https://wapi.kuwo.cn/api/www/playlist/playListInfo" +
                  $"?pid={Uri.EscapeDataString(playlistId)}&pn={page}&rn={pageSize}&httpsStatus=1&plat=www&https=1";
        try
        {
            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var wrapper) ||
                !wrapper.TryGetProperty("musicList", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            return arr.EnumerateArray().Select(FromPlaylistSong).Where(s => s != null).Cast<OnlineSong>().ToList();
        }
        catch
        {
            return null;
        }
    }

    // ── 解析 ──

    /// <summary>推荐 / 分类歌单（getRcmPlayList / getTagPlayList） → OnlinePlaylist</summary>
    private static OnlinePlaylist? FromPlaylistItem(JsonElement it)
    {
        try
        {
            string Get(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = Get("id");
            var name = DecodeName(Get("name"));
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            _ = int.TryParse(Get("total"), out var total);
            var img = Get("img");
            // img 默认是 500，不是则补 500 大图
            if (!string.IsNullOrEmpty(img) && (img.Contains("/120/") || img.Contains("_240.") || img.Contains("_150.")))
                img = img.Replace("/120/", "/500/").Replace("_240.", "_500.").Replace("_150.", "_500.");
            var desc = Get("desc");
            if (string.IsNullOrEmpty(desc))
            {
                var uname = Get("uname");
                var listen = Get("listencnt");
                if (!string.IsNullOrEmpty(uname)) desc = $"作者：{uname}";
                if (!string.IsNullOrEmpty(listen)) desc = string.IsNullOrEmpty(desc) ? $"播放：{listen}" : $"{desc} · 播放：{listen}";
            }
            return new OnlinePlaylist
            {
                Id = id,
                Platform = "lx",
                Name = name,
                CoverUrl = img,
                Description = desc,
                SongCount = total,
            };
        }
        catch { return null; }
    }

    /// <summary>歌单搜索结果（searchPlayListBykeyWord list 项） → OnlinePlaylist</summary>
    private static OnlinePlaylist? FromPlaylistSearchItem(JsonElement it)
    {
        try
        {
            string Get(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = Get("id");
            var name = DecodeName(Get("name"));
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            _ = int.TryParse(Get("total"), out var total);
            var img = Get("img");
            if (!string.IsNullOrEmpty(img) && (img.Contains("_240.") || img.Contains("/120/") || img.Contains("_150.")))
                img = img.Replace("_240.", "_500.").Replace("/120/", "/500/").Replace("_150.", "_500.");
            var uname = Get("uname");
            var listen = Get("listencnt");
            var desc = "";
            if (!string.IsNullOrEmpty(uname)) desc = $"作者：{uname}";
            if (!string.IsNullOrEmpty(listen)) desc = string.IsNullOrEmpty(desc) ? $"播放：{listen}" : $"{desc} · 播放：{listen}";
            return new OnlinePlaylist
            {
                Id = id,
                Platform = "lx",
                Name = name,
                CoverUrl = img,
                Description = desc,
                SongCount = total,
            };
        }
        catch { return null; }
    }

    /// <summary>歌单 playListInfo.musicList 项 → OnlineSong（同 lx kw sdk 转换逻辑）</summary>
    private static OnlineSong? FromPlaylistSong(JsonElement it)
    {
        try
        {
            string GetStr(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            // rid 可能是 number 或 string，统一字符串化
            var songmid = it.TryGetProperty("rid", out var rid) ? rid.ToString() : "";
            if (string.IsNullOrEmpty(songmid)) return null;
            var durSec = it.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number && dur.TryGetInt32(out var ds) ? ds : 0;
            var pic = GetStr("pic");
            if (!string.IsNullOrEmpty(pic) && pic.Contains("/120/")) pic = pic.Replace("/120/", "/500/");
            return new OnlineSong
            {
                Id = $"kw:{songmid}",
                Platform = "lx",
                PlatformName = "酷我",
                Title = DecodeName(GetStr("name")),
                Artist = FormatSinger(DecodeName(GetStr("artist"))),
                Album = DecodeName(GetStr("album")),
                DurationMs = durSec * 1000L,
                CoverUrl = string.IsNullOrEmpty(pic) ? null : pic,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "kw",
                    ["RawId"] = songmid,
                },
            };
        }
        catch { return null; }
    }

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