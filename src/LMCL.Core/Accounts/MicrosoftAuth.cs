using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Accounts;

public sealed record XboxAuthResult(
    string Name,
    string Uuid,
    string AccessToken,
    string RefreshToken,
    string Xuid,
    DateTime ExpiresAt,
    IReadOnlyList<SkinInfo>? Skins = null,
    IReadOnlyList<CapeInfo>? Capes = null);

/// <summary>
/// 一次完整的档案读取结果。
///
/// 登录时其实只要 uuid 和 name 就够启动游戏了，但档案接口顺手就把皮肤和披风一起
/// 返回了——多解析两个数组几乎不花时间，却能让账号刚登录完就能在「账号管理」里
/// 看到自己的皮肤，省掉一次「点刷新」。
/// </summary>
public sealed record MinecraftProfile(
    string Uuid,
    string Name,
    List<SkinInfo> Skins,
    List<CapeInfo> Capes)
{
    public SkinInfo? ActiveSkin => Skins.FirstOrDefault(s => s.IsActive);

    public CapeInfo? ActiveCape => Capes.FirstOrDefault(c => c.IsActive);
}

/// <summary>
/// 微软正版登录。
///
/// 全流程分四棒：
///   ① 打开浏览器 → 用户登录 → 浏览器停在 login.live.com/oauth20_desktop.srf，
///      授权码（code）就挂在地址栏里
///   ② 用户把那串 code（或整段网址）粘回启动器
///   ③ 授权码 → Microsoft 令牌，再换成 Xbox Live 用户令牌（XBL）
///   ④ XBL → XSTS 授权令牌 → Minecraft 服务令牌，再取档案（名字 + UUID）
///
/// 密码全程在微软自己的页面上输入，启动器碰不到；令牌只存本机。
///
/// 关于端点与 redirect_uri（重点，这就是之前登录报
/// “redirect_uri is not valid” 的根因）：
///   走的是老式 Live 授权码流（login.live.com），不是 Azure AD v2.0 设备代码流——
///   那个内置 client_id 在 v2.0 端点上会直接报 AADSTS700016（应用不存在）。
///   而在 Live 端点上，这个内置 client_id **只**认一个回调地址：
///   https://login.live.com/oauth20_desktop.srf 。它不认任何
///   http://127.0.0.1:端口/ 之类的本机回环地址——那些地址没在应用里登记过，
///   所以会被原样拒绝。
///
///   正因如此，启动器不再尝试搭本机监听去“自动接住”授权码（那只会换来上面的报错），
///   而是改用桌面回调，让用户把浏览器地址栏里的 code 粘回来——这是该 client_id 下
///   唯一走得通的路子。想用本机回环自动接码，得自己到 Azure 注册一个应用、
///   把对应地址登记进去，但那不在内置流程里。
/// </summary>
public sealed class MicrosoftAuth
{
    private const string AuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string Scope = "XboxLive.signin offline_access";

    /// <summary>
    /// 游戏档案端点。除了名字和 UUID，它还一并返回这个账号拥有的皮肤与披风
    ///（skins / capes 两个数组），是皮肤管理功能的唯一数据来源。
    /// </summary>
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>
    /// 登录用的 client_id，内置写死。
    ///
    /// 这是官方 Minecraft 启动器自己的应用 ID，在启动器圈子里是公开的事实标准，
    /// 绝大多数第三方启动器用的都是这一个。请求全程直连微软，不过任何中转。
    ///
    /// 哪天微软把它换了，登录会以「应用不存在」的形式失败 —— 改这一个常量即可。
    /// </summary>
    public const string ClientId = "00000000402b5328";

    /// <summary>
    /// 这个内置 client_id 在 Live 端点上**唯一**有效的回调地址。
    /// 它落在 login.live.com 域名下，本机没法监听它来“自动接码”，
    /// 只能让用户把浏览器地址栏里的 code 粘回来（见 <see cref="ExchangeCodeAsync"/>）。
    /// </summary>
    private const string DesktopRedirect = "https://login.live.com/oauth20_desktop.srf";

    private readonly HttpClient _http;
    private readonly ILogSink? _log;

    public MicrosoftAuth(HttpClient http, ILogSink? log = null)
    {
        _http = http;
        _log = log;
    }

    /// <summary>拼出要丢给浏览器打开的授权地址（已经带好桌面回调）。</summary>
    public string BuildAuthorizeUrl()
    {
        return $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(ClientId)}"
               + "&response_type=code"
               + $"&scope={Uri.EscapeDataString(Scope)}"
               + "&redirect_uri=" + Uri.EscapeDataString(DesktopRedirect);
    }

    /// <summary>
    /// 用户把浏览器地址栏里的东西（整段网址，或只粘 code= 后面那一串）粘回来之后，
    /// 换成令牌并一路走到 Minecraft 档案。
    /// </summary>
    public async Task<XboxAuthResult> ExchangeCodeAsync(
        string rawCode, CancellationToken ct = default)
    {
        var code = ExtractCode(rawCode);
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException(
                "没认出授权码。请把浏览器登录后停在的那个地址（形如 "
                + DesktopRedirect + "?code=……）整段粘进来，或者只粘 code= 后面那一长串。");

        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = DesktopRedirect,
            }), ct).ConfigureAwait(false);

        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = GetString(json, "error") ?? "";
            var description = GetString(json, "error_description") ?? "";
            throw new InvalidOperationException(DescribeFailure(error, description));
        }

        var access = GetString(json, "access_token")
            ?? throw new InvalidOperationException("令牌响应里没有 access_token");
        var refresh = GetString(json, "refresh_token") ?? "";

        return await AuthenticateXboxAsync(access, refresh, ct).ConfigureAwait(false);
    }

    /// <summary>用 refresh_token 续期，避免每次都让用户走一遍浏览器。</summary>
    public async Task<XboxAuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            }), ct).ConfigureAwait(false);

        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = GetString(json, "error") ?? "";
            var description = GetString(json, "error_description") ?? "";
            throw new InvalidOperationException(DescribeFailure(error, description));
        }

        var msToken = GetString(json, "access_token")
            ?? throw new InvalidOperationException("续期响应里没有 access_token");
        var newRefresh = GetString(json, "refresh_token") ?? refreshToken;

        return await AuthenticateXboxAsync(msToken, newRefresh, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 冒烟测试：只问微软认不认这个 client_id，不需要真人登录。
    /// 给命令行探针用 —— 哪天登录集体失败，先跑它区分「id 作废了」还是「网断了」。
    /// </summary>
    public async Task<(bool Ok, string Message)> ProbeClientIdAsync(CancellationToken ct = default)
    {
        var url = $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(ClientId)}"
                  + "&response_type=code"
                  + $"&scope={Uri.EscapeDataString(Scope)}"
                  + "&redirect_uri=" + Uri.EscapeDataString(DesktopRedirect);

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            // Live 认不认识的判据：不认识的 client_id 会直接把错误写在回来的地址里
            var landed = response.RequestMessage?.RequestUri?.ToString() ?? "";
            var rejected = response.StatusCode == HttpStatusCode.BadRequest
                           || landed.Contains("invalid_client", StringComparison.OrdinalIgnoreCase);

            return rejected
                ? (false, $"微软不认这个 client_id 了（HTTP {(int)response.StatusCode}）。" +
                          "改 MicrosoftAuth.ClientId 换一个。")
                : (true, $"可用 —— 微软接受了这个 client_id（HTTP {(int)response.StatusCode}）。");
        }
        catch (Exception ex)
        {
            return (false, $"连不上微软的登录服务：{ex.Message}");
        }
    }

    // ── Xbox → XSTS → Minecraft ─────────────────────────────

    private async Task<XboxAuthResult> AuthenticateXboxAsync(string msToken, string refreshToken, CancellationToken ct)
    {
        // ② Xbox Live
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, ct).ConfigureAwait(false);

        var xblToken = GetString(xbl, "Token")
            ?? throw new InvalidOperationException("Xbox Live 没有返回令牌");
        var uhs = ReadClaim(xbl, "uhs")
            ?? throw new InvalidOperationException("Xbox Live 响应缺少 uhs");
        xbl.Dispose();

        // ③ XSTS
        JsonDocument xsts;
        try
        {
            xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken },
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }, ct).ConfigureAwait(false);
        }
        catch (XstsException ex)
        {
            throw new InvalidOperationException(ex.XErr switch
            {
                "2148916233" => "这个微软账号还没有 Xbox 档案，请先到 xbox.com 登录一次",
                "2148916235" => "你所在地区无法使用 Xbox Live",
                "2148916236" or "2148916237" => "该账号需要完成成人验证",
                "2148916238" => "这是未成年账号，需要由家庭成员添加到家庭组",
                _ => $"XSTS 授权失败（XErr {ex.XErr}）",
            });
        }

        var xstsToken = GetString(xsts, "Token")
            ?? throw new InvalidOperationException("XSTS 没有返回令牌");
        var xstsUhs = ReadClaim(xsts, "uhs") ?? uhs;
        xsts.Dispose();

        // ④ Minecraft 服务
        var minecraft = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={xstsUhs};{xstsToken}" }, ct).ConfigureAwait(false);

        var mcToken = GetString(minecraft, "access_token")
            ?? throw new InvalidOperationException("Minecraft 服务没有返回令牌");
        var expiresIn = GetInt(minecraft, "expires_in", 86400);
        minecraft.Dispose();

        var profile = await GetProfileFullAsync(mcToken, ct).ConfigureAwait(false);

        _log?.Success($"已登录：{profile.Name}");
        return new XboxAuthResult(profile.Name, profile.Uuid, mcToken, refreshToken, xstsUhs,
            DateTime.Now.AddSeconds(expiresIn), profile.Skins, profile.Capes);
    }

    /// <summary>
    /// 读一份完整档案：名字、UUID，以及这个账号拥有的皮肤与披风。
    ///
    /// 这是「皮肤管理」的入口——登录之后想换皮肤、想看看现在戴着什么披风，
    /// 都从这里取一次最新的，不要拿账号里缓存的那份当准（缓存只是用来离线画图）。
    /// </summary>
    public async Task<MinecraftProfile> GetProfileFullAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = ParseSafe(raw);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            json.Dispose();
            throw new InvalidOperationException("这个账号没有购买 Minecraft：Java 版");
        }

        EnsureSuccess(response, json, "获取游戏档案失败", raw);

        var uuid = GetString(json, "id") ?? "";
        var name = GetString(json, "name") ?? "";
        var skins = ReadSkins(json);
        var capes = ReadCapes(json);
        json.Dispose();

        return new MinecraftProfile(uuid, name, skins, capes);
    }

    /// <summary>解析档案里的 skins 数组。字段缺失或类型不对就跳过那一项，不让一条脏数据毁掉整次读取。</summary>
    private static List<SkinInfo> ReadSkins(JsonDocument json)
    {
        var list = new List<SkinInfo>();
        if (!json.RootElement.TryGetProperty("skins", out var array) || array.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new SkinInfo
            {
                Id = ReadText(item, "id"),
                Url = ReadText(item, "url"),
                Variant = ReadText(item, "variant"),
                State = ReadText(item, "state"),
                Alias = ReadText(item, "alias"),
            });
        }

        return list;
    }

    /// <summary>解析档案里的 capes 数组。</summary>
    private static List<CapeInfo> ReadCapes(JsonDocument json)
    {
        var list = new List<CapeInfo>();
        if (!json.RootElement.TryGetProperty("capes", out var array) || array.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new CapeInfo
            {
                Id = ReadText(item, "id"),
                Url = ReadText(item, "url"),
                State = ReadText(item, "state"),
                Alias = ReadText(item, "alias"),
            });
        }

        return list;
    }

    private static string ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private async Task<JsonDocument> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        var jsonText = JsonSerializer.Serialize(body);
        var content = new StringContent(jsonText, Encoding.UTF8, "application/json");
        // 老 Xbox 端点对 Content-Type 里的 charset 参数挑嘴，去掉更稳
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = ParseSafe(raw);

        // XSTS 的账号类错误（未成年 / 无 Xbox 档案 / 地区限制）可能以 400 或 401 回来，
        // 都带 XErr。之前只在 401 时认，400 会被吞成一句「请求 Xbox 服务失败」，
        // 用户完全看不出真正原因。这里只要响应里出现 XErr 就走友好翻译。
        if (url.Contains("xsts"))
        {
            var xerr = GetString(json, "XErr") ?? GetString(json, "xErr");
            if (xerr is { Length: > 0 })
            {
                json.Dispose();
                throw new XstsException(xerr);
            }
        }

        EnsureSuccess(response, json, "请求 Xbox 服务失败", raw);
        return json;
    }

    private static string? ReadClaim(JsonDocument document, string claim)
    {
        if (!document.RootElement.TryGetProperty("DisplayClaims", out var claims)) return null;
        if (!claims.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in xui.EnumerateArray())
            if (entry.TryGetProperty(claim, out var value)) return value.GetString();
        return null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseSafe(text);
    }

    /// <summary>把响应体尽量解析成 JSON；不是合法 JSON（HTML 错误页等）就退回空对象，别让解析异常把真正状态码盖掉。</summary>
    private static JsonDocument ParseSafe(string? text)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, JsonDocument json, string what, string? raw = null)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = GetString(json, "error_description")
                     ?? GetString(json, "errorMessage")
                     ?? GetString(json, "error")
                     ?? (json.RootElement.ValueKind == JsonValueKind.Object
                         && json.RootElement.TryGetProperty("Message", out var m)
                         ? m.GetString() : null)
                     ?? (string.IsNullOrWhiteSpace(raw) ? null : CleanRaw(raw))
                     ?? $"HTTP {(int)response.StatusCode}";
        json.Dispose();
        throw new InvalidOperationException($"{what}：{CleanDescription(detail)}（HTTP {(int)response.StatusCode}）");
    }

    /// <summary>砍掉微软错误页里的大段 HTML，只留前几百字给排查用。</summary>
    private static string CleanRaw(string raw)
    {
        var text = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    /// <summary>
    /// 把换令牌时的失败说成人话。Live 那边常见的就那么几个，
    /// 直接把原始 JSON 丢给用户等于没说。
    /// </summary>
    private static string DescribeFailure(string error, string description)
    {
        var detail = CleanDescription(description);

        return error switch
        {
            "invalid_grant" =>
                "授权码已经用过了或者过期了。这通常不是故障 —— 回到 LMCL 重新点一次登录就行。",
            "invalid_client" =>
                "微软不认这个 client_id 了（可能被停用）。改 MicrosoftAuth.ClientId 换一个。",
            "invalid_request" when detail.Contains("redirect", StringComparison.OrdinalIgnoreCase) =>
                "回调地址没被接受：浏览器登录后没能回到启动器。重新点一次登录。",
            "invalid_scope" =>
                "这个应用没有 XboxLive.signin 权限，登录不了 Minecraft。",
            "interaction_required" =>
                "这个账号需要额外验证（比如绑定手机或者重新同意授权），在浏览器里做完再试。",
            _ => detail.Length > 0 ? $"换令牌失败：{detail}" : $"换令牌失败（{error}）",
        };
    }

    /// <summary>砍掉微软附带的 Trace ID / Correlation ID / Timestamp —— 对排查没用，只会把提示撑爆。</summary>
    private static string CleanDescription(string text)
    {
        foreach (var marker in new[] { "Trace ID:", "Correlation ID:", "Timestamp:" })
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) text = text[..index];
        }

        return text.Trim().TrimEnd(';', '.', ',', ' ');
    }

    /// <summary>
    /// 从用户粘回来的内容里抠出授权码。支持两种粘法：
    ///   · 整段网址（形如 …?code=XXXX&amp;state=… 或 …#code=XXXX）—— 取 code= 后面那一串；
    ///   · 只粘了 code 本身那一大串 —— 原样返回，令牌端点会告诉我们对不对。
    /// </summary>
    private static string ExtractCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        raw = raw.Trim();
        const string marker = "code=";
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (idx >= 0)
        {
            var value = raw[(idx + marker.Length)..];

            // 截到下一个分隔符为止（&、#、空白都算结束）
            foreach (var stop in new[] { '&', '#', ' ', '\t', '\n', '\r' })
            {
                var at = value.IndexOf(stop);
                if (at >= 0) value = value[..at];
            }

            return Uri.UnescapeDataString(value.Trim());
        }

        // 没找到 code= 标记，就当作用户直接粘了码本身
        return raw;
    }

    private static string? GetString(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonDocument json, string name, int fallback) =>
        json.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private sealed class XstsException(string xerr) : Exception
    {
        public string XErr { get; } = xerr;
    }
}
