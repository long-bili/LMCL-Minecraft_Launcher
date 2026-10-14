using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Accounts;

/// <summary>
/// 本地皮肤中继（loopback 上的一个小 HTTP 服务）。
///
/// ── 为什么需要它 ──────────────────────────────────────────
/// Minecraft 客户端**只认 Mojang 的验证服务器**：进游戏后它会去
/// sessionserver 拉这个 UUID 的档案，从档案里拿皮肤纹理地址。
/// 离线账号压根没有档案，所以无论启动器里选哪张皮肤，游戏里永远是默认的
/// Steve / Alex —— 这正是「启动器切换了但游戏里没变」的原因。
///
/// ── 怎么骗过客户端 ────────────────────────────────────────
/// 用 authlib-injector 这个 javaagent 把客户端的 Yggdrasil 请求**改写到我们指定的地址**。
/// 于是在本机起一个回环 HTTP 服务，让它冒充一套 Yggdrasil 验证服务：
///   · 提供 <c>/</c>：宣告公钥与 skinDomains（authlib-injector 的规范要求）
///   · 提供 <c>/sessionserver/session/minecraft/profile/&lt;uuid&gt;</c>：
///     返回一份**用私钥签名**的档案，textures 属性里指向我们自己托管的皮肤 PNG
///   · 提供皮肤 PNG 本身
/// 客户端验签通过后就会把这张皮肤贴到玩家身上。
///
/// 签名用的 RSA 私钥持久化在启动器目录下，第一次生成、之后一直复用
/// （每次换密钥都要重新让客户端认一遍，没必要）。
/// </summary>
public sealed class SkinRelay : IDisposable
{
    /// <summary>客户端请求的档案路径前缀。</summary>
    private const string ProfilePrefix = "/sessionserver/session/minecraft/profile/";

    private static readonly JsonSerializerOptions Json = new()
    {
        // 纹理 blob 里的字段名必须是 Mojang 的原样拼写，不能改大小写
        PropertyNamingPolicy = null,
    };

    private readonly HttpListener _http = new();
    private readonly RSA _key;
    private readonly byte[] _skinPng;
    private readonly string _skinPath;
    private readonly string _uuidCompact;
    private readonly string _userName;
    private readonly bool _slim;
    private readonly ILogSink? _log;

    private bool _disposed;

    private SkinRelay(
        RSA key, string uuid, string userName, byte[] skinPng, bool slim, int port, ILogSink? log)
    {
        _key = key;
        _userName = userName.Trim();
        _uuidCompact = uuid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        _skinPng = skinPng;
        _slim = slim;
        _log = log;

        // 用内容哈希当文件名：同一张皮肤永远同一个地址，客户端可以放心缓存
        var token = Convert.ToHexString(SHA256.HashData(skinPng))[..20].ToLowerInvariant();
        _skinPath = $"/lmcl/skin/{token}.png";

        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _http.Prefixes.Add(BaseUri.AbsoluteUri);
    }

    public int Port { get; }

    public Uri BaseUri { get; }

    /// <summary>给 -javaagent 用的地址（authlib-injector 要求以 / 结尾）。</summary>
    public string InjectorAddress => BaseUri.GetLeftPart(UriPartial.Authority) + "/";

    /// <summary>
    /// 起一个中继。<paramref name="skinPngPath"/> 必须是一个存在且可读的 PNG。
    /// 任何一步失败都返回 null，调用方降级成「不带皮肤启动」而不是让启动失败。
    /// </summary>
    public static SkinRelay? TryCreate(
        string uuid, string userName, string skinPngPath, bool slim, ILogSink? log = null)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(userName)) return null;
        if (!File.Exists(skinPngPath)) return null;

        try
        {
            var png = File.ReadAllBytes(skinPngPath);
            if (png.Length <= 64) return null;

            var port = ReserveLoopbackPort();
            var key = LoadOrCreateKey(log);
            var relay = new SkinRelay(key, uuid, userName, png, slim, port, log);
            relay.Start();
            return relay;
        }
        catch (Exception ex)
        {
            log?.Warn($"本地皮肤中继启动失败：{ex.Message}");
            return null;
        }
    }

    public void Start()
    {
        _http.Start();

        // 接客循环本身的任务要存住：它是 fire-and-forget 的，
        // 万一里面漏出异常没人接，终结器线程会把它当「未观察异常」再抛一次，
        // 表现就是主界面弹一个 AggregateException 崩溃框。
        _accept = Task.Run(AcceptAsync);

        _log?.Info($"本地皮肤中继已在 {BaseUri} 启动（{_userName}）");
    }

    /// <summary>接客循环的任务。只为在 Dispose 时观察它的异常，别让终结器捡到。</summary>
    private Task? _accept;

    /// <summary>
    /// 等下一个请求。
    ///
    /// 这里**直接 await GetContextAsync()**，不能用 <c>WaitAsync(token)</c> 那种写法：
    /// 后者在取消时只是放弃等待，**内层的 GetContextAsync 任务还挂着没人管**；
    /// 等 Dispose 里 Close() 一调，那个孤儿任务就带着 ObjectDisposedException 结束，
    /// 而它的异常没人观察 —— 终结器线程会把它重新抛出来，
    /// 于是「游戏一退出，主界面就爆一个 AggregateException」。
    /// 直接 await 就没这问题：Close() 让这个 await 抛出，我们在下面接住，异常被观察掉了。
    /// </summary>
    private async Task AcceptAsync()
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                context = await _http.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }      // Stop()/Close() 的正常收尾
            catch (ObjectDisposedException) { break; }    // Close() 之后的收尾
            catch (InvalidOperationException) { break; }  // 监听器没起来
            catch (OperationCanceledException) { break; }

            _ = Task.Run(() => DispatchAsync(context), CancellationToken.None);
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod ?? "GET";

            // 把每个进来的请求都记一条：排查「游戏里皮肤没变」时，
            // 第一件要确认的就是客户端到底有没有来问过我们。
            // 一条都没有 = 客户端压根没查档案（那就不是中继的问题）；
            // 有请求但皮肤没变 = 返回值没被接受，照着路径继续查。
            _log?.Info($"[皮肤中继] {method} {path}");

            if (method != "GET")
            {
                await StatusAsync(context, 405).ConfigureAwait(false);
                return;
            }

            // 根文档：authlib-injector 靠它拿公钥和允许的皮肤域名
            if (path is "/" or "")
            {
                await JsonAsync(context, BuildRoot()).ConfigureAwait(false);
                return;
            }

            // 档案查询：客户端启动时要的就是这个
            if (path.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                var id = path[ProfilePrefix.Length..];
                if (string.Equals(id, _uuidCompact, StringComparison.OrdinalIgnoreCase))
                    await JsonAsync(context, BuildProfile()).ConfigureAwait(false);
                else
                    await StatusAsync(context, 204).ConfigureAwait(false);
                return;
            }

            // 皮肤 PNG 本体
            if (string.Equals(path, _skinPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/png";
                context.Response.Headers["Cache-Control"] = "public, max-age=604800";
                context.Response.ContentLength64 = _skinPng.Length;
                await context.Response.OutputStream.WriteAsync(_skinPng).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            await StatusAsync(context, 404).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"皮肤中继处理请求失败：{ex.Message}");
            try { context.Response.Abort(); } catch { /* 已经断了就算了 */ }
        }
    }

    /// <summary>根文档：公钥 + 允许的皮肤域名。</summary>
    private Dictionary<string, object?> BuildRoot() => new()
    {
        ["signaturePublickey"] = ExportPublicKey(_key),
        // authlib-injector 只允许把皮肤指向这些域名下的地址
        ["skinDomains"] = new[] { "127.0.0.1", "localhost" },
        ["meta"] = new Dictionary<string, object?>
        {
            ["serverName"] = "LMCL 本地皮肤",
            ["implementationName"] = "lmcl-skin-relay",
            ["implementationVersion"] = "2.0.1",
        },
    };

    /// <summary>
    /// 伪造的档案：id / name / properties，properties 里那条 textures
    /// 是 base64 的 JSON，并且带 RSA-SHA1 签名（客户端会验）。
    /// </summary>
    private Dictionary<string, object?> BuildProfile()
    {
        var skin = new Dictionary<string, object?>
        {
            ["url"] = $"{BaseUri.GetLeftPart(UriPartial.Authority)}{_skinPath}",
        };

        if (_slim)
            skin["metadata"] = new Dictionary<string, object?> { ["model"] = "slim" };

        var blob = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = _uuidCompact,
            ["profileName"] = _userName,
            ["textures"] = new Dictionary<string, object?> { ["SKIN"] = skin },
        };

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blob)));
        var signature = Convert.ToBase64String(_key.SignData(
            Encoding.UTF8.GetBytes(encoded), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));

        return new Dictionary<string, object?>
        {
            ["id"] = _uuidCompact,
            ["name"] = _userName,
            ["properties"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "textures",
                    ["value"] = encoded,
                    ["signature"] = signature,
                },
            },
        };
    }

    /// <summary>占一个空闲的回环端口：先绑 0 让系统挑，拿到号再放掉。</summary>
    private static int ReserveLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>签名密钥持久化在启动器目录下，第一次生成、以后复用。</summary>
    private static RSA LoadOrCreateKey(ILogSink? log)
    {
        var path = AppPaths.SkinRelayKeyFile;

        var rsa = RSA.Create(2048);
        try
        {
            if (File.Exists(path))
            {
                rsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                return rsa;
            }
        }
        catch (Exception ex)
        {
            // 密钥文件坏了就重新生成一份：皮肤还能用，只是客户端要重新认一次
            log?.Warn($"皮肤中继密钥读取失败，将重新生成：{ex.Message}");
            rsa.Dispose();
            rsa = RSA.Create(2048);
        }

        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllBytes(path, rsa.ExportPkcs8PrivateKey());
        }
        catch (Exception ex)
        {
            // 存不下也不致命，本次运行照常能用
            log?.Warn($"皮肤中继密钥保存失败（本次仍可用）：{ex.Message}");
        }

        return rsa;
    }

    /// <summary>导出 PEM 格式公钥（authlib-injector 的规范要求就是这个格式）。</summary>
    private static string ExportPublicKey(RSA rsa)
    {
        var base64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var text = new StringBuilder(base64.Length + 64);
        text.Append("-----BEGIN PUBLIC KEY-----\n");
        for (var i = 0; i < base64.Length; i += 64)
            text.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        text.Append("-----END PUBLIC KEY-----");

        return text.ToString();
    }

    private static async Task JsonAsync(HttpListenerContext context, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static Task StatusAsync(HttpListenerContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.Close();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先让监听器停下来，正在 await 的 GetContextAsync 就会抛出来被 AcceptAsync 接住
        try { _http.Stop(); } catch { /* 已经停了 */ }
        try { _http.Close(); } catch { /* 已经停了 */ }

        // **必须观察接客任务的异常**：它是 fire-and-forget 起来的，
        // 只要异常没被观察过，终结器线程就会把它重新抛成 AggregateException，
        // 用户看到的是「游戏退出后主界面弹一个未处理的错误」。
        // Wait 会把异常取出来（哪怕它抛出来），这样就算观察过了。
        try { _accept?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* 已经观察掉，收尾阶段不需要再报 */ }
        catch { /* 同上 */ }

        _key.Dispose();
    }
}
