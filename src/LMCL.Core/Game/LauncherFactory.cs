using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;
using LMCL.Core.Java;

namespace LMCL.Core.Game;

/// <summary>
/// 集中构造 <see cref="MinecraftLauncher"/>。
/// 所有下载策略（镜像、并发、超时、Java 复用）都收在这里，别处不再各自拼装。
/// </summary>
public sealed class LauncherFactory
{
    private readonly ConfigService _config;
    private readonly JavaService _java;
    private readonly ILogSink? _log;

    public LauncherFactory(ConfigService config, JavaService java, ILogSink? log = null)
    {
        _config = config;
        _java = java;
        _log = log;
    }

    public HttpClient CreateHttpClient() => HttpClients.Create(_config.UseMirror, _log);

    /// <summary>Mojang Java 运行时清单。走镜像时指向 BMCLAPI（实测该端点可用）。</summary>
    public string JavaManifestServer => _config.UseMirror
        ? MirrorHttpHandler.BmclApi + "/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json"
        : "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>Fabric 元数据地址。null 表示用库里的官方默认值。</summary>
    public string? FabricMetaHost => _config.UseMirror ? MirrorHttpHandler.BmclApi + "/fabric-meta" : null;

    /// <summary>Quilt 没有镜像，始终走官方。</summary>
    public static string QuiltMetaHost => "https://meta.quiltmc.org";

    public MinecraftLauncher Create(MinecraftPath path)
    {
        var http = CreateHttpClient();
        return Create(path, http);
    }

    public MinecraftLauncher Create(MinecraftPath path, HttpClient http)
    {
        var parameters = MinecraftLauncherParameters.CreateDefault(path, http);

        // 并发下载：BMCLAPI 在高并发下会给 429，8 路是实测稳的档位。
        var downloaders = Math.Clamp(_config.Current.DownloadThreads, 1, 32);

        // 外面再套一层去重：资产索引里同一物理对象常被多个名字引用，
        // 不去重的话并行安装必然在同一个文件上「一边写一边校验」而崩掉。
        parameters.GameInstaller = new DedupedGameInstaller(
            new ParallelGameInstaller(
                maxChecker: Math.Min(4, downloaders), maxDownloader: downloaders,
                boundedCapacity: 128, httpClient: http),
            _log);

        // 本机已有合适 Java 时不重复下载 ~45 MB 的 Mojang 运行时。
        var collection = parameters.FileExtractors ?? new FileExtractorCollection();
        var existing = collection.OfType<JavaFileExtractor>().FirstOrDefault();
        if (existing is not null)
        {
            collection.Remove(existing);
            var wrapped = new LocalJavaExtractor(existing, _java, _log) { JavaManifestServer = JavaManifestServer };
            collection.Add(wrapped);
        }
        else
        {
            var resolver = parameters.JavaPathResolver ?? new MinecraftJavaPathResolver(path);
            collection.Add(new LocalJavaExtractor(http, resolver, _java, JavaManifestServer, _log));
        }
        parameters.FileExtractors = collection;

        return new MinecraftLauncher(parameters);
    }
}
