using System.Security.Cryptography;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;

namespace LMCL.Core.Accounts;

/// <summary>发布清单里我们真正用得上的三个字段。</summary>
public sealed record InjectorRelease(string Version, string Url, string Sha256)
{
    public override string ToString() => $"authlib-injector {Version}";
}

/// <summary>
/// 负责把 authlib-injector.jar 弄到手。
///
/// 这个 jar 只有外置登录才用得上，但少了它客户端根本不认第三方验证服务器——
/// 光在版本设置里手填参数对普通用户来说门槛太高，所以这里做成自动的：
/// 本地有就直接用，没有就按当前下载源拉一份，校验完 sha256 才落盘。
///
/// 官方发布站有 BMCLAPI 镜像，实测两边的 download_url 与 sha256 完全一致，
/// 所以走镜像不会降低安全性。
/// </summary>
public sealed class AuthlibInjector
{
    private const string OfficialFeed =
        "https://authlib-injector.yushi.moe/artifact/latest.json";

    private const string MirrorFeed =
        "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";

    private readonly ILogSink? _log;

    public AuthlibInjector(ILogSink? log = null) => _log = log;

    /// <summary>本地已经就位的 jar；没有就是 null。</summary>
    public static string? LocalPath =>
        File.Exists(AppPaths.AuthlibInjectorPath) ? AppPaths.AuthlibInjectorPath : null;

    /// <summary>本地 jar 的落盘时间，用来在界面上给个「是什么时候下的」的交代。</summary>
    public static DateTime? LocalTimestamp =>
        File.Exists(AppPaths.AuthlibInjectorPath)
            ? File.GetLastWriteTime(AppPaths.AuthlibInjectorPath)
            : null;

    /// <summary>读发布清单，拿到最新版本号 / 下载地址 / sha256。</summary>
    public async Task<InjectorRelease?> CheckAsync(bool useMirror, CancellationToken ct = default)
    {
        var feed = useMirror ? MirrorFeed : OfficialFeed;

        try
        {
            using var http = HttpClients.Create(useMirror: false, _log);
            var text = await http.GetStringAsync(feed, ct).ConfigureAwait(false);

            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;

            var version = Text(root, "version");
            var url = Text(root, "download_url");
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return null;

            var sha = "";
            if (root.TryGetProperty("checksums", out var sums) && sums.ValueKind == JsonValueKind.Object)
                sha = Text(sums, "sha256");

            return new InjectorRelease(version, url, sha);
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取 authlib-injector 版本信息失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 确保 jar 就位，返回可用的本地路径；失败返回 null（调用方降级成一句提示）。
    /// force 为 true 时忽略本地已有文件重新下载。
    /// </summary>
    public async Task<string?> EnsureAsync(
        bool useMirror, IProgress<double>? progress = null, bool force = false, CancellationToken ct = default)
    {
        if (!force && LocalPath is { } local)
        {
            _log?.Info($"已有 authlib-injector：{local}");
            return local;
        }

        var release = await CheckAsync(useMirror, ct).ConfigureAwait(false);
        if (release is null) return null;

        var target = AppPaths.AuthlibInjectorPath;
        AppPaths.EnsureCreated();

        try
        {
            using var http = HttpClients.Create(useMirror: false, _log);
            using var response = await http
                .GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;

            // 先落临时文件再改名：中途失败不会留下半个 jar 冒充好文件
            var temp = target + ".downloading";
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = File.Create(temp))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total);
                }
            }

            if (!string.IsNullOrEmpty(release.Sha256) && !Verify(temp, release.Sha256))
            {
                File.Delete(temp);
                _log?.Error("authlib-injector 校验失败，文件已丢弃");
                return null;
            }

            File.Move(temp, target, overwrite: true);
            _log?.Success($"authlib-injector {release.Version} 已就位：{target}");
            return target;
        }
        catch (Exception ex)
        {
            _log?.Warn($"下载 authlib-injector 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>拼出 -javaagent 那一段。server 必须是 authlib-injector 规范要求的、以 / 结尾的地址。</summary>
    public static string BuildJavaAgent(string jarPath, string server)
    {
        var address = (server ?? "").Trim();
        if (address.Length == 0) throw new InvalidOperationException("缺少验证服务器地址");
        if (!address.EndsWith('/')) address += "/";

        return $"-javaagent:{jarPath}={address}";
    }

    private static bool Verify(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
