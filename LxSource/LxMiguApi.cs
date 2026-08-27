using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>咪咕(mg)歌单分类里的一个标签。</summary>
public class LxMiguTag
{
    public string Name { get; }
    public string Id { get; }

    public LxMiguTag(string name, string id)
    {
        Name = name;
        Id = id;
    }
}

/// <summary>咪咕歌单分类大组（header.title + content 标签数组；另有 hotTag 热门标签）。</summary>
public class LxMiguTagGroup
{
    public string Name { get; }
    public List<LxMiguTag> Tags { get; }

    public LxMiguTagGroup(string name, List<LxMiguTag> tags)
    {
        Name = name;
        Tags = tags;
    }
}

/// <summary>
/// 咪咕(mg)内置源 API —— 照搬 lx-music-mobile（lyswhut/lx-music-mobile）的
/// src/utils/musicSdk/mg 实现：内容数据（搜索/排行榜/歌单/歌词/封面）直连咪咕公开 API。
/// <para>与酷我源架构一致：播放直链不由本类提供，统一交由自定义源脚本（musicUrl action）解析。</para>
/// <para>签名接口：搜索/歌单搜索走 jadeite.migu.cn v3，需 createSignature（MD5 拼接）与 header 头。</para>
/// </summary>
public static class LxMiguApi
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static LxMiguApi()
    {
        // 咪咕接口对 UA / Referer / channel 敏感，统一注入默认头
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) " +
            "AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("channel", "0146921");
    }

    // ── 常量（照搬 lx mg sdk）──
    private const string DeviceId = "963B7AA0D21511ED807EE5846EC87D20";
    private const string SignatureMd5 = "6cdc72a439cef99a3418d2a78aa28c73";
    private const string SignatureTail = "yyapp2d16148780a1dcc7408e06336b98cfd50";
    private const string SuccessCode = "000000";

    /// <summary>榜单列表（照搬 lx mg leaderboard.boardList 硬编码 10 个）。</summary>
    public static List<LxBoardItem> GetBoards() => new()
    {
        new("mg__27553319", "新歌榜", "27553319"),
        new("mg__27186466", "热歌榜", "27186466"),
        new("mg__27553408", "原创榜", "27553408"),
        new("mg__75959118", "音乐风向榜", "75959118"),
        new("mg__76557036", "彩铃分贝榜", "76557036"),
        new("mg__76557745", "会员臻爱榜", "76557745"),
        new("mg__23189800", "港台榜", "23189800"),
        new("mg__23189399", "内地榜", "23189399"),
        new("mg__19190036", "欧美榜", "19190036"),
        new("mg__83176390", "国风金曲榜", "83176390"),
    };

    /// <summary>搜索歌曲（咪咕 jadeite v3，带 MD5 签名）。返回 null 表示失败。</summary>
    public static async Task<List<OnlineSong>?> SearchAsync(string keyword, int page = 1, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<OnlineSong>();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = CreateSignature(keyword, time);
        var searchSwitch = Uri.EscapeDataString(
            "{\"song\":1,\"album\":0,\"singer\":0,\"tagSong\":1,\"mvSong\":0,\"bestShow\":1,\"songlist\":0,\"lyricSong\":0}");
        var url = "https://jadeite.migu.cn/music_search/v3/search/searchAll" +
                  $"?isCorrect=0&isCopyright=1&searchSwitch={searchSwitch}&pageSize={limit}" +
                  $"&text={Uri.EscapeDataString(keyword)}&pageNo={page}&sort=0&sid=USS";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("uiVersion", "A_music_3.6.1");
            req.Headers.TryAddWithoutValidation("deviceId", sign.DeviceId);
            req.Headers.TryAddWithoutValidation("timestamp", time);
            req.Headers.TryAddWithoutValidation("sign", sign.Sign);
            var raw = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await raw.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode)
                return new List<OnlineSong>();
            if (!root.TryGetProperty("songResultData", out var sd) ||
                !sd.TryGetProperty("resultList", out var rl) || rl.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            var list = new List<OnlineSong>();
            // resultList 是「数组的数组」，需逐层拍平
            foreach (var sub in rl.EnumerateArray())
            {
                if (sub.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in sub.EnumerateArray())
                {
                    var s = FromSearchItem(it);
                    if (s != null) list.Add(s);
                }
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取榜单歌曲（咪咕 querycontentbyId，老列结构 columnInfo.contents）。失败返回 null。</summary>
    public static async Task<List<OnlineSong>?> GetBoardSongsAsync(string bangId, int limit = 200)
    {
        var url = $"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/querycontentbyId.do?columnId={bangId}&needAll=0";
        try
        {
            var raw = await GetWithMobileHeaders(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode) return new List<OnlineSong>();
            // columnInfo.contents[].objectInfo
            if (!root.TryGetProperty("columnInfo", out var ci) ||
                !ci.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            var list = new List<OnlineSong>();
            foreach (var c in contents.EnumerateArray())
            {
                if (!c.TryGetProperty("objectInfo", out var obj)) continue;
                var s = FromResourceItem(obj);
                if (s != null) list.Add(s);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取歌曲封面（咪咕 getSongPic，返回纯文本 URL）。songId 为咪咕真实 songId，失败返回 null。</summary>
    public static async Task<string?> GetPicAsync(string songId)
    {
        var url = $"http://music.migu.cn/v3/api/music/audioPlayer/getSongPic?songId={songId}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "http://music.migu.cn/v3/music/player/audio?from=migu");
            var res = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetString(root, "returnCode", out var rc) || rc != SuccessCode) return null;
            var pic = TryGetString(root, "largePic", out var lp) ? lp
                : TryGetString(root, "mediumPic", out var mp) ? mp
                : TryGetString(root, "smallPic", out var sp) ? sp : null;
            if (string.IsNullOrEmpty(pic)) return null;
            if (!pic.StartsWith("http", StringComparison.OrdinalIgnoreCase)) pic = "http:" + pic;
            return pic;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取歌词（优先 lrcUrl 拿整句 LRC 文本；lrcUrl 缺失时回退 resourceinfo 补齐）。
    /// 咪咕 mrc 增强歌词含专属加密，本实现沿用 lx 的简单 LRC 通道。返回 null 表示失败。</summary>
    public static async Task<string?> GetLyricAsync(OnlineSong song)
    {
        // 从歌曲内部字段解析 lrcUrl/mrcUrl（搜索、榜单、歌单已回填）
        string lrcUrl = "", mrcUrl = "";
        if (song.Internal != null)
        {
            if (song.Internal.TryGetValue("LrcUrl", out var l) && l is string ls) lrcUrl = ls;
            if (song.Internal.TryGetValue("MrcUrl", out var m) && m is string ms) mrcUrl = ms;
        }
        // lrcUrl 缺失：尝试用 copyrightId 触发 resourceinfo 补齐（资源信息含 lrcUrl）
        if (string.IsNullOrEmpty(lrcUrl))
        {
            var copyrightId = song.Internal != null && song.Internal.TryGetValue("RawId", out var rid) && rid is string rids ? rids : "";
            if (!string.IsNullOrEmpty(copyrightId))
            {
                var res = await FetchResourceAsync(copyrightId).ConfigureAwait(false);
                if (res is { LrcUrl: not null } r && !string.IsNullOrEmpty(r.LrcUrl)) lrcUrl = r.LrcUrl;
                if (string.IsNullOrEmpty(lrcUrl) && res is { MrcUrl: not null }) mrcUrl = res.MrcUrl!;
            }
        }
        if (string.IsNullOrEmpty(lrcUrl) && !string.IsNullOrEmpty(mrcUrl))
        {
            // 仅 mrc 可用时读 mrc 原始密文（lx 走 mrc 解密；此处退化返回 LRC 文本不可用时以 mrc 原文兜底）
            lrcUrl = mrcUrl;
        }
        if (string.IsNullOrEmpty(lrcUrl)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, lrcUrl);
            req.Headers.TryAddWithoutValidation("Referer", "https://app.c.nf.migu.cn/");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Linux; Android 5.1.1; Nexus 6 Build/LYZ28E) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/59.0.3071.115 Mobile Safari/537.36");
            var res = await Http.SendAsync(req).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    // ── 歌单 / 歌单分类 / 歌单搜索 ──

    /// <summary>歌单分类标签（咪咕 musiclistplaza-taglist）。第一个大类是「热门」标签（hotTag），其余为分类组。
    /// 返回 null 表示失败。</summary>
    public static async Task<List<LxMiguTagGroup>?> GetPlaylistCategoriesAsync()
    {
        var url = "https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-taglist/release";
        try
        {
            var raw = await GetWithWebHeaders(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode) return new List<LxMiguTagGroup>();
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return new List<LxMiguTagGroup>();
            var groups = new List<LxMiguTagGroup>();
            var arr = data.EnumerateArray().ToList();
            // 第一项是热门（扁平 {content:[{texts:[name,id]}]}），其余是分类组 {header:{title}, content:[...]}
            for (int i = 1; i < arr.Count; i++)
            {
                var grp = arr[i];
                var grpName = GetNested(grp, "header", "title") ?? "";
                if (string.IsNullOrEmpty(grpName) || !grp.TryGetProperty("content", out var ct) || ct.ValueKind != JsonValueKind.Array)
                    continue;
                var tags = new List<LxMiguTag>();
                foreach (var t in ct.EnumerateArray())
                {
                    var tn = GetTexts(t, 0);
                    var ti = GetTexts(t, 1);
                    if (string.IsNullOrEmpty(tn) || string.IsNullOrEmpty(ti)) continue;
                    tags.Add(new LxMiguTag(tn, ti));
                }
                if (tags.Count > 0) groups.Add(new LxMiguTagGroup(grpName, tags));
            }
            // 热门标签并入作为第一组
            var hot = new List<LxMiguTag>();
            if (arr.Count > 0 && arr[0].TryGetProperty("content", out var hct) && hct.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in hct.EnumerateArray())
                {
                    var tn = GetTexts(t, 0);
                    var ti = GetTexts(t, 1);
                    if (!string.IsNullOrEmpty(tn) && !string.IsNullOrEmpty(ti)) hot.Add(new LxMiguTag(tn, ti));
                }
            }
            if (hot.Count > 0) groups.Insert(0, new LxMiguTagGroup("热门", hot));
            return groups;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>推荐/分类歌单列表。category 为空走「推荐」（playlist-square-recommend），有值走「分类」（listbytag）。
    /// 返回 null 表示失败。</summary>
    public static async Task<List<OnlinePlaylist>?> GetPlaylistsAsync(string? category = null, int page = 1)
    {
        string url;
        if (string.IsNullOrEmpty(category))
            url = $"https://app.c.nf.migu.cn/pc/bmw/page-data/playlist-square-recommend/v1.0?templateVersion=2&pageNo={page}";
        else
            url = $"https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-listbytag/release" +
                  $"?pageNumber={page}&templateVersion=2&tagId={Uri.EscapeDataString(category)}";
        try
        {
            var raw = await GetWithWebHeaders(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode) return new List<OnlinePlaylist>();
            if (!root.TryGetProperty("data", out var data)) return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            if (data.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                CollectListItems(contents, list);
            else if (data.TryGetProperty("contentItemList", out var cil) && cil.ValueKind == JsonValueKind.Array)
            {
                foreach (var grp in cil.EnumerateArray())
                {
                    if (grp.TryGetProperty("itemList", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in items.EnumerateArray())
                        {
                            var p = FromSquareItem(it);
                            if (p != null) list.Add(p);
                        }
                    }
                }
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>歌单搜索（咪咕 jadeite v3，songlist=1）。失败返回 null。</summary>
    public static async Task<List<OnlinePlaylist>?> SearchPlaylistsAsync(string keyword, int page = 1, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<OnlinePlaylist>();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = CreateSignature(keyword, time);
        var searchSwitch = Uri.EscapeDataString(
            "{\"song\":0,\"album\":0,\"singer\":0,\"tagSong\":0,\"mvSong\":0,\"bestShow\":0,\"songlist\":1,\"lyricSong\":0}");
        var url = "https://jadeite.migu.cn/music_search/v3/search/searchAll" +
                  $"?isCorrect=1&isCopyright=1&searchSwitch={searchSwitch}&pageSize={limit}" +
                  $"&text={Uri.EscapeDataString(keyword)}&pageNo={page}&sort=0&sid=USS";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("uiVersion", "A_music_3.6.1");
            req.Headers.TryAddWithoutValidation("deviceId", sign.DeviceId);
            req.Headers.TryAddWithoutValidation("timestamp", time);
            req.Headers.TryAddWithoutValidation("sign", sign.Sign);
            var raw = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await raw.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("songListResultData", out var sd) ||
                !sd.TryGetProperty("result", out var rr) || rr.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            foreach (var it in rr.EnumerateArray())
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

    /// <summary>歌单详情信息（咪咕 resource/playlist/v2.0）。返回 null 表示失败。</summary>
    public static async Task<(string Name, string? Cover, string? Desc, string? Author)?> GetPlaylistInfoAsync(string playlistId)
    {
        var url = $"https://c.musicapp.migu.cn/MIGUM3.0/resource/playlist/v2.0?playlistId={playlistId}";
        try
        {
            var raw = await GetWithWebHeaders(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode || !root.TryGetProperty("data", out var d))
                return null;
            var name = GetName(d, "title") ?? "";
            string? img = null;
            if (d.TryGetProperty("imgItem", out var im) && im.TryGetProperty("img", out var iv) && iv.ValueKind == JsonValueKind.String)
            {
                var s = iv.GetString();
                if (!string.IsNullOrEmpty(s)) img = s;
            }
            var desc = GetName(d, "summary");
            var author = GetName(d, "ownerName");
            return (name, img, desc, author);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>歌单内歌曲（咪咕 resource/playlist/song/v2.0）。playlist.Id 若以 mg__ 开头视为榜单走 GetBoardSongsAsync。
    /// 失败返回 null。</summary>
    public static async Task<List<OnlineSong>?> GetPlaylistSongsAsync(string playlistId, int page = 1, int pageSize = 30)
    {
        if (string.IsNullOrEmpty(playlistId)) return new List<OnlineSong>();
        if (playlistId.StartsWith("mg__", StringComparison.OrdinalIgnoreCase))
        {
            var bangId = playlistId.Substring(4);
            if (string.IsNullOrEmpty(bangId)) bangId = "27186466";
            return await GetBoardSongsAsync(bangId, pageSize).ConfigureAwait(false);
        }
        if (playlistId.StartsWith("mg:", StringComparison.OrdinalIgnoreCase))
            playlistId = playlistId.Substring(3);
        var url = "https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0" +
                  $"?pageNo={page}&pageSize={pageSize}&playlistId={Uri.EscapeDataString(playlistId)}";
        try
        {
            var raw = await GetWithWebHeaders(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!TryGetString(root, "code", out var code) || code != SuccessCode ||
                !root.TryGetProperty("data", out var d) || !d.TryGetProperty("songList", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            var list = new List<OnlineSong>();
            foreach (var it in arr.EnumerateArray())
            {
                var s = FromV5Item(it);
                if (s != null) list.Add(s);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>补齐歌曲资源信息（resourceinfo.do）。musicInfo 老格式，含 lrcUrl/mrcUrl/真实 songId。失败返回 null。</summary>
    public static async Task<LxMiguResourceInfo?> FetchResourceAsync(string copyrightId)
    {
        var url = "https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?resourceType=2";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["resourceId"] = copyrightId }),
            };
            var res = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("resource", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return null;
            var r = FromResourceInfoRecord(arr[0]);
            return r;
        }
        catch
        {
            return null;
        }
    }

    // ── 解析 ──

    /// <summary>搜索条目 → OnlineSong（字段源：filterData，字段 songId/copyrightId/name/album/singerList/duration/img1..3）。</summary>
    private static OnlineSong? FromSearchItem(JsonElement it)
    {
        try
        {
            var copyrightId = GetName(it, "copyrightId") ?? "";
            var songId = GetName(it, "songId") ?? "";
            if (string.IsNullOrEmpty(songId) || string.IsNullOrEmpty(copyrightId)) return null;
            var img = PickImage(GetName(it, "img1"), GetName(it, "img2"), GetName(it, "img3"));
            var dur = ParseDuration(GetLong(it, "duration"));
            return new OnlineSong
            {
                Id = $"mg:{copyrightId}",
                Platform = "lx",
                PlatformName = "咪咕",
                Title = GetName(it, "name") ?? "",
                Artist = FormatSingerArr(it.TryGetProperty("singerList", out var sl) && sl.ValueKind == JsonValueKind.Array ? sl : default),
                Album = GetName(it, "album") ?? "",
                DurationMs = dur,
                CoverUrl = img,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "mg",
                    ["RawId"] = copyrightId,
                    ["ResId"] = songId,
                    ["LrcUrl"] = GetName(it, "lrcUrl") ?? "",
                    ["MrcUrl"] = GetName(it, "mrcurl") ?? "",
                },
            };
        }
        catch { return null; }
    }

    /// <summary>resourceinfo 记录 → LxMiguResourceInfo（老格式字段：songName/artists/album/albumImgs/lrcUrl/mrcUrl）。</summary>
    private static LxMiguResourceInfo? FromResourceInfoRecord(JsonElement it)
    {
        try
        {
            var info = new LxMiguResourceInfo
            {
                SongId = GetName(it, "songId") ?? "",
                CopyrightId = GetName(it, "copyrightId") ?? "",
                LrcUrl = GetName(it, "lrcUrl"),
                MrcUrl = GetName(it, "mrcUrl"),
            };
            if (string.IsNullOrEmpty(info.SongId) && string.IsNullOrEmpty(info.CopyrightId)) return null;
            return info;
        }
        catch { return null; }
    }

    /// <summary>老格式榜单项（querycontentbyId / resourceinfo）→ OnlineSong。字段源 filterMusicInfoList。</summary>
    private static OnlineSong? FromResourceItem(JsonElement it)
    {
        try
        {
            var songId = GetName(it, "songId") ?? "";
            var copyrightId = GetName(it, "copyrightId") ?? "";
            var raw = string.IsNullOrEmpty(copyrightId) ? songId : copyrightId;
            if (string.IsNullOrEmpty(raw)) return null;
            string? img = null;
            if (it.TryGetProperty("albumImgs", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
            {
                var first = imgs[0];
                var i = first.TryGetProperty("img", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : null;
                if (!string.IsNullOrEmpty(i)) img = i;
            }
            return new OnlineSong
            {
                Id = $"mg:{raw}",
                Platform = "lx",
                PlatformName = "咪咕",
                Title = GetName(it, "songName") ?? "",
                Artist = FormatSingerArr(it.TryGetProperty("artists", out var at) && at.ValueKind == JsonValueKind.Array ? at : default),
                Album = GetName(it, "album") ?? "",
                DurationMs = ParseDurationFromString(GetName(it, "length")),
                CoverUrl = img,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "mg",
                    ["RawId"] = raw,
                    ["ResId"] = songId,
                    ["LrcUrl"] = GetName(it, "lrcUrl") ?? "",
                    ["MrcUrl"] = GetName(it, "mrcUrl") ?? "",
                },
            };
        }
        catch { return null; }
    }

    /// <summary>新格式条目（listbytag / playlist song）→ OnlineSong。字段源 filterMusicInfoListV5。</summary>
    private static OnlineSong? FromV5Item(JsonElement it)
    {
        try
        {
            var songId = GetName(it, "songId") ?? "";
            var copyrightId = GetName(it, "copyrightId") ?? "";
            var raw = string.IsNullOrEmpty(copyrightId) ? songId : copyrightId;
            if (string.IsNullOrEmpty(raw)) return null;
            var img = PickImage(GetName(it, "img3"), GetName(it, "img2"), GetName(it, "img1"));
            return new OnlineSong
            {
                Id = $"mg:{raw}",
                Platform = "lx",
                PlatformName = "咪咕",
                Title = GetName(it, "songName") ?? "",
                Artist = FormatSingerArr(it.TryGetProperty("singerList", out var sl) && sl.ValueKind == JsonValueKind.Array ? sl : default),
                Album = GetName(it, "album") ?? "",
                DurationMs = ParseDuration(GetLong(it, "duration")),
                CoverUrl = img,
                Internal = new Dictionary<string, object>
                {
                    ["Source"] = "mg",
                    ["RawId"] = raw,
                    ["ResId"] = songId,
                    ["LrcUrl"] = GetName(it, "lrcUrl") ?? "",
                    ["MrcUrl"] = GetName(it, "mrcUrl") ?? "",
                },
            };
        }
        catch { return null; }
    }

    /// <summary>歌单广场方形条目（contentItemList[].itemList，filterList） → OnlinePlaylist。</summary>
    private static OnlinePlaylist? FromSquareItem(JsonElement it)
    {
        try
        {
            var id = GetNested(it, "logEvent", "contentId") ?? (GetName(it, "contentId") ?? "");
            var name = GetName(it, "title") ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            var img = GetName(it, "imageUrl");
            var count = it.TryGetProperty("barList", out var bars) && bars.ValueKind == JsonValueKind.Array && bars.GetArrayLength() > 0
                ? GetName(bars[0], "title") : null;
            return new OnlinePlaylist
            {
                Id = "mg:" + id,
                Platform = "lx",
                Name = name,
                CoverUrl = img,
                Description = count,
            };
        }
        catch { return null; }
    }

    /// <summary>歌单搜索结果（songListResultData.result，filterSongListResult） → OnlinePlaylist。</summary>
    private static OnlinePlaylist? FromPlaylistSearchItem(JsonElement it)
    {
        try
        {
            var id = GetName(it, "id") ?? "";
            var name = GetName(it, "name") ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            _ = int.TryParse(GetName(it, "musicNum"), out var total);
            var author = GetName(it, "userName");
            var desc = string.IsNullOrEmpty(author) ? null : $"作者：{author}";
            return new OnlinePlaylist
            {
                Id = "mg:" + id,
                Platform = "lx",
                Name = name,
                CoverUrl = GetName(it, "musicListPicUrl"),
                Description = desc,
                SongCount = total,
            };
        }
        catch { return null; }
    }

    /// <summary>递归收集 contents 里的歌单节点（resType=='2021'，filterList2）。</summary>
    private static void CollectListItems(JsonElement contents, List<OnlinePlaylist> list)
    {
        if (contents.ValueKind != JsonValueKind.Array) return;
        foreach (var item in contents.EnumerateArray())
        {
            if (item.TryGetProperty("contents", out var sub) && sub.ValueKind == JsonValueKind.Array)
                CollectListItems(sub, list);
            else if (TryGetString(item, "resType", out var rt) && rt == "2021")
            {
                var id = item.TryGetProperty("resId", out var rid) ? rid.ToString() : "";
                var name = GetName(item, "txt") ?? "";
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                list.Add(new OnlinePlaylist
                {
                    Id = "mg:" + id,
                    Platform = "lx",
                    Name = name,
                    CoverUrl = GetName(item, "img"),
                    Description = GetName(item, "txt2"),
                });
            }
        }
    }

    // ── lx 工具函数对齐 ──

    /// <summary>lx createSignature：sign=md5(text+signatureMd5+tail+deviceId+time)。</summary>
    private static (string Sign, string DeviceId) CreateSignature(string text, string time)
    {
        var raw = $"{text}{SignatureMd5}{SignatureTail}{DeviceId}{time}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return (hash, DeviceId);
    }

    private static async Task<string> GetWithMobileHeaders(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://app.c.nf.migu.cn/");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 5.1.1; Nexus 6 Build/LYZ28E) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/59.0.3071.115 Mobile Safari/537.36");
        var res = await Http.SendAsync(req).ConfigureAwait(false);
        return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static async Task<string> GetWithWebHeaders(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1");
        req.Headers.TryAddWithoutValidation("Referer", "https://m.music.migu.cn/");
        var res = await Http.SendAsync(req).ConfigureAwait(false);
        return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static string? PickImage(string? img3, string? img2, string? img1)
    {
        var img = img3 ?? img2 ?? img1;
        if (string.IsNullOrEmpty(img)) return null;
        if (!img.StartsWith("http", StringComparison.OrdinalIgnoreCase)) img = "http://d.musicapp.migu.cn" + img;
        return img;
    }

    private static string FormatSingerArr(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return "";
        var sb = new List<string>();
        foreach (var s in arr.EnumerateArray())
        {
            if (s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                sb.Add(n.GetString()!);
        }
        return string.Join("、", sb);
    }

    private static long ParseDuration(long ms) => ms > 0 ? ms : 0;

    private static long ParseDurationFromString(string? len)
    {
        // "mm:ss" → ms；咪咕老列 length 形如 "03:45"
        if (string.IsNullOrEmpty(len)) return 0;
        var parts = len.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
            return (m * 60L + s) * 1000L;
        return 0;
    }

    private static string? GetName(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long GetLong(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0) : 0;

    private static bool TryGetString(JsonElement e, string key, out string value)
    {
        value = "";
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return true;
        }
        return false;
    }

    private static string? GetNested(JsonElement e, string outer, string inner)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(outer, out var o)) return null;
        return GetName(o, inner);
    }

    private static string? GetTexts(JsonElement e, int index)
    {
        if (e.TryGetProperty("texts", out var t) && t.ValueKind == JsonValueKind.Array && index < t.GetArrayLength())
            return GetName(t[index], "str") ?? (t[index].ValueKind == JsonValueKind.String ? t[index].GetString() : null);
        return null;
    }
}

/// <summary>咪咕 resourceinfo 补齐后的资源信息（含真实 songId 与歌词地址）。</summary>
public class LxMiguResourceInfo
{
    public string SongId { get; set; } = "";
    public string CopyrightId { get; set; } = "";
    public string? LrcUrl { get; set; }
    public string? MrcUrl { get; set; }
}