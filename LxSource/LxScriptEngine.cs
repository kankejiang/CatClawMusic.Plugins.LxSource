using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace CatClawMusic.Plugins.LxSource;

// ──────────────────────────────────────────────────────────────────────────
//  Jint 引擎加载器：从嵌入资源加载 Jint.dll / Acornima.dll
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// 把嵌入资源的 Jint.dll / Acornima.dll 在首次需要时通过 AppDomain.AssemblyResolve
/// 加载进 AppDomain。插件 .ccp 是单 DLL、宿主不提供 Jint，故必须自加载。
/// <para>契约：插件类型不得继承/字段签名引用 Jint 类型——只能在方法体内引用，
/// 否则宿主 GetTypes() 阶段 JIT 解析类型时会早于本加载器注册而失败。</para>
/// </summary>
public static class LxScriptEngineLoader
{
    private static readonly Dictionary<string, string> ResByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jint"] = "Jint.dll",
        ["Acornima"] = "Acornima.dll",
    };

    private static int _registered;

    /// <summary>注册 AssemblyResolve 处理器（幂等）。由 ModuleInitializer 调用。</summary>
    [ModuleInitializer]
    internal static void Register()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name ?? "";
        if (!ResByName.TryGetValue(name, out var resName)) return null;
        var asm = typeof(LxScriptEngineLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream == null) return null;
        using var ms = new MemoryStream((int)stream.Length);
        stream.CopyTo(ms);
        return Assembly.Load(ms.ToArray());
    }
}

// ──────────────────────────────────────────────────────────────────────────
//  平台码映射：lx 短码（wy/kw/kg/tx/mg）↔ lx-music-api-server 全名（netease/...）
// ──────────────────────────────────────────────────────────────────────────

/// <summary>lx 自定义源短码与 lx-music-api-server 全名互转（kw=酷我/wy=网易 等）。</summary>
public static class LxPlatformCodes
{
    /// <summary>全名 → lx 短码（netease→wy, qq→tx, kuwo→kw, kugou→kg, migu→mg）</summary>
    public static readonly Dictionary<string, string> FullToShort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["netease"] = "wy",
        ["qq"] = "tx",
        ["kuwo"] = "kw",
        ["kugou"] = "kg",
        ["migu"] = "mg",
    };

    /// <summary>lx 短码 → 全名</summary>
    public static readonly Dictionary<string, string> ShortToFull = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wy"] = "netease",
        ["tx"] = "qq",
        ["kw"] = "kuwo",
        ["kg"] = "kugou",
        ["mg"] = "migu",
    };

    public static string ToShort(string full) =>
        FullToShort.TryGetValue(full ?? "", out var s) ? s : (full ?? "");

    public static string ToFull(string short_) =>
        ShortToFull.TryGetValue(short_ ?? "", out var f) ? f : (short_ ?? "");
}

// ──────────────────────────────────────────────────────────────────────────
//  脚本声明的源信息（inited 返回的 sources）
// ──────────────────────────────────────────────────────────────────────────

/// <summary>脚本通过 send(inited, {sources:{ wy:{ actions:['musicUrl'], qualitys:[...] }, ... }}) 声明的源能力。</summary>
public class LxScriptSources
{
    /// <summary>key=源短码（wy/kw...），value=该源支持的 action 列表</summary>
    public Dictionary<string, HashSet<string>> ActionsBySource { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>key=源短码，value=该源声明的音质列表（128k/320k/flac/flac24bit），未声明时为空集合</summary>
    public Dictionary<string, HashSet<string>> QualitysBySource { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>源短码集合</summary>
    public List<string> SourceCodes => ActionsBySource.Keys.ToList();

    public bool Supports(string sourceCode, string action) =>
        ActionsBySource.TryGetValue(sourceCode ?? "", out var set) && set.Contains(action);

    /// <summary>取该源声明的音质列表（未声明返回空集合）。</summary>
    public IReadOnlySet<string> Qualitys(string sourceCode) =>
        QualitysBySource.TryGetValue(sourceCode ?? "", out var qs) ? qs : new HashSet<string>();
}

// ──────────────────────────────────────────────────────────────────────────
//  JS 桥（globalThis.lx）：EVENT_NAMES / env / version / on / send / request / utils
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// lx-music 自定义源 SDK 的 CLR 桥。Jint 的 ObjectWrapper 自动把属性/方法暴露给 JS，
/// 脚本 `const { EVENT_NAMES, request, on, send, utils, env, version } = globalThis.lx` 解构即用。
/// </summary>
public class LxBridge
{
    private readonly Engine _engine;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public readonly Dictionary<string, string> EVENT_NAMES = new(StringComparer.Ordinal)
    {
        ["request"] = "request",
        ["inited"] = "inited",
        ["updateAlert"] = "updateAlert",
    };

    public string env => "desktop";
    public string version => "2.0.0";

    /// <summary>当前脚本信息（lx 协议字段；脚本头部注释解析暂由宿主外部填充，
    /// 这里给占位对象避免脚本访问 undefined 抛错，如 currentScriptInfo.version）。</summary>
    public IDictionary<string, string> currentScriptInfo { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["name"] = "",
        ["description"] = "",
        ["version"] = "",
        ["author"] = "",
        ["homepage"] = "",
        ["rawScript"] = "",
    };

    /// <summary>on(name, handler) 注册的处理器（key=event name, value=JS handler）</summary>
    public Dictionary<string, JsValue> Handlers { get; } = new(StringComparer.Ordinal);

    /// <summary>send(inited, data) 时捕获的源声明</summary>
    public LxScriptSources? Sources { get; private set; }

    /// <summary>脚本是否已 inited</summary>
    public bool Inited { get; private set; }

    /// <summary>send(updateAlert, data) 时捕获的更新提示（脚本检测到新版会发此事件且不发 inited）</summary>
    public string? UpdateMessage { get; private set; }
    public string? UpdateVersion { get; private set; }
    public string? UpdateUrl { get; private set; }
    public bool HasUpdateAlert => UpdateMessage != null;

    /// <summary>request 失败时的最后错误（调试用）</summary>
    public string? LastError { get; private set; }

    public LxBridge(Engine engine) { _engine = engine; }

    public void on(string name, JsValue handler)
    {
        if (!string.IsNullOrEmpty(name) && handler.IsObject())
            Handlers[name] = handler;
    }

    public void send(string name, JsValue data)
    {
        if (name == "inited")
        {
            Inited = true;
            Sources = ParseSources(data);
        }
        else if (name == "updateAlert")
        {
            // 脚本检测到新版：{version, message, updateUrl/downloadUrl}，发此事件后通常不发 inited
            try
            {
                if (data.IsObject())
                {
                    var o = data.AsObject();
                    string Get(string k) => o.Get(k) is var v && v.IsString() ? v.AsString() : "";
                    UpdateMessage = Get("message") ?? Get("changeLog") ?? Get("desc");
                    UpdateVersion = Get("version") ?? Get("newVersion");
                    UpdateUrl = Get("updateUrl") ?? Get("downloadUrl") ?? Get("url");
                    if (string.IsNullOrEmpty(UpdateMessage) && !string.IsNullOrEmpty(UpdateVersion))
                        UpdateMessage = $"发现新版本 {UpdateVersion}";
                }
            }
            catch { /* 解析失败忽略 */ }
        }
    }

    private static LxScriptSources ParseSources(JsValue data)
    {
        var src = new LxScriptSources();
        try
        {
            if (!data.IsObject()) return src;
            var sourcesObj = data.AsObject().Get("sources");
            if (!sourcesObj.IsObject()) return src;
            var srcObj = sourcesObj.AsObject();
            foreach (var (code, sv) in EnumerateObject(srcObj))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var qs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (sv.IsObject())
                {
                    var actions = sv.AsObject().Get("actions");
                    if (actions.IsArray())
                    {
                        var arr = actions.AsArray();
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var a = arr.Get((uint)i);
                            if (a.IsString()) set.Add(a.AsString());
                        }
                    }
                    // qualitys：脚本声明的可解析音质（128k/320k/flac/flac24bit）
                    var qualitys = sv.AsObject().Get("qualitys");
                    if (qualitys.IsArray())
                    {
                        var qarr = qualitys.AsArray();
                        for (int i = 0; i < qarr.Length; i++)
                        {
                            var q = qarr.Get((uint)i);
                            if (q.IsString()) qs.Add(q.AsString());
                        }
                    }
                }
                src.ActionsBySource[code] = set;
                src.QualitysBySource[code] = qs;
            }
        }
        catch { /* 容错：解析失败按空源处理，调用方会回落 server */ }
        return src;
    }

    // request(url, options, callback) —— lx 协议 Node 风格回调
    public void request(string url, JsValue options, JsValue callback)
    {
        try
        {
            var (method, headers, body, timeoutMs, followRedirect) = ParseOptions(options);
            var cts = timeoutMs > 0
                ? new CancellationTokenSource(timeoutMs)
                : new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var req = BuildRequest(url, method, headers, body);
            // 同步执行：脚本用回调式 Promise，request 内完成 HTTP 后立即回调 resolve，
            // Promise 在 request 返回时已 settle，UnwrapIfPromiseAsync 直接读值。
            using var resp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            var raw = ReadBody(resp);
            var respObj = new Dictionary<string, object?>
            {
                ["statusCode"] = (int)resp.StatusCode,
                ["headers"] = ReadHeaders(resp),
                ["raw"] = raw,
                ["body"] = ParseBody(raw, resp.Content.Headers.ContentType?.MediaType),
            };
            callback.Call(JsValue.Undefined, new JsValue[] { JsValue.Null, JsValue.FromObject(_engine, respObj) });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            var errObj = new Dictionary<string, object?> { ["message"] = ex.Message, ["name"] = ex.GetType().Name };
            callback.Call(JsValue.Undefined, new JsValue[] { JsValue.FromObject(_engine, errObj), JsValue.Null });
        }
    }

    private static (string method, Dictionary<string, string> headers, string? body, int timeoutMs, bool followRedirect) ParseOptions(JsValue options)
    {
        var method = "GET";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? body = null;
        int timeoutMs = 0;
        var follow = true;
        if (!options.IsObject()) return (method, headers, body, timeoutMs, follow);
        var o = options.AsObject();
        var m = o.Get("method");
        if (m.IsString()) method = m.AsString().ToUpperInvariant();
        var h = o.Get("headers");
        if (h.IsObject())
            foreach (var (k, v) in EnumerateObject(h.AsObject()))
                if (v.IsString()) headers[k] = v.AsString();
        var b = o.Get("body");
        if (b.IsString()) body = b.AsString();
        else if (b.IsObject() || b.IsArray())
        {
            // body 是 JS 对象（如 {source,id,level}）→ JSON 序列化（长青SVIP 等脚本用此形式）
            body = JsonSerializer.Serialize(ToClr(b));
            if (!headers.ContainsKey("content-type")) headers["content-type"] = "application/json";
        }
        var json = o.Get("json");
        if (json.IsObject() || json.IsArray() || json.IsString() || json.IsNumber() || json.IsBoolean())
        {
            // json 字段：把 JSON 序列化为 body 并设 content-type
            body = json.IsString() ? json.AsString() : JsonSerializer.Serialize(ToClr(json));
            if (!headers.ContainsKey("content-type")) headers["content-type"] = "application/json";
        }
        var form = o.Get("form");
        if (form.IsObject())
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var (k, v) in EnumerateObject(form.AsObject()))
            {
                if (!first) sb.Append('&'); first = false;
                sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v.IsString() ? v.AsString() : v.ToString()));
            }
            body = sb.ToString();
            if (!headers.ContainsKey("content-type")) headers["content-type"] = "application/x-www-form-urlencoded";
        }
        var t = o.Get("timeout");
        if (t.IsNumber()) timeoutMs = (int)t.AsNumber();
        var fr = o.Get("followRedirect");
        if (fr.IsBoolean()) follow = fr.AsBoolean();
        return (method, headers, body, timeoutMs, follow);
    }

    private static HttpRequestMessage BuildRequest(string url, string method, Dictionary<string, string> headers, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (k, v) in headers)
        {
            // Content-Type 由 HttpContent 管理，避免重复
            if (k.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (body != null)
        {
            var ct = headers.TryGetValue("content-type", out var ctv) ? ctv : "application/json";
            req.Content = new StringContent(body, Encoding.UTF8, ct);
        }
        return req;
    }

    private static string ReadBody(HttpResponseMessage resp)
    {
        // GetAwaiter().GetResult() 同步读
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage resp)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in resp.Headers.Concat(resp.Content.Headers))
            dict[k] = string.Join(",", v);
        return dict;
    }

    private static object? ParseBody(string raw, string? mediaType)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var isJson = (mediaType ?? "").IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isJson)
        {
            // 即便 content-type 不是 json，也尝试解析（很多 lx fork 返回 text/plain 但实为 JSON）
            var trimmed = raw.TrimStart();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')) isJson = true;
        }
        if (!isJson) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ToClr(doc.RootElement);
        }
        catch
        {
            return raw;
        }
    }

    private static object? ToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value))!,
        JsonValueKind.Array => el.EnumerateArray().Select(ToClr).ToList()!,
        JsonValueKind.String => el.GetString()!,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ToString(),
    };

    private static object? ToClr(JsValue v) => v switch
    {
        _ when v.IsString() => v.AsString(),
        _ when v.IsNumber() => v.AsNumber(),
        _ when v.IsBoolean() => v.AsBoolean(),
        _ when v.IsNull() || v.IsUndefined() => null,
        _ when v.IsObject() => EnumerateObject(v.AsObject())
            .ToDictionary(kv => kv.Key, kv => ToClr(kv.Value))!,
        _ => v.ToString(),
    };

    private static IEnumerable<KeyValuePair<string, JsValue>> EnumerateObject(ObjectInstance obj)
    {
        foreach (var p in obj.GetOwnProperties())
        {
            var key = p.Key.ToString();
            if (!string.IsNullOrEmpty(key))
                yield return new KeyValuePair<string, JsValue>(key, p.Value.Value);
        }
    }

    // ── utils（基础实现；阶段 2 补 AES/RSA）──

    public LxUtils utils => new(_engine);
}

/// <summary>lx utils 桥（buffer + crypto 基础；AES/RSA 见阶段 2）。</summary>
public class LxUtils
{
    private readonly Engine _engine;
    public LxUtils(Engine e) { _engine = e; }
    public LxBufferUtils buffer => new(_engine);
    public LxCryptoUtils crypto => new(_engine);
}

public class LxBufferUtils
{
    private readonly Engine _engine;
    public LxBufferUtils(Engine e) { _engine = e; }

    /// <summary>from(str/buf, encoding?) → byte[]（Jint 包装为可索引对象）</summary>
    public object from(JsValue input, JsValue encoding)
    {
        if (input.IsString())
            return Encoding.UTF8.GetBytes(input.AsString());
        if (input.IsArray())
        {
            var arr = input.AsArray();
            var bytes = new byte[arr.Length];
            for (uint i = 0; i < arr.Length; i++) bytes[i] = (byte)arr.Get(i).AsNumber();
            return bytes;
        }
        return Array.Empty<byte>();
    }

    /// <summary>bufToString(buf, encoding?) → string</summary>
    public string bufToString(JsValue buf, JsValue encoding)
    {
        var bytes = ToBytes(buf);
        return Encoding.UTF8.GetString(bytes);
    }

    public object alloc(int size) => new byte[Math.Max(0, size)];

    internal static byte[] ToBytes(JsValue buf)
    {
        if (buf.IsString()) return Encoding.UTF8.GetBytes(buf.AsString());
        if (buf.IsArray())
        {
            var arr = buf.AsArray();
            var bytes = new byte[arr.Length];
            for (uint i = 0; i < arr.Length; i++) bytes[i] = (byte)arr.Get(i).AsNumber();
            return bytes;
        }
        // CLR byte[] 经 ObjectWrapper 包装时，尝试转回 byte[]
        if (buf.ToObject() is byte[] b) return b;
        return Array.Empty<byte>();
    }
}

public class LxCryptoUtils
{
    private readonly Engine _engine;
    public LxCryptoUtils(Engine e) { _engine = e; }

    public string md5(JsValue input)
    {
        var bytes = LxBufferUtils.ToBytes(input);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

    public string sha256(JsValue input)
    {
        var bytes = LxBufferUtils.ToBytes(input);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public string hmacSha256(JsValue data, JsValue key)
    {
        using var h = new HMACSHA256(LxBufferUtils.ToBytes(key));
        return Convert.ToHexString(h.ComputeHash(LxBufferUtils.ToBytes(data))).ToLowerInvariant();
    }

    public object randomBytes(int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(Math.Max(0, size));
        return bytes;
    }

    public string base64Encode(JsValue input)
    {
        return Convert.ToBase64String(LxBufferUtils.ToBytes(input));
    }

    public object base64Decode(string input)
    {
        try { return Convert.FromBase64String(input); }
        catch { return Array.Empty<byte>(); }
    }

    // AES / RSA —— 对齐 lx 脚本 SDK（参照 lx-music user-api-preload）。
    // 输入/输出均为字节数组（byte[]，经 Jint 包装）；字符串按 UTF-8 转字节、数组按原样取字节，
    // 与 utils.buffer 的约定一致（lx 的 dataToB64 对字符串做 UTF-8→base64，等价于 UTF-8 字节）。
    public object aesEncrypt(JsValue data, JsValue mode, JsValue key, JsValue iv)
        => AesBytes(LxBufferUtils.ToBytes(data), mode.AsString(), LxBufferUtils.ToBytes(key), LxBufferUtils.ToBytes(iv), encrypt: true);

    public object aesDecrypt(JsValue data, JsValue mode, JsValue key, JsValue iv)
        => AesBytes(LxBufferUtils.ToBytes(data), mode.AsString(), LxBufferUtils.ToBytes(key), LxBufferUtils.ToBytes(iv), encrypt: false);

    public object rsaEncrypt(JsValue data, JsValue key)
    {
        var plain = LxBufferUtils.ToBytes(data);
        var pubKey = key.IsString() ? key.AsString() : "";
        // 剥去 PEM 头尾（lx 允许带 BEGIN/END PUBLIC KEY 或裸 base64 的 SubjectPublicKeyInfo）
        pubKey = pubKey
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("-----BEGIN RSA PUBLIC KEY-----", "")
            .Replace("-----END RSA PUBLIC KEY-----", "")
            .Replace("\n", "").Replace("\r", "").Replace(" ", "");
        if (string.IsNullOrWhiteSpace(pubKey))
            throw new ArgumentException("utils.crypto.rsaEncrypt: invalid RSA public key");
        var der = Convert.FromBase64String(pubKey);
        using var rsa = RSA.Create();
        try { rsa.ImportSubjectPublicKeyInfo(der, out _); }
        catch (CryptographicException) { rsa.ImportRSAPublicKey(der, out _); }
        return RsaRawEncrypt(plain, rsa.ExportParameters(false));
    }

    /// <summary>RSA/ECB/NoPadding（裸指数运算）：cipher = plain^E mod N，等长输出（大端、左补零到模长）。</summary>
    private static byte[] RsaRawEncrypt(byte[] plain, RSAParameters pub)
    {
        if (pub.Modulus == null || pub.Exponent == null) throw new InvalidOperationException("RSA public key missing");
        var k = pub.Modulus.Length;
        var n = new BigInteger(pub.Modulus, isUnsigned: true, isBigEndian: true);
        var e = new BigInteger(pub.Exponent, isUnsigned: true, isBigEndian: true);
        var m = new BigInteger(plain, isUnsigned: true, isBigEndian: true);
        var c = BigInteger.ModPow(m, e, n);
        var raw = c.ToByteArray(isUnsigned: true, isBigEndian: true);
        // NoPadding 要求密文等长于模长；不足左侧补零
        if (raw.Length < k)
        {
            var padded = new byte[k];
            Buffer.BlockCopy(raw, 0, padded, k - raw.Length, raw.Length);
            return padded;
        }
        return raw;
    }

    private static byte[] AesBytes(byte[] data, string mode, byte[] key, byte[] iv, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Mode = mode.EndsWith("ecb", StringComparison.OrdinalIgnoreCase) ? CipherMode.ECB : CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.Key = Pad16(key);
        // ECB 无 IV；CBC 用不足 16 字节时补零的 IV（lx 脚本 IV 均为 16 字节）
        aes.IV = aes.Mode == CipherMode.ECB ? new byte[16] : Pad16(iv);
        var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, transform, CryptoStreamMode.Write))
        {
            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();
        }
        return ms.ToArray();
    }

    private static byte[] Pad16(byte[] b)
    {
        // 截断到 16、不足补零，保证 key/iv 长度恒为 16（AES-128）
        var padded = new byte[16];
        Buffer.BlockCopy(b, 0, padded, 0, Math.Min(b.Length, 16));
        return padded;
    }
}

// ──────────────────────────────────────────────────────────────────────────
//  脚本宿主：加载 .js、暴露 lx SDK、分发 action
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// lx-music 自定义源 .js 脚本宿主：下载脚本 → 在 Jint 沙箱执行 → 捕获 inited 声明 →
/// 按 action（musicUrl/lyric/pic/musicSearch）调用脚本注册的 request 处理器。
/// </summary>
public class LxScriptHost : IDisposable
{
    private static readonly HttpClient FetchHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>串行化引擎创建/赋值（防止并发加载时 _engine 被覆盖/置空）</summary>
    private readonly object _scriptLock = new();

    /// <summary>脚本动作执行串行锁：Jint 引擎非线程安全，且 lx.request 内为同步阻塞 HTTP，
    /// 必须让每次 handler.Call 独占引擎并在后台线程执行，避免占用 UI 线程。</summary>
    private readonly SemaphoreSlim _execLock = new(1, 1);

    private Engine? _engine;
    private LxBridge? _bridge;

    public bool IsLoaded => _engine != null && _bridge?.Inited == true;
    public string ScriptUrl { get; private set; } = "";
    public string ScriptFilePath { get; private set; } = "";
    /// <summary>脚本原始内容（执行成功后保留，供插件持久化到私有目录）</summary>
    public string ScriptCode { get; private set; } = "";
    /// <summary>导入方式：online=URL 拉取，local=本地文件</summary>
    public string ImportMode { get; private set; } = "";
    public LxScriptSources? Sources => _bridge?.Sources;
    public string? LastError { get; private set; }
    /// <summary>脚本更新提示（脚本检测到新版时填充）</summary>
    public string? UpdateMessage => _bridge?.UpdateMessage;
    public string? UpdateVersion => _bridge?.UpdateVersion;
    public string? UpdateUrl => _bridge?.UpdateUrl;
    public bool HasUpdateAlert => _bridge?.HasUpdateAlert == true;

    /// <summary>在线导入：下载 .js 并执行。</summary>
    public async Task<bool> LoadAsync(string jsUrl, CancellationToken ct = default)
    {
        Unload();
        ScriptUrl = (jsUrl ?? "").Trim();
        ScriptFilePath = "";
        ImportMode = "online";
        if (string.IsNullOrEmpty(ScriptUrl))
        {
            LastError = "脚本地址为空";
            return false;
        }
        try
        {
            var code = await FetchHttp.GetStringAsync(ScriptUrl, ct).ConfigureAwait(false);
            return await RunAsync(code);
        }
        catch (Exception ex)
        {
            LastError = "下载脚本失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>本地导入：读取本地 .js 文件并执行。</summary>
    public async Task<bool> LoadFromFileAsync(string filePath, CancellationToken ct = default)
    {
        Unload();
        ScriptFilePath = (filePath ?? "").Trim();
        ScriptUrl = "";
        ImportMode = "local";
        if (string.IsNullOrEmpty(ScriptFilePath) || !File.Exists(ScriptFilePath))
        {
            LastError = "脚本文件不存在";
            return false;
        }
        try
        {
            var code = await File.ReadAllTextAsync(ScriptFilePath, ct).ConfigureAwait(false);
            return await RunAsync(code);
        }
        catch (Exception ex)
        {
            LastError = "读取脚本文件失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>执行脚本代码（async：脚本用 async IIFE 初始化时需 drain 微任务）。
    /// 并发安全：引擎创建/赋值在锁内，执行用局部引用（await 期间不受 Unload 并发置空影响）。</summary>
    public async Task<bool> RunAsync(string code)
    {
        Engine engine;
        LxBridge bridge;
        lock (_scriptLock)
        {
            engine = new Engine(opts => opts
                .LimitRecursion(5000)
                .TimeoutInterval(TimeSpan.FromSeconds(8)));
            bridge = new LxBridge(engine);
            _engine = engine;
            _bridge = bridge;
            engine.Global["lx"] = JsValue.FromObject(engine, bridge);
            // 提供 no-op console（脚本常用 console.log 调试，Jint 默认无 console）
            engine.Execute("var console={log:function(){},error:function(){},warn:function(){},info:function(){},debug:function(){},trace:function(){}}");
        }
        try
        {
            // ExecuteAsync 会 await 脚本里 pending 的 Promise/async IIFE（如 checkUpdate 后 send inited）
            await engine.ExecuteAsync(code);
            if (bridge.Inited && bridge.Sources != null && bridge.Sources.SourceCodes.Count > 0)
            {
                ScriptCode = code;
                return true;
            }
            // 脚本发了 updateAlert 但未 inited（检测到新版，拒绝运行旧版）
            if (bridge.HasUpdateAlert)
            {
                LastError = bridge.UpdateMessage ?? "脚本有新版本";
                return false;
            }
            LastError = bridge.Inited ? "脚本未声明任何源" : "脚本未调用 send(inited, ...)";
            return false;
        }
        catch (Exception ex)
        {
            LastError = "执行脚本失败：" + ex.Message;
            // JS 异常附带堆栈（定位混淆脚本报错点）
            if (ex is Jint.Runtime.JavaScriptException jex && !string.IsNullOrWhiteSpace(jex.JavaScriptStackTrace))
                LastError += "\n" + jex.JavaScriptStackTrace;
            return false;
        }
    }

    /// <summary>同步执行（脚本顶层无 async 时用；兼容旧调用）。</summary>
    public bool Run(string code) => RunAsync(code).GetAwaiter().GetResult();

    public void Unload()
    {
        _engine = null;
        _bridge = null;
        ScriptUrl = "";
        ScriptFilePath = "";
        ImportMode = "";
        ScriptCode = "";
    }

    /// <summary>脚本是否支持某源某 action。</summary>
    public bool Supports(string sourceCode, string action) =>
        IsLoaded && _bridge!.Sources != null && _bridge.Sources.Supports(sourceCode, action);

    /// <summary>取播放直链（musicUrl action）。musicInfo 用过供式 id 字段覆盖常见脚本。</summary>
    public async Task<string?> GetMusicUrlAsync(string sourceCode, string rawId, string title, string artist,
        long durationSec, string lxQuality, CancellationToken ct = default)
    {
        if (!Supports(sourceCode, "musicUrl")) return null;
        var musicInfo = new Dictionary<string, object?>
        {
            ["hash"] = rawId,
            ["songmid"] = rawId,
            ["songId"] = rawId,
            ["id"] = rawId,
            ["copyrightId"] = rawId,
            ["name"] = title,
            ["singer"] = artist,
            ["interval"] = durationSec,
        };
        var arg = new Dictionary<string, object?>
        {
            ["action"] = "musicUrl",
            ["source"] = sourceCode,
            ["info"] = new Dictionary<string, object?> { ["musicInfo"] = musicInfo, ["type"] = lxQuality },
        };
        var result = await InvokeHandlerAsync(arg, ct).ConfigureAwait(false);
        return result?.IsString() == true ? result.AsString() : null;
    }

    /// <summary>搜索（musicSearch action）。脚本返回歌曲数组，每项含 {id/name/singer/album/interval/source}。</summary>
    public async Task<List<LxScriptSearchItem>?> SearchAsync(string keyword, int page, int limit, string sourceCode, CancellationToken ct = default)
    {
        if (!Supports(sourceCode, "musicSearch")) return null;
        var arg = new Dictionary<string, object?>
        {
            ["action"] = "musicSearch",
            ["source"] = sourceCode,
            ["info"] = new Dictionary<string, object?> { ["text"] = keyword, ["page"] = page, ["limit"] = limit },
        };
        var result = await InvokeHandlerAsync(arg, ct).ConfigureAwait(false);
        return ParseSearchResult(result);
    }

    /// <summary>歌词（lyric action）。脚本返回 {lyric/translation/romanic} 或 LRC 文本。</summary>
    public async Task<(string? Lrc, string? TLrc, string? RLrc)?> GetLyricAsync(string sourceCode, string rawId, string title, string artist, long durationSec, CancellationToken ct = default)
    {
        if (!Supports(sourceCode, "lyric")) return null;
        var musicInfo = new Dictionary<string, object?>
        {
            ["hash"] = rawId, ["songmid"] = rawId, ["songId"] = rawId, ["id"] = rawId,
            ["name"] = title, ["singer"] = artist, ["interval"] = durationSec,
        };
        var arg = new Dictionary<string, object?>
        {
            ["action"] = "lyric", ["source"] = sourceCode, ["info"] = new Dictionary<string, object?> { ["musicInfo"] = musicInfo },
        };
        var result = await InvokeHandlerAsync(arg, ct).ConfigureAwait(false);
        if (result == null) return null;
        // 兼容两种返回：字符串（LRC）或 {lyric, translation, romanic}
        if (result.IsString()) return (result.AsString(), null, null);
        if (result.IsObject())
        {
            var o = result.AsObject();
            string? Get(string k) => o.Get(k) is var v && v.IsString() ? v.AsString() : null;
            return (Get("lyric") ?? Get("lrc"), Get("translation") ?? Get("tlyric"), Get("romanic") ?? Get("rlyric"));
        }
        return null;
    }

    /// <summary>封面（pic action）。</summary>
    public async Task<string?> GetPicUrlAsync(string sourceCode, string rawId, string title, string artist, long durationSec, CancellationToken ct = default)
    {
        if (!Supports(sourceCode, "pic")) return null;
        var musicInfo = new Dictionary<string, object?>
        {
            ["hash"] = rawId, ["songmid"] = rawId, ["songId"] = rawId, ["id"] = rawId,
            ["name"] = title, ["singer"] = artist, ["interval"] = durationSec,
        };
        var arg = new Dictionary<string, object?>
        {
            ["action"] = "pic", ["source"] = sourceCode, ["info"] = new Dictionary<string, object?> { ["musicInfo"] = musicInfo },
        };
        var result = await InvokeHandlerAsync(arg, ct).ConfigureAwait(false);
        return result?.IsString() == true ? result.AsString() : null;
    }

    private async Task<JsValue?> InvokeHandlerAsync(object arg, CancellationToken ct)
    {
        if (_engine == null || _bridge?.Handlers.TryGetValue("request", out var handler) != true) return null;
        try
        {
            var argJs = JsValue.FromObject(_engine, arg);
            // 拿到 handler 后在其初始化之外执行。为避免 Jint 引擎被并发访问以及
            // lx.request 内同步阻塞 HTTP 卡 UI，把整段 JS 调用放到后台线程并串行化。
            await _execLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await Task.Run(() => handler.Call(JsValue.Undefined, new JsValue[] { argJs }), ct).ConfigureAwait(false);
                if (result.IsPromise())
                {
                    var settled = await result.UnwrapIfPromiseAsync(ct).ConfigureAwait(false);
                    return settled;
                }
                return result;
            }
            finally
            {
                _execLock.Release();
            }
        }
        catch (Exception ex)
        {
            LastError = "脚本执行错误：" + ex.Message;
            return null;
        }
    }

    private static List<LxScriptSearchItem>? ParseSearchResult(JsValue? result)
    {
        if (result == null || !result.IsArray()) return null;
        var arr = result.AsArray();
        var list = new List<LxScriptSearchItem>((int)arr.Length);
        for (uint i = 0; i < arr.Length; i++)
        {
            var item = arr.Get(i);
            if (!item.IsObject()) continue;
            var o = item.AsObject();
            string Get(string k) => o.Get(k) is var v && v.IsString() ? v.AsString() : "";
            long GetInterval()
            {
                var v = o.Get("interval");
                if (v.IsNumber()) return (long)v.AsNumber();
                return 0;
            }
            list.Add(new LxScriptSearchItem
            {
                Id = Get("id") ?? Get("songmid") ?? Get("hash") ?? Get("songId"),
                Name = Get("name") ?? Get("title"),
                Artist = Get("singer") ?? Get("artist"),
                Album = Get("album"),
                Source = Get("source"),
                IntervalSeconds = GetInterval(),
            });
        }
        return list;
    }

    public void Dispose()
    {
        Unload();
    }
}

/// <summary>musicSearch 返回项（源短码 + 原始 id + 元信息）。</summary>
public class LxScriptSearchItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Source { get; set; } = "";
    public long IntervalSeconds { get; set; }
}
