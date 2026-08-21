using System.Text;
using System.Text.Json;

namespace CatClawMusic.Plugins.LxSource;

/// <summary>
/// lx-music-api-server 单曲（/search 返回项）。
/// 协议参考 https://github.com/lyswhut/lx-music-api-server
/// </summary>
public class LxSong
{
    /// <summary>歌曲 ID</summary>
    public string Id { get; set; } = "";

    /// <summary>歌名</summary>
    public string Name { get; set; } = "";

    /// <summary>歌手</summary>
    public string Artist { get; set; } = "";

    /// <summary>专辑</summary>
    public string Album { get; set; } = "";

    /// <summary>封面 ID（/pic 用）</summary>
    public string PicId { get; set; } = "";

    /// <summary>播放/歌词 ID（/songurl /lyric 用）</summary>
    public string UrlId { get; set; } = "";

    /// <summary>时长（秒）</summary>
    public long IntervalSeconds { get; set; }

    /// <summary>音源标识（netease/qq/kuwo/kugou/migu/bilibili…）</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// lx-music-api-server HTTP 客户端（纯 GET + JSON，匿名访问）。
/// 端点：/ping /search /songurl /pic /lyric；响应统一为 {code:0, data:...}。
/// </summary>
public class LxApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private string _baseUrl = "";

    /// <summary>是否已配置服务器地址</summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(_baseUrl);

    /// <summary>当前服务器地址</summary>
    public string ServerUrl => _baseUrl;

    /// <summary>设置服务器地址（保存前临时测试也会调用）</summary>
    public void SetServerUrl(string url) => _baseUrl = (url ?? "").Trim().TrimEnd('/');

    // ── 端点 ──

    /// <summary>连接测试（/ping，任意 2xx 视为可达）</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!HasServer) return false;
        try
        {
            using var resp = await Http.GetAsync(BuildUrl("/ping"), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>搜索歌曲（type=song）；失败返回 null，无结果返回空列表</summary>
    public async Task<List<LxSong>?> SearchAsync(string keyword, int page, int pageSize, string? source, CancellationToken ct = default)
    {
        if (!HasServer || string.IsNullOrWhiteSpace(keyword)) return null;
        var qs = new List<(string K, string V)>
        {
            ("name", keyword),
            ("page", Math.Max(1, page).ToString()),
            ("limit", Math.Clamp(pageSize, 1, 100).ToString()),
            ("type", "song"),
        };
        if (!string.IsNullOrWhiteSpace(source)) qs.Add(("source", source));
        try
        {
            var doc = await GetJsonAsync(BuildUrl("/search", qs), ct).ConfigureAwait(false);
            var data = EnvelopeData(doc);
            if (data == null || !data.Value.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var songs = new List<LxSong>(Math.Clamp(pageSize, 1, 100));
            foreach (var item in list.EnumerateArray())
                songs.Add(ParseSong(item));
            return songs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取播放直链（br: 128 / 320 / flac / m4a）；失败返回 null</summary>
    public async Task<string?> GetSongUrlAsync(LxSong song, string br, CancellationToken ct = default)
    {
        if (!HasServer || song == null) return null;
        var qs = CommonQuery(song);
        qs.Add(("br", br));
        // 播放/歌词优先用搜索结果的 url_id
        ReplaceIdWith(qs, song.UrlId);
        try
        {
            var doc = await GetJsonAsync(BuildUrl("/songurl", qs), ct).ConfigureAwait(false);
            var data = EnvelopeData(doc);
            if (data == null || !data.Value.TryGetProperty("url", out var url)) return null;
            return url.ValueKind switch
            {
                JsonValueKind.String => Clean(url.GetString()),
                // FLAC 分段直链（url 为 {url,size} 数组）：取第一段（分段拼接需播放器支持，MVP 取首段）
                JsonValueKind.Array => FirstSegmentUrl(url),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取封面直链；失败返回 null</summary>
    public async Task<string?> GetPicUrlAsync(LxSong song, int size = 300, CancellationToken ct = default)
    {
        if (!HasServer || song == null) return null;
        var qs = CommonQuery(song);
        qs.Add(("size", Math.Max(1, size).ToString()));
        // 封面优先用搜索结果的 pic_id
        ReplaceIdWith(qs, song.PicId);
        try
        {
            var doc = await GetJsonAsync(BuildUrl("/pic", qs), ct).ConfigureAwait(false);
            var data = EnvelopeData(doc);
            if (data == null || !data.Value.TryGetProperty("url", out var url)) return null;
            return url.ValueKind switch
            {
                JsonValueKind.String => Clean(url.GetString()),
                // 个别 fork 的 /pic 与 /songurl 一样返回分段数组，取首段
                JsonValueKind.Array => FirstSegmentUrl(url),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取歌词（lrc/tlyric/rlyric 三流；无歌词返回 null）</summary>
    public async Task<(string? Lrc, string? TLrc, string? RLrc)?> GetLyricsAsync(LxSong song, CancellationToken ct = default)
    {
        if (!HasServer || song == null) return null;
        var qs = CommonQuery(song);
        qs.Add(("time", Math.Max(1, song.IntervalSeconds).ToString()));
        ReplaceIdWith(qs, song.UrlId);
        try
        {
            var doc = await GetJsonAsync(BuildUrl("/lyric", qs), ct).ConfigureAwait(false);
            var data = EnvelopeData(doc);
            if (data == null) return null;
            string? Get(string key) => data.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? Clean(v.GetString())
                : null;
            return (Get("lrc"), Get("tlyric"), Get("rlyric"));
        }
        catch
        {
            return null;
        }
    }

    // ── 内部 ──

    private List<(string K, string V)> CommonQuery(LxSong song) => new()
    {
        ("name", song.Name ?? ""),
        ("artist", song.Artist ?? ""),
        ("album", song.Album ?? ""),
        ("id", song.Id ?? ""),
        ("source", song.Source ?? ""),
    };

    /// <summary>把 id 参数替换为优先值（url_id / pic_id），空值保持原 id</summary>
    private static void ReplaceIdWith(List<(string K, string V)> qs, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return;
        for (int i = 0; i < qs.Count; i++)
            if (qs[i].K == "id")
            {
                qs[i] = ("id", preferred);
                return;
            }
    }

    private string BuildUrl(string path) => BuildUrl(path, new List<(string K, string V)>());

    private string BuildUrl(string path, List<(string K, string V)> qs)
    {
        var sb = new StringBuilder(_baseUrl).Append(path);
        if (qs.Count > 0)
        {
            sb.Append('?');
            for (int i = 0; i < qs.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(qs[i].K)).Append('=').Append(Uri.EscapeDataString(qs[i].V ?? ""));
            }
        }
        return sb.ToString();
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    /// <summary>取响应 data 字段（code 缺失或 code==0 时有效）</summary>
    private static JsonElement? EnvelopeData(JsonDocument? doc)
    {
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var c) && c != 0)
            return null;
        return data;
    }

    private static LxSong ParseSong(JsonElement item)
    {
        var s = new LxSong();
        string Get(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
        s.Id = Get("id");
        s.Name = Get("name");
        s.Artist = Get("artist");
        s.Album = Get("album");
        s.PicId = Get("pic_id");
        s.UrlId = Get("url_id");
        s.Source = Get("source");
        // interval：秒数（number，含小数）或 "mm:ss" / 数字字符串
        if (item.TryGetProperty("interval", out var iv))
        {
            if (iv.ValueKind == JsonValueKind.Number)
            {
                if (iv.TryGetInt64(out var sec)) s.IntervalSeconds = Math.Max(0, sec);
                else if (iv.TryGetDouble(out var d)) s.IntervalSeconds = (long)Math.Max(0, d);
            }
            else if (iv.ValueKind == JsonValueKind.String)
            {
                var str = iv.GetString() ?? "";
                var colon = str.IndexOf(':');
                if (colon > 0
                    && int.TryParse(str[..colon], out var m)
                    && int.TryParse(str[(colon + 1)..], out var ss))
                    s.IntervalSeconds = m * 60L + ss;
                else if (long.TryParse(str, out var n))
                    s.IntervalSeconds = Math.Max(0, n);
            }
        }
        return s;
    }

    private static string? FirstSegmentUrl(JsonElement arr)
    {
        foreach (var seg in arr.EnumerateArray())
        {
            if (seg.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                var s = Clean(u.GetString());
                if (s != null) return s;
            }
        }
        return null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
