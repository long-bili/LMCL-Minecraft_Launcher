using System.Net.Http.Json;
using System.Text.Json;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Accounts;

public sealed record AuthResult(
    string Name, string Uuid, string AccessToken, string ClientToken, DateTime ExpiresAt);

/// <summary>
/// 第三方验证（外置登录 / authlib-injector 协议）。
///
/// 这里只负责走 Yggdrasil 的 authenticate 接口拿到令牌；
/// 真正让客户端认这台服务器，还得在 JVM 上加一个 -javaagent 指向 authlib-injector.jar，
/// 那部分交给 <see cref="AuthlibInjector"/> 备料、由 LauncherService 拼进启动参数。
/// </summary>
public sealed class ThirdPartyAuth
{
    private readonly HttpClient _http;
    private readonly ILogSink? _log;

    public ThirdPartyAuth(HttpClient http, ILogSink? log = null)
    {
        _http = http;
        _log = log;
    }

    public async Task<AuthResult> LoginAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        var baseUrl = Normalize(server);
        var url = $"{baseUrl}/authserver/authenticate";

        var payload = new
        {
            username,
            password,
            clientToken = Guid.NewGuid().ToString("N"),
            agent = new { name = "Minecraft", version = 1 },
            requestUser = false,
        };

        using var response = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument json;
        try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException) { throw new InvalidOperationException($"验证服务器返回了非 JSON 内容：{Truncate(text)}"); }

        using (json)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    Read(json, "errorMessage") ?? $"登录失败（HTTP {(int)response.StatusCode}）");

            var accessToken = Read(json, "accessToken")
                ?? throw new InvalidOperationException("验证服务器没有返回 accessToken");
            var clientToken = Read(json, "clientToken") ?? "";

            // 优先用 selectedProfile；没有就退到 availableProfiles 的第一个
            var (uuid, name) = ReadProfile(json, "selectedProfile");
            if (string.IsNullOrEmpty(uuid)) (uuid, name) = ReadProfile(json, "availableProfiles");

            if (string.IsNullOrEmpty(uuid))
                throw new InvalidOperationException("这个账号在该服务器上没有可用的角色");

            _log?.Success($"第三方登录成功：{name}");
            return new AuthResult(name, uuid, accessToken, clientToken, DateTime.Now.AddHours(24));
        }
    }

    /// <summary>探测地址是否为有效的 authlib-injector 服务器，顺便拿到它的名字。</summary>
    public async Task<string?> DescribeServerAsync(string server, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = Normalize(server);
            using var response = await _http.GetAsync(baseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            if (!json.RootElement.TryGetProperty("meta", out var meta)) return "验证服务器";
            return Read(meta, "serverName") ?? Read(meta, "implementationName") ?? "验证服务器";
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string server)
    {
        var text = (server ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请填写验证服务器地址");
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;

        // authlib-injector 的规范要求地址以 / 结尾
        return text + "/";
    }

    private static (string Uuid, string Name) ReadProfile(JsonDocument json, string key)
    {
        if (key == "availableProfiles")
        {
            if (!json.RootElement.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                return ("", "");
            foreach (var item in array.EnumerateArray())
            {
                var id = Read(item, "id");
                if (!string.IsNullOrEmpty(id)) return (id!, Read(item, "name") ?? "");
            }
            return ("", "");
        }

        if (!json.RootElement.TryGetProperty(key, out var profile) || profile.ValueKind != JsonValueKind.Object)
            return ("", "");

        return (Read(profile, "id") ?? "", Read(profile, "name") ?? "");
    }

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Read(JsonDocument document, string name) => Read(document.RootElement, name);

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";
}
