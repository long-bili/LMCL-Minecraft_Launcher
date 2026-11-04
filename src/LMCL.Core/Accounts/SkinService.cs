using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Accounts;

/// <summary>
/// 皮肤与披风的读写。
///
/// 全部走 Minecraft 服务（api.minecraftservices.com），Bearer 用的是
/// <see cref="Account.AccessToken"/>——注意它和微软那个令牌不是一回事，是
/// Xbox → XSTS → Minecraft 一路换下来的「游戏令牌」，默认 24 小时过期。
/// 所以这里的每个动作开头都要先过一遍 <see cref="EnsureTokenAsync"/>：
/// 过期了就拿刷新令牌续一次，续不动才报错让用户重新登录。
///
/// 用到的端点：
///   GET    /minecraft/profile                读档案（含 skins / capes）
///   PATCH  /minecraft/profile                切换激活的皮肤 / 披风
///   DELETE /minecraft/profile/capes/active   取消披风（官方文档里没写，但确实有效）
///   PUT    /minecraft/profile/skin           上传新皮肤（multipart）
///   DELETE /minecraft/profile/skin           重置成默认的 Steve / Alex
///
/// 披风只能从「已拥有」里选，不能上传——这是 Mojang 的限制，不是这里偷懒。
/// </summary>
public sealed class SkinService
{
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string SkinUrl = "https://api.minecraftservices.com/minecraft/profile/skin";
    private const string ActiveCapeUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";

    /// <summary>皮肤允许的像素尺寸。64×32 是 1.8 之前的老格式，现在的客户端仍然认。</summary>
    private static readonly (int Width, int Height)[] AllowedSkinSizes = { (64, 64), (64, 32) };

    private readonly MicrosoftAuth _auth;
    private readonly HttpClient _http;
    private readonly ILogSink? _log;

    public SkinService(MicrosoftAuth auth, HttpClient http, ILogSink? log = null)
    {
        _auth = auth;
        _http = http;
        _log = log;
    }

    // ── 档案 ────────────────────────────────────────────────

    /// <summary>重新拉一次档案，并把它写回账号（含皮肤 / 披风列表与当前激活项）。</summary>
    public async Task<MinecraftProfile> RefreshAsync(Account account, CancellationToken ct = default)
    {
        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);
        var profile = await _auth.GetProfileFullAsync(token, ct).ConfigureAwait(false);

        Apply(account, profile);

        _log?.Info($"已读取 {profile.Name} 的外观：{profile.Skins.Count} 款皮肤，" +
                   (profile.Capes.Count == 0 ? "没有披风" : $"{profile.Capes.Count} 款披风"));

        return profile;
    }

    /// <summary>把一次档案读取的结果落到账号上。UI 层登录完也能直接调它，省一次网络往返。</summary>
    public static void Apply(Account account, MinecraftProfile profile) =>
        Apply(account, profile.Skins, profile.Capes);

    /// <summary>同上，但接收裸列表——登录流程里拿到的就是两个列表。</summary>
    public static void Apply(Account account, IReadOnlyList<SkinInfo>? skins, IReadOnlyList<CapeInfo>? capes)
    {
        account.Skins = skins?.ToList() ?? new List<SkinInfo>();
        account.Capes = capes?.ToList() ?? new List<CapeInfo>();

        var skin = account.Skins.FirstOrDefault(s => s.IsActive);
        var cape = account.Capes.FirstOrDefault(c => c.IsActive);

        account.SkinUrl = skin?.Url;
        account.SkinVariant = skin?.Variant;
        account.CapeUrl = cape?.Url;
    }

    // ── 切换 / 重置 ─────────────────────────────────────────

    /// <summary>把某个已拥有的皮肤设为激活。</summary>
    public async Task SelectSkinAsync(Account account, string skinId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(skinId)) throw new InvalidOperationException("没指定要切换成哪款皮肤。");

        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);

        // 一起把当前披风带上：PATCH 是「覆盖式」的写法，只发 skinId 有把披风一并
        // 清掉的风险，官方启动器自己也是两个字段一起发的。
        var body = new Dictionary<string, string> { ["skinId"] = skinId };
        if (account.Capes?.FirstOrDefault(c => c.IsActive) is { } cape && cape.Id.Length > 0)
            body["capeId"] = cape.Id;

        await PatchProfileAsync(token, body, ct).ConfigureAwait(false);
        await RefreshAsync(account, ct).ConfigureAwait(false);
        _log?.Success($"已切换皮肤：{account.Name}");
    }

    /// <summary>戴上某个已拥有的披风。</summary>
    public async Task SelectCapeAsync(Account account, string capeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(capeId)) throw new InvalidOperationException("没指定要换成哪款披风。");

        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);

        var body = new Dictionary<string, string> { ["capeId"] = capeId };
        if (account.Skins?.FirstOrDefault(s => s.IsActive) is { } skin && skin.Id.Length > 0)
            body["skinId"] = skin.Id;

        await PatchProfileAsync(token, body, ct).ConfigureAwait(false);
        await RefreshAsync(account, ct).ConfigureAwait(false);
        _log?.Success($"已切换披风：{account.Name}");
    }

    /// <summary>取消披风（不戴）。</summary>
    public async Task ClearCapeAsync(Account account, CancellationToken ct = default)
    {
        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Delete, ActiveCapeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        // 404 = 本来就没戴披风，对用户来说等于「已经达成了」，不该报错
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"取消披风失败（HTTP {(int)response.StatusCode}）{await DescribeAsync(response, ct).ConfigureAwait(false)}");

        await RefreshAsync(account, ct).ConfigureAwait(false);
        _log?.Success($"已取消披风：{account.Name}");
    }

    /// <summary>重置成默认皮肤（Steve / Alex）。自己上传过的皮肤不会丢，随时能再切回来。</summary>
    public async Task ResetSkinAsync(Account account, CancellationToken ct = default)
    {
        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Delete, SkinUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"重置皮肤失败（HTTP {(int)response.StatusCode}）{await DescribeAsync(response, ct).ConfigureAwait(false)}");

        await RefreshAsync(account, ct).ConfigureAwait(false);
        _log?.Success($"已重置皮肤：{account.Name}");
    }

    /// <summary>
    /// 上传新皮肤并立即应用。
    ///
    /// <paramref name="variant"/> 取 <c>CLASSIC</c>（经典，4px 手臂）或 <c>SLIM</c>（纤细，3px）。
    /// PNG 必须是 64×64 或 64×32，这里先本地量一遍尺寸——上传一个尺寸不对的文件，
    /// 服务端只会回一句很含糊的 400，不如提前说清楚。
    /// </summary>
    public async Task UploadSkinAsync(Account account, byte[] png, string variant, CancellationToken ct = default)
    {
        if (png is null || png.Length == 0) throw new InvalidOperationException("皮肤文件是空的。");

        if (ReadPngSize(png) is not { } size)
            throw new InvalidOperationException("这不是一张有效的 PNG 图片。");

        if (!AllowedSkinSizes.Contains(size))
            throw new InvalidOperationException(
                $"皮肤尺寸不对：当前是 {size.Width}×{size.Height}，必须是 64×64（或老格式 64×32）。");

        var isSlim = variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase);
        var token = await EnsureTokenAsync(account, ct).ConfigureAwait(false);

        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "skin.png");

        // variant 用 text/plain 明说一遍：StringContent 默认会塞 charset=utf-8，
        // 老端点对这种多余参数挑嘴。
        var variantContent = new StringContent(isSlim ? "slim" : "classic");
        variantContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(variantContent, "variant");

        using var request = new HttpRequestMessage(HttpMethod.Put, SkinUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"上传皮肤失败（HTTP {(int)response.StatusCode}）{await DescribeAsync(response, ct).ConfigureAwait(false)}");

        await RefreshAsync(account, ct).ConfigureAwait(false);
        _log?.Success($"已上传并应用新皮肤：{account.Name}（{(isSlim ? "纤细" : "经典")}模型）");
    }

    // ── 纹理缓存 ────────────────────────────────────────────

    /// <summary>
    /// 把一张纹理下载到本地缓存，返回本地文件路径；已经在缓存里就直接返回。
    ///
    /// 为什么不直接在 XAML 里绑远程地址：textures.minecraft.net 在国内时好时坏，
    /// 每次打开窗口都联网拉一遍既慢又容易白屏。落盘之后断网也画得出来。
    /// </summary>
    public async Task<string> EnsureTextureAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("纹理地址是空的。", nameof(url));

        var file = TextureCachePath(url);

        if (File.Exists(file) && new FileInfo(file).Length > 0) return file;

        var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidOperationException("服务器返回了一张空图。");

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // 先写临时文件再改名：中途断网不会留下半张图被当成「已缓存」
        var temp = file + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);

        return file;
    }

    /// <summary>已经缓存过的纹理路径；没缓存过返回 null（不去联网）。</summary>
    public static string? CachedTexturePath(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : TextureCachePath(url!);

    /// <summary>纹理地址 → 本地缓存文件。取地址末段当文件名（就是一个 sha1 哈希），拿不到就整串算一个。</summary>
    private static string TextureCachePath(string url)
    {
        var name = url.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

        if (name.Length is 0 or > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

        return Path.Combine(AppPaths.CacheDir, "textures", name + ".png");
    }

    // ── 内部 ────────────────────────────────────────────────

    /// <summary>
    /// 交出一个能用的游戏令牌。没到期直接用；到期了用刷新令牌换一个新的，
    /// 顺手把新令牌写回账号——不然每次操作都要多续一次。
    /// </summary>
    private async Task<string> EnsureTokenAsync(Account account, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(account.AccessToken) && !account.IsExpired)
            return account.AccessToken;

        if (string.IsNullOrEmpty(account.RefreshToken))
            throw new InvalidOperationException(
                "这个账号的登录凭证已经过期，也没有刷新令牌可用。请到「添加账号」里重新登录一次。");

        _log?.Info("登录凭证已过期，正在自动续期…");

        var refreshed = await _auth.RefreshAsync(account.RefreshToken, ct).ConfigureAwait(false);

        account.AccessToken = refreshed.AccessToken;
        if (!string.IsNullOrEmpty(refreshed.RefreshToken)) account.RefreshToken = refreshed.RefreshToken;
        account.ExpiresAt = refreshed.ExpiresAt;
        if (!string.IsNullOrEmpty(refreshed.Xuid)) account.Xuid = refreshed.Xuid;

        _log?.Success("登录凭证已续期");
        return account.AccessToken;
    }

    private async Task PatchProfileAsync(string token, Dictionary<string, string> body, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Patch, ProfileUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"切换外观失败（HTTP {(int)response.StatusCode}）{await DescribeAsync(response, ct).ConfigureAwait(false)}");
    }

    /// <summary>把失败响应体里那句话抠出来。抠不到就返回空串，让调用方只用状态码。</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return "";

            using var json = JsonDocument.Parse(raw);

            foreach (var key in new[] { "errorMessage", "message", "error", "details" })
            {
                if (!json.RootElement.TryGetProperty(key, out var value)) continue;

                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Array => string.Join("；", value.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())),
                    _ => value.ToString(),
                };

                if (!string.IsNullOrWhiteSpace(text)) return $"：{Trim(text!)}";
            }

            return $"：{Trim(raw)}";
        }
        catch
        {
            return "";
        }
    }

    private static string Trim(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// 从 PNG 字节里直接读宽高（IHDR 就在固定偏移上），不引入任何图像库。
    /// 读不出来（不是 PNG）返回 null。
    /// </summary>
    private static (int Width, int Height)? ReadPngSize(byte[] bytes)
    {
        // 8 字节签名 + 4 字节块长度 + "IHDR" + 宽(4) + 高(4)
        if (bytes.Length < 24) return null;

        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;
        if (bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R')
            return null;

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        return width > 0 && height > 0 ? (width, height) : null;
    }
}
