using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Files;
using CmlLib.Core.Java;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Java;

/// <summary>
/// 包住库自带的 <see cref="JavaFileExtractor"/>，加一条短路：
/// 本机已经有满足该版本要求的 Java 时，就不再往游戏目录里下一份 ~45 MB 的 Mojang 运行时。
///
/// 只在「本机确定有合适 Java」时才跳过——其它情况一律交给原实现，
/// 保证缺 Java 的机器仍然能自动补上运行时。
/// </summary>
public sealed class LocalJavaExtractor : IFileExtractor
{
    private readonly JavaFileExtractor _inner;
    private readonly string? _javaManifestServer;
    private readonly JavaService _java;
    private readonly ILogSink? _log;

    public LocalJavaExtractor(JavaFileExtractor inner, JavaService java, ILogSink? log = null)
    {
        _inner = inner;
        _java = java;
        _log = log;
    }

    public LocalJavaExtractor(
        HttpClient httpClient, IJavaPathResolver javaPathResolver, JavaService java,
        string? javaManifestServer = null, ILogSink? log = null)
    {
        _inner = new JavaFileExtractor(httpClient, javaPathResolver);
        _java = java;
        _javaManifestServer = javaManifestServer;
        _log = log;
    }

    public string? JavaManifestServer
    {
        get => _inner.JavaManifestServer;
        // 库那边的属性是不可空的；null 传进去会被它当成空串，行为一致，这里显式归一
        set => _inner.JavaManifestServer = value ?? "";
    }

    public async ValueTask<IEnumerable<GameFile>> Extract(
        MinecraftPath path, IVersion version, RulesEvaluatorContext rulesContext,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_javaManifestServer))
            _inner.JavaManifestServer = _javaManifestServer!;

        // Java 列表可能是冷的——比如刚启动就点安装，或者命令行直接跑安装步骤。
        // 冷缓存会让 Best() 返回 null，于是明明本机有合适的 Java 也要白下 45 MB 运行时。
        // 先扫一次盘（结果会缓存住，代价只付一次）再判断。
        if (_java.Cached.Count == 0)
            await _java.DetectAsync(ct: cancellationToken).ConfigureAwait(false);

        var required = JavaService.ParseMajor(version.JavaVersion?.MajorVersion);
        var best = _java.Best(required > 0 ? required : null);

        if (best is not null && (required <= 0 || best.Major >= required))
        {
            _log?.Debug($"本机已有 Java {best.Major}（需要 {(required > 0 ? required.ToString() : "任意")}），跳过运行时下载");
            return Enumerable.Empty<GameFile>();
        }

        return await _inner.Extract(path, version, rulesContext, cancellationToken).ConfigureAwait(false);
    }
}
