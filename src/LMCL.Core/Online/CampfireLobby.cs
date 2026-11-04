using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LMCL.Core.Online;

/// <summary>
/// 房间邀请码，格式 <c>U/XXXX-XXXX-XXXX-XXXX</c>。
///
/// 编解码规则与 Campfire（GPL-3.0，https://github.com/Xocio/Campfire）的公开邀请码格式
/// 完全一致，这样 LMCL 建的房间和 Campfire 用户能互相加入：
/// 16 个 base-34 字符（去掉易混淆的 I / O）按低位在前排列，整体是 7 的倍数作校验；
/// 前 8 位是 EasyTier 网络名（scaffolding-mc-XXXX-XXXX），后 8 位是网络密码。
/// </summary>
public sealed class RoomCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string NetworkPrefix = "scaffolding-mc-";
    private const int DigitCount = 16;
    private const int Checksum = 7;
    private const int TextLength = 21; // "U/XXXX-XXXX-XXXX-XXXX"

    private static readonly BigInteger Range = BigInteger.Pow(Alphabet.Length, DigitCount);

    private RoomCode(BigInteger value)
    {
        var digits = new char[DigitCount];
        for (var i = 0; i < DigitCount; i++)
        {
            digits[i] = Alphabet[(int)(value % Alphabet.Length)];
            value /= Alphabet.Length;
        }

        var code = new StringBuilder("U/", TextLength);
        for (var i = 0; i < DigitCount; i++)
        {
            if (i > 0 && i % 4 == 0) code.Append('-');
            code.Append(digits[i]);
        }

        Code = code.ToString();
        NetworkName = NetworkPrefix + Group(digits, 0) + "-" + Group(digits, 4);
        NetworkSecret = Group(digits, 8) + "-" + Group(digits, 12);
    }

    /// <summary>展示给玩家的邀请码，如 U/1A2B-3C4D-5E6F-7G8H。</summary>
    public string Code { get; }

    /// <summary>邀请码对应的 EasyTier 网络名。</summary>
    public string NetworkName { get; }

    /// <summary>邀请码对应的 EasyTier 网络密码。</summary>
    public string NetworkSecret { get; }

    public override string ToString() => Code;

    /// <summary>为新房间生成一个随机邀请码。</summary>
    public static RoomCode Create()
    {
        // 末尾补一个 0 字节保证 little-endian 数值为正
        var bytes = new byte[17];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes, 0, 16);

        var value = new BigInteger(bytes) % Range;
        return new RoomCode(value - value % Checksum);
    }

    /// <summary>
    /// 从一段文本里找出第一个合法邀请码（可以整段聊天记录直接粘进来）。
    /// 忽略大小写；长得像的 I / O 按 1 / 0 读。
    /// </summary>
    public static RoomCode? Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var upper = text.ToUpperInvariant();
        for (var start = 0; start + TextLength <= upper.Length; start++)
        {
            if (upper[start] == 'U' && upper[start + 1] == '/' && TryDecode(upper, start + 2, out var value))
                return new RoomCode(value);
        }
        return null;
    }

    private static bool TryDecode(string text, int offset, out BigInteger value)
    {
        value = BigInteger.Zero;

        // "XXXX-XXXX-XXXX-XXXX" 的最高位在末尾，所以倒着解
        for (var i = TextLength - 3; i >= 0; i--)
        {
            var c = text[offset + i];
            if (i == 4 || i == 9 || i == 14)
            {
                if (c != '-') return false;
                continue;
            }

            var digit = DigitOf(c);
            if (digit < 0) return false;
            value = value * Alphabet.Length + digit;
        }
        return value % Checksum == 0;
    }

    private static int DigitOf(char c)
    {
        if (c == 'I') c = '1';
        else if (c == 'O') c = '0';
        return Alphabet.IndexOf(c);
    }

    private static string Group(char[] digits, int start) => new(digits, start, 4);
}

/// <summary>大厅里的一间房（所有人可见的样子；邀请码只在加入时下发）。</summary>
public sealed record LobbyRoom(
    string Id, string Title, string Host, string Version, string Map,
    IReadOnlyList<string> Tags, int Players, int MaxPlayers,
    bool Locked, bool Voice, string Description);

/// <summary>大厅清单：房间列表 + 服务端公告 + 建议刷新间隔。</summary>
public sealed record LobbyList(
    bool Enabled, string Notice, int RefreshSeconds,
    IReadOnlyList<string> Tags, IReadOnlyList<LobbyRoom> Rooms);

/// <summary>
/// Campfire 公共联机大厅（https://api.mccampfire.top）的客户端。
///
/// 协议照 Campfire（GPL-3.0）公开源码里的 LobbyClient 实现并保持互通：
/// 列房间、发布房间、心跳续期、下架、换取邀请码。LMCL 以 AGPL-3.0 开源，
/// 与 GPLv3 兼容；此处为独立实现，仅复用其公开 API 与邀请码格式。
/// </summary>
public static class CampfireLobby
{
    public const string BaseUrl = "https://api.mccampfire.top";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LMCL/{AppInfo.Version}");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>发布房间用的匿名标识（随机生成、落盘复用；与服务端统计无关）。</summary>
    public static string OwnerId()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LMCL");
            var file = Path.Combine(dir, "lobby-id");
            if (File.Exists(file))
            {
                var saved = File.ReadAllText(file).Trim();
                if (saved.Length == 32 && saved.All(Uri.IsHexDigit)) return saved;
            }
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            // 落盘失败就用一次性 id，功能不受影响
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }
    }

    /// <summary>拉取公共房间列表。</summary>
    public static async Task<LobbyList> ListAsync(CancellationToken ct = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "/v1/lobby", null, ct).ConfigureAwait(false);

        var rooms = new List<LobbyRoom>();
        if (json.RootElement.TryGetProperty("rooms", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in list.EnumerateArray())
            {
                var id = Str(r, "id");
                var title = Str(r, "title");
                if (id.Length == 0 || title.Length == 0) continue;

                rooms.Add(new LobbyRoom(
                    id, title, Str(r, "host"), Str(r, "version"), Str(r, "map"),
                    Strs(r, "tags"),
                    Math.Max(1, Int(r, "players", 1)), Int(r, "maxPlayers", 0),
                    Bool(r, "locked"), Bool(r, "voice"), Str(r, "description")));
            }
        }

        var tags = json.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();

        return new LobbyList(
            GetBool(json.RootElement, "enabled", true),
            Str(json.RootElement, "notice"),
            Math.Max(5, Int(json.RootElement, "refreshSeconds", 30)),
            tags, rooms);
    }

    /// <summary>加入大厅里的房间：换取该房间的邀请码（带锁的房间要密码）。</summary>
    public static async Task<RoomCode> GetCodeAsync(string roomId, string? password, CancellationToken ct = default)
    {
        var body = string.IsNullOrEmpty(password)
            ? null
            : new Dictionary<string, object> { ["password"] = password };

        using var json = await SendAsync(HttpMethod.Post, $"/v1/lobby/rooms/{roomId}/join", body, ct)
            .ConfigureAwait(false);

        return RoomCode.Find(Str(json.RootElement, "code"))
               ?? throw new InvalidOperationException("大厅没有返回这个房间的邀请码。");
    }

    /// <summary>把房间挂上大厅，返回后续心跳 / 下架要用的 (id, token)。password 非空 = 房间上锁，加入时要密码。</summary>
    public static async Task<(string Id, string Token)> PublishAsync(
        RoomCode code, string title, string host, string version, string map,
        int maxPlayers, string description, bool showRegion = true,
        string? password = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["owner"] = OwnerId(),
            ["app"] = AppInfo.Version,
            ["code"] = code.Code,
            ["title"] = title,
            ["host"] = host,
            ["version"] = version ?? "",
            ["map"] = map ?? "",
            ["tags"] = Array.Empty<string>(),
            ["players"] = 1,
            ["maxPlayers"] = maxPlayers,
            ["showRegion"] = showRegion,
            ["description"] = description ?? "",
            ["voice"] = false,
        };
        // 照 Campfire 的协议：密码非空才带这个字段
        if (!string.IsNullOrEmpty(password))
            body["password"] = password;

        using var json = await SendAsync(HttpMethod.Post, "/v1/lobby/rooms", body, ct).ConfigureAwait(false);

        var id = Str(json.RootElement, "id");
        var token = Str(json.RootElement, "token");
        if (id.Length == 0 || token.Length == 0)
            throw new InvalidOperationException("大厅接受了房间但没有返回凭据。");
        return (id, token);
    }

    /// <summary>心跳续期；服务端多久不收到心跳就把房间摘掉，返回建议的下次间隔（秒）。</summary>
    public static async Task<int> HeartbeatAsync(string id, string token, int players, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["players"] = players, ["maxPlayers"] = 0, ["voice"] = false };
        using var json = await SendAsync(HttpMethod.Put, $"/v1/lobby/rooms/{id}", body, ct, token).ConfigureAwait(false);
        return Math.Min(Math.Max(Int(json.RootElement, "heartbeatSeconds", 240), 30), 3600);
    }

    /// <summary>把房间从大厅下架；已经不在了也算成功。</summary>
    public static async Task CloseAsync(string id, string token, CancellationToken ct = default)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"/v1/lobby/rooms/{id}", null, ct, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // 下架失败不该拦住退出 —— 大厅的心跳超时自己会把它摘掉
        }
    }

    // ── HTTP 底座 ───────────────────────────────────────────

    private static async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, Dictionary<string, object>? body,
        CancellationToken ct, string? token = null)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException) { doc = JsonDocument.Parse("{}"); }

        if (response.IsSuccessStatusCode) return doc;

        var error = Str(doc.RootElement, "error");
        doc.Dispose();
        throw new InvalidOperationException(Describe((int)response.StatusCode, error));
    }

    private static string Describe(int status, string error) => status switch
    {
        401 => "房间密码不对，或大厅要求先填密码。",
        403 => "大厅拒绝了这次请求（可能被限制）。",
        404 => "这个房间已经不在大厅里了。",
        409 => "这个邀请码已经被别的房间挂在大厅里了。",
        426 => "大厅要求更新启动器才能继续使用。",
        429 => "请求太频繁，等一会儿再试。",
        503 => "大厅暂时关闭或已满。",
        _ => $"联机大厅返回 HTTP {status}" + (error.Length > 0 ? $"（{error}）" : ""),
    };

    // ── JSON 小工具 ─────────────────────────────────────────

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name, int fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static bool Bool(JsonElement e, string name) => GetBool(e, name, false);

    private static bool GetBool(JsonElement e, string name, bool fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static IReadOnlyList<string> Strs(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }
}

/// <summary>当前启动器版本（读自程序集，避免在 Core 里再存一份常量）。</summary>
internal static class AppInfo
{
    public static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? v.ToString(3)
            : "0.0.0";
}
