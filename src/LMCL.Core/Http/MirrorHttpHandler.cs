using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace LMCL.Core.Http;

/// <summary>下载源。BMCLAPI 是国内镜像，能把依赖库与资源文件的下载速度拉满。</summary>
public enum DownloadSource
{
    Bmclapi,
    Official,
}

/// <summary>
/// 全局下载管道。三层策略，每一层都有实测依据：
///
/// <b>① 改写</b>：把官方地址按 <see cref="Rules"/> 换成 BMCLAPI 地址。
/// 资源对象、maven jar、Forge/NeoForge 安装器、fabric-meta 在 BMCLAPI 上都直连可用。
///
/// <b>② 例外</b>：<see cref="KeepOfficialPaths"/> 里的路径直奔官方源，一次都不试镜像。
/// 因为这些路径在 BMCLAPI 上必然 302 到限时签名的第三方 CDN，实测数据如下：
///   · <c>version_manifest_v2.json</c>：3 次请求 2 次直接超时（15~22 秒无响应），
///     官方源同一时刻只要 0.9 秒。清单仅 277 KB，留在官方源。
///   · <c>/v1/objects/</c>（客户端 jar、Mojang 运行时）：302 到
///     <c>https://15.749333.xyz:443/download/…?s=…&amp;e=…</c>，实测 38~350 KB/s 且抖动极大；
///     同一个 22 MB 的客户端 jar 走官方源 20 秒就下完，走镜像要爬十几分钟。
///     更阴的地方是它最终会返回 200，所以「失败才回退」这种判断根本触发不了。
///
/// <b>③ 兜底学习</b>：剩下的路径先试镜像；如果发现它把请求转手给了带签名参数的
/// 限时地址（见 <see cref="IsUsable"/>），就改走官方源，并记住这一类路径以后别再试镜像。
/// 首次安装有四千多个资源文件，全靠这一层把 <c>/assets</c> 也拉回官方源。
/// 注意别把「转手」一律当成坏事——<c>/maven</c> 转给的是
/// <c>mirrors.cernet.edu.cn</c> 这种正经镜像节点，那是好的，要照收。
///
/// 最后：任何一步失败都往下退，官方源也拿不到时会退回镜像那个慢地址——
/// 慢总比拿不到强，不能把路走死。
/// </summary>
public sealed class MirrorHttpHandler : DelegatingHandler
{
    public const string BmclApi = "https://bmclapi2.bangbang93.com";

    /// <summary>官方地址前缀 → BMCLAPI 地址前缀。顺序敏感：长前缀必须排在短前缀之前。</summary>
    private static readonly (string From, string To)[] Rules =
    {
        ("https://files.minecraftforge.net/maven", BmclApi + "/maven"),
        ("https://resources.download.minecraft.net", BmclApi + "/assets"),
        ("https://libraries.minecraft.net", BmclApi + "/maven"),
        ("https://maven.minecraftforge.net", BmclApi + "/maven"),
        ("https://maven.neoforged.net/releases", BmclApi + "/maven"),
        ("https://maven.fabricmc.net", BmclApi + "/maven"),
        ("https://meta.fabricmc.net", BmclApi + "/fabric-meta"),
        ("https://piston-meta.mojang.com", BmclApi),
        ("https://piston-data.mojang.com", BmclApi),
        ("https://launcher.mojang.com", BmclApi),
        // launchermeta 只放行 Java 运行时清单；版本清单在 KeepOfficialPaths 里被排除。
        ("https://launchermeta.mojang.com", BmclApi),
    };

    /// <summary>这些路径直奔官方源，一次都不试镜像——见类注释里的实测数据。</summary>
    private static readonly string[] KeepOfficialPaths = { "version_manifest", "/v1/objects" };

    public bool UseMirror { get; set; } = true;

    /// <summary>单次请求的重试次数（仅对无 body 的请求生效）。</summary>
    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    public Action<string>? Log { get; set; }

    /// <summary>
    /// 记住「镜像把这一类请求转手给了限时签名地址」。
    ///
    /// 一旦发现 /assets 这种路径是转手的，后面同类请求就直奔官方源，
    /// 不必每次都先让镜像 302 一次再丢掉——首次安装有四千多个资源文件，那点浪费很可观。
    /// 只记在内存里：重启就重新探测，万一 BMCLAPI 哪天改好了也能自动用回来。
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _delegated = new();

    public MirrorHttpHandler(HttpMessageHandler inner) : base(inner) { }

    /// <summary>取「主机 + 第一段路径」当作分类键，例如 <c>bmclapi2.bangbang93.com/assets</c>。</summary>
    private static string Bucket(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return uri.Host + "/" + (segments.Length > 0 ? segments[0] : "");
    }

    /// <summary>把官方地址换算成镜像地址；不需要/不能改写时返回 null。</summary>
    public static Uri? ToMirror(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

        var withoutQuery = uri.GetLeftPart(UriPartial.Path);

        foreach (var keep in KeepOfficialPaths)
            if (withoutQuery.Contains(keep, StringComparison.OrdinalIgnoreCase))
                return null;

        foreach (var (from, to) in Rules)
        {
            if (!withoutQuery.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = withoutQuery[from.Length..];
            if (!Uri.TryCreate(to + rest + uri.Query, UriKind.Absolute, out var result))
                return null;
            return result;
        }

        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri
            ?? throw new InvalidOperationException("请求缺少 URI");

        var mirrored = UseMirror ? ToMirror(original) : null;
        // 只有无 body 的请求才能安全重发（HttpClient 发送完会释放 Content）。
        var resendable = request.Content is null;

        // 这一类请求已经确认会被转手到限时签名地址，就别再试镜像了
        var skipMirror = mirrored is null || _delegated.ContainsKey(Bucket(mirrored));

        if (mirrored is null || !resendable || skipMirror)
            return await SendWithRetryAsync(request, original, resendable, cancellationToken)
                .ConfigureAwait(false);

        var bucket = Bucket(mirrored);

        // ── ① 先试镜像 ──────────────────────────────────────
        try
        {
            var response = await SendWithRetryAsync(request, mirrored, true, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // 200 也可能是「镜像 302 转手给第三方 CDN」后的结果，得看清楚是谁给的字节。
                if (IsUsable(response))
                    return response;

                Log?.Invoke($"镜像把请求转手给限时地址，改走官方源：{Describe(original)}");
                _delegated[bucket] = true;
                response.Dispose();
            }
            else
            {
                Log?.Invoke($"镜像返回 {(int)response.StatusCode}，回退官方源：{Describe(original)}");
                response.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"镜像请求失败（{ex.GetType().Name}），回退官方源：{Describe(original)}");
        }

        // ── ② 再试官方源 ────────────────────────────────────
        try
        {
            return await SendWithRetryAsync(request, original, true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // ── ③ 官方也拿不到（国内网络很常见）────────────────
            // 那就退回镜像转手的那个地址。慢总比拿不到强，不能把路走死。
            _delegated.TryRemove(bucket, out _);
            Log?.Invoke($"官方源也失败（{ex.GetType().Name}），退回镜像：{Describe(original)}");
            return await SendWithRetryAsync(request, mirrored, true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 最终提供字节的地址可不可信。
    ///
    /// BMCLAPI 有两种 302，处理方式正好相反：
    ///   · 转给正经镜像节点，例如 <c>mirrors.cernet.edu.cn/bmclapi/…</c> —— 好事，接受。
    ///   · 转给限时签名的临时 CDN，例如
    ///     <c>https://15.749333.xyz:443/download/&lt;hash&gt;?s=…&amp;e=…</c>
    ///     —— 实测只有 38~350 KB/s 且抖动极大，同一个文件走官方源能到 3~10 MB/s，拒绝。
    ///
    /// 两者的区别就在 URL 形态：签名地址一定同时带 <c>s=</c>（签名）和 <c>e=</c>（过期时间）。
    /// </summary>
    private static bool IsUsable(HttpResponseMessage response)
    {
        var final = response.RequestMessage?.RequestUri;

        // 拿不到最终地址就当作可用，别因为取不到元信息而白绕一圈
        if (final is null) return true;

        var query = final.Query;
        if (query.Length == 0) return true;

        var signed = query.Contains("s=", StringComparison.OrdinalIgnoreCase)
                     && query.Contains("e=", StringComparison.OrdinalIgnoreCase);

        return !signed;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage template, Uri uri, bool mayRetry, CancellationToken cancellationToken)
    {
        var attempts = mayRetry ? Math.Max(1, MaxAttempts) : 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1 && RetryDelay > TimeSpan.Zero)
                await Task.Delay(RetryDelay * (attempt - 1), cancellationToken).ConfigureAwait(false);

            var current = Clone(template, uri);
            try
            {
                var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);

                if (!IsTransientStatus(response.StatusCode) || attempt == attempts)
                    return response;

                response.Dispose();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;                      // 用户主动取消，不要吞掉
            }
            catch (Exception ex) when (IsTransientError(ex))
            {
                lastError = ex;
                if (attempt == attempts) break;
            }
        }

        throw lastError ?? new HttpRequestException($"请求失败：{uri}");
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source, Uri uri)
    {
        var clone = new HttpRequestMessage(source.Method, uri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };

        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Content 只在「不重发」的路径上出现，此时原样传递即可。
        if (source.Content is not null) clone.Content = source.Content;

        return clone;
    }

    private static bool IsTransientStatus(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout          // 408
             or HttpStatusCode.TooManyRequests         // 429 ← BMCLAPI 高并发时会给这个
        || (int)code >= 500;

    private static bool IsTransientError(Exception ex) =>
        ex is HttpRequestException
           or IOException
           or System.Net.Sockets.SocketException
           or TaskCanceledException                       // 超时（非用户取消）
        || ex.InnerException is System.Net.Sockets.SocketException;

    private static string Describe(Uri uri)
    {
        var text = uri.PathAndQuery;
        return text.Length <= 70 ? text : "…" + text[^69..];
    }
}
