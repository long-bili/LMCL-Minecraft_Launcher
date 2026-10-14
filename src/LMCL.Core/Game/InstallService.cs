extern alias forge;
extern alias neoforge;

using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.VersionLoader;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;
using LMCL.Core.Java;

using ForgeInstaller = forge::CmlLib.Core.Installer.Forge.ForgeInstaller;
using ForgeInstallOptions = forge::CmlLib.Core.Installer.Forge.ForgeInstallOptions;
using NeoForgeInstaller = neoforge::CmlLib.Core.Installer.NeoForge.NeoForgeInstaller;
using NeoForgeInstallOptions = neoforge::CmlLib.Core.Installer.NeoForge.Installers.NeoForgeInstallOptions;

namespace LMCL.Core.Game;

public sealed record RemoteVersion(string Name, string Type, DateTimeOffset ReleaseTime)
{
    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string ReleaseText => ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");
}

public sealed record LoaderVersion(string Version, bool Recommended)
{
    public string Label => Recommended ? Version + "  （推荐）" : Version;
}

/// <summary>原版与各类加载器的下载安装。</summary>
public sealed class InstallService
{
    private const string OfficialManifest =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly ConfigService _config;
    private readonly JavaService _java;
    private readonly LauncherFactory _factory;
    private readonly ILogSink? _log;

    public InstallService(ConfigService config, JavaService java, LauncherFactory factory, ILogSink? log = null)
    {
        _config = config;
        _java = java;
        _factory = factory;
        _log = log;
    }

    private string GameDir => _config.GameDir;

    // ── 原版 ────────────────────────────────────────────────

    /// <summary>拉取官方版本清单。失败时回退到本地缓存（库里自带这个能力）。</summary>
    public async Task<IReadOnlyList<RemoteVersion>> GetRemoteVersionsAsync(CancellationToken ct = default)
    {
        using var http = _factory.CreateHttpClient();
        var loader = new MojangJsonVersionLoaderV2(GamePaths.Shared(GameDir), http, OfficialManifest)
        {
            UseLocalManifestWhenError = true,
        };

        var collection = await loader.GetVersionMetadatasAsync(ct).ConfigureAwait(false);
        return collection
            .Select(m => new RemoteVersion(m.Name, m.Type ?? "", m.ReleaseTime))
            .OrderByDescending(v => v.ReleaseTime)
            .ToList();
    }

    public async Task InstallAsync(string version, ProgressReporter reporter, CancellationToken ct = default)
    {
        reporter.Info($"开始安装原版 {version}");
        reporter.Status($"正在下载 Minecraft {version}…");

        var path = GamePaths.Shared(GameDir);
        using var http = _factory.CreateHttpClient();
        var launcher = _factory.Create(path, http);

        await ResilientInstaller.InstallAsync(launcher, version, reporter, _log, ct).ConfigureAwait(false);

        reporter.Info($"原版 {version} 安装完成");
    }

    // ── 加载器 ──────────────────────────────────────────────

    public async Task<string> InstallLoaderAsync(
        LoaderKind kind, string mcVersion, string? loaderVersion,
        ProgressReporter reporter, CancellationToken ct = default)
    {
        var label = LoaderLabels.Of(kind);
        reporter.Info($"开始安装 {label} · 游戏版本 {mcVersion}");
        reporter.Status($"正在安装 {label}…");

        var path = GamePaths.Shared(GameDir);
        using var http = _factory.CreateHttpClient();
        var launcher = _factory.Create(path, http);

        // 先把原版备齐：加载器安装器要基于它工作，而且这一步会顺带
        // 在缺 Java 的机器上把 Mojang 运行时下下来（安装器需要 Java 才能跑处理器）。
        await ResilientInstaller.InstallAsync(launcher, mcVersion, reporter, _log, ct).ConfigureAwait(false);
        await _java.DetectAsync(force: true, ct).ConfigureAwait(false);

        var installed = kind switch
        {
            LoaderKind.Forge => await InstallForgeAsync(launcher, http, mcVersion, loaderVersion, reporter, ct),
            LoaderKind.NeoForge => await InstallNeoForgeAsync(launcher, http, mcVersion, loaderVersion, reporter, ct),
            LoaderKind.Fabric => await InstallFabricAsync(launcher, http, mcVersion, loaderVersion, reporter, ct),
            LoaderKind.Quilt => await InstallQuiltAsync(launcher, http, mcVersion, loaderVersion, reporter, ct),
            _ => throw new NotSupportedException($"{label} 暂不支持自动安装"),
        };

        // 加载器版本按全局设置决定开不开版本隔离（默认开，避免模组互相污染）
        if (!File.Exists(Path.Combine(GamePaths.VersionDir(GameDir, installed), GamePaths.InstanceFileName)))
        {
            var settings = new InstanceSettings { Isolated = _config.Current.DefaultIsolate };
            GamePaths.SaveInstance(GameDir, installed, settings);
            GamePaths.EnsureVersionDirs(GameDir, installed, _config.Current.DefaultIsolate);
        }

        reporter.Success($"{label} 安装完成：{installed}");
        return installed;
    }

    // ── 实例 ────────────────────────────────────────────────
    //
    // 加载器安装器按自己的规矩给版本目录起名（forge-47.4.27-1.20.1 之类），
    // 但整合包需要「实例名 = 整合包名」。所以这里补两个操作：改名与建空实例。
    // 注意顺序：必须先装加载器再改名，最后才把整合包文件解进去——
    // 反过来的话目标目录已经存在，改名会被安全地拒绝。

    /// <summary>装加载器，并把产出的版本目录改名成指定实例名。</summary>
    public async Task<string> InstallLoaderAsAsync(
        LoaderKind kind, string mcVersion, string? loaderVersion, string instanceName,
        ProgressReporter reporter, CancellationToken ct = default)
    {
        var installed = await InstallLoaderAsync(kind, mcVersion, loaderVersion, reporter, ct)
            .ConfigureAwait(false);

        if (string.Equals(installed, instanceName, StringComparison.OrdinalIgnoreCase))
            return installed;

        return RenameVersion(installed, instanceName) ? instanceName : installed;
    }

    /// <summary>
    /// 把 versions/&lt;from&gt; 整体改成 versions/&lt;to&gt;。
    /// 目录名与 json 文件名必须一致，CmlLib 是拿版本名去找 &lt;name&gt;/&lt;name&gt;.json 的。
    /// </summary>
    public bool RenameVersion(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return false;

        var source = GamePaths.VersionDir(GameDir, from);
        var target = GamePaths.VersionDir(GameDir, to);
        if (!Directory.Exists(source) || Directory.Exists(target)) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);

            var oldJson = Path.Combine(target, from + ".json");
            var newJson = Path.Combine(target, to + ".json");
            if (File.Exists(oldJson)) File.Move(oldJson, newJson, overwrite: true);

            _log?.Info($"版本 {from} 已改名为 {to}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn($"版本改名失败（{from} → {to}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 以某个已装版本为父本，建一个空的隔离实例。
    ///
    /// 整合包如果没有加载器依赖，直接装原版再把文件解进去会污染原版目录；
    /// 这里写一个只声明 inheritsFrom 的版本 JSON，让客户端自己去继承父版本的一切，
    /// 而 mods / config 就落在实例自己的目录里。
    /// </summary>
    public string CreateInstance(string baseVersion, string instanceName)
    {
        if (!File.Exists(GamePaths.VersionJson(GameDir, baseVersion)))
            throw new InvalidOperationException($"父版本 {baseVersion} 还没安装，无法创建实例");

        var dir = GamePaths.VersionDir(GameDir, instanceName);
        Directory.CreateDirectory(dir);

        // 只写必需的两个字段，其余全部继承
        var json = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["id"] = instanceName,
                ["inheritsFrom"] = baseVersion,
            },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dir, instanceName + ".json"), json);

        // 整合包的实例也按全局设置决定隔离——它本来就是「独立一个包」的语义
        var isolate = _config.Current.DefaultIsolate;
        GamePaths.SaveInstance(GameDir, instanceName, new InstanceSettings { Isolated = isolate });
        GamePaths.EnsureVersionDirs(GameDir, instanceName, isolate);

        _log?.Info($"已创建隔离实例 {instanceName}（基于 {baseVersion}）");
        return instanceName;
    }

    /// <summary>把名字收拾成可做目录名的形式。</summary>
    public static string SanitizeInstanceName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        cleaned = cleaned.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "新实例" : cleaned;
    }

    private async Task<string> InstallForgeAsync(
        MinecraftLauncher launcher, HttpClient http, string mcVersion, string? loaderVersion,
        ProgressReporter reporter, CancellationToken ct)
    {
        var installer = new ForgeInstaller(launcher, http);
        var options = CreateForgeOptions(await PickInstallerJavaAsync(launcher, mcVersion, ct), reporter, ct);

        if (!string.IsNullOrWhiteSpace(loaderVersion))
            return await installer.Install(mcVersion, loaderVersion!, options).ConfigureAwait(false);

        var versions = await installer.GetForgeVersions(mcVersion).ConfigureAwait(false);
        var recommended = versions.FirstOrDefault(v => v.IsRecommendedVersion)
                          ?? versions.FirstOrDefault(v => v.IsLatestVersion)
                          ?? versions.FirstOrDefault();
        if (recommended is null) throw new InvalidOperationException($"Forge 不支持 {mcVersion}");

        reporter.Info($"选用 Forge {recommended.ForgeVersionName}");
        return await installer.Install(mcVersion, recommended.ForgeVersionName, options).ConfigureAwait(false);
    }

    private async Task<string> InstallNeoForgeAsync(
        MinecraftLauncher launcher, HttpClient http, string mcVersion, string? loaderVersion,
        ProgressReporter reporter, CancellationToken ct)
    {
        // 用专门的 NeoForge 包（4.x，和 CmlLib.Core 4.x 同代）。
        // 注意：这个包的 NeoForgeInstaller 只有单参构造函数，列版本的方法叫 GetForgeVersions。
        var installer = new NeoForgeInstaller(launcher);
        var options = new NeoForgeInstallOptions
        {
            JavaPath = await PickInstallerJavaAsync(launcher, mcVersion, ct),
            FileProgress = reporter,
            ByteProgress = reporter,
            InstallerOutput = new Progress<string>(line => reporter.Info("  " + line)),
            CancellationToken = ct,
            SkipIfAlreadyInstalled = true,
        };

        if (!string.IsNullOrWhiteSpace(loaderVersion))
            return await installer.Install(mcVersion, loaderVersion!, options).ConfigureAwait(false);

        // 版本列表走镜像的 maven-metadata（库自带的加载器打的是没被镜像的 /api/maven）
        var available = await NeoForgeCatalog.VersionsAsync(http, mcVersion, ct).ConfigureAwait(false);
        var newest = available.FirstOrDefault();
        if (newest is null)
            throw new InvalidOperationException(
                NeoForgeCatalog.ExplainMissing(mcVersion) is { } why
                    ? $"NeoForge 不支持 {mcVersion}：{why}"
                    : $"NeoForge 没有 {mcVersion} 的构建");

        reporter.Info($"选用 NeoForge {newest}");
        return await installer.Install(mcVersion, newest, options).ConfigureAwait(false);
    }

    private async Task<string> InstallFabricAsync(
        MinecraftLauncher launcher, HttpClient http, string mcVersion, string? loaderVersion,
        ProgressReporter reporter, CancellationToken ct)
    {
        var fabric = _factory.FabricMetaHost is { } host
            ? new FabricInstaller(http, host)
            : new FabricInstaller(http);

        var name = string.IsNullOrWhiteSpace(loaderVersion)
            ? await fabric.Install(mcVersion, launcher.MinecraftPath).ConfigureAwait(false)
            : await fabric.Install(mcVersion, loaderVersion!, launcher.MinecraftPath).ConfigureAwait(false);

        // Fabric 的 Install 只负责写出 profile，依赖库仍需按 playload 装齐
        await ResilientInstaller.InstallAsync(launcher, name, reporter, _log, ct).ConfigureAwait(false);
        return name;
    }

    private async Task<string> InstallQuiltAsync(
        MinecraftLauncher launcher, HttpClient http, string mcVersion, string? loaderVersion,
        ProgressReporter reporter, CancellationToken ct)
    {
        // BMCLAPI 没有 quilt-meta（实测 404），Quilt 只能走官方源
        var quilt = new QuiltInstaller(http, LauncherFactory.QuiltMetaHost);

        var name = string.IsNullOrWhiteSpace(loaderVersion)
            ? await quilt.Install(mcVersion, launcher.MinecraftPath).ConfigureAwait(false)
            : await quilt.Install(mcVersion, loaderVersion!, launcher.MinecraftPath).ConfigureAwait(false);

        await ResilientInstaller.InstallAsync(launcher, name, reporter, _log, ct).ConfigureAwait(false);
        return name;
    }

    private static ForgeInstallOptions CreateForgeOptions(
        string javaPath, ProgressReporter reporter, CancellationToken ct) => new()
    {
        JavaPath = javaPath,
        FileProgress = reporter,
        ByteProgress = reporter,
        InstallerOutput = new Progress<string>(line => reporter.Info("  " + line)),
        CancellationToken = ct,
        SkipIfAlreadyInstalled = true,
    };

    /// <summary>
    /// 加载器安装器需要靠 Java 跑处理器（Forge 的 ConsoleTool 之类）。
    /// 优先挑「刚好满足该版本 ABI 要求」的 Java，实在没有就用最新的。
    /// </summary>
    private async Task<string> PickInstallerJavaAsync(
        MinecraftLauncher launcher, string mcVersion, CancellationToken ct)
    {
        int required = 0;
        try
        {
            var version = await launcher.GetVersionAsync(mcVersion, ct).ConfigureAwait(false);
            required = JavaService.ParseMajor(version.JavaVersion?.MajorVersion);
        }
        catch (Exception ex)
        {
            _log?.Warn($"读不到 {mcVersion} 的 Java 要求：{ex.Message}");
        }

        var best = _java.Best(required > 0 ? required : null) ?? _java.Best(null);
        if (best is null)
            throw new InvalidOperationException("本机没有任何 Java，无法运行加载器安装器");

        _log?.Info($"加载器安装器使用 Java {best.Major}：{best.Path}");
        return best.Path;
    }

    // ── 版本列表 ─────────────────────────────────────────────
    //
    // 游戏版本统一由官方清单提供（下载页先选游戏版本，再选加载器版本），
    // 所以这里只需要「某个游戏版本对应的加载器版本列表」。

    public async Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind kind, string mcVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return Array.Empty<LoaderVersion>();

        using var http = _factory.CreateHttpClient();
        var launcher = _factory.Create(GamePaths.Shared(GameDir), http);

        switch (kind)
        {
            case LoaderKind.Forge:
            {
                try
                {
                    var versions = await new ForgeInstaller(launcher, http)
                        .GetForgeVersions(mcVersion).ConfigureAwait(false);
                    var list = versions
                        .Select(v => new LoaderVersion(v.ForgeVersionName, v.IsRecommendedVersion))
                        .OrderByDescending(v => v.Version, NumericVersionComparer.Instance)
                        .ToList();
                    if (list.Count > 0) return list;
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Forge 版本列表读取失败，改用 BMCLAPI：{ex.Message}");
                }
                return await GetForgeVersionsFromBmclAsync(http, mcVersion, ct).ConfigureAwait(false);
            }

            case LoaderKind.NeoForge:
            {
                var available = await NeoForgeCatalog.VersionsAsync(http, mcVersion, ct).ConfigureAwait(false);
                if (available.Count == 0 && NeoForgeCatalog.ExplainMissing(mcVersion) is { } why)
                    _log?.Warn($"NeoForge：{why}");
                return available
                    .Select(v => new LoaderVersion(v, false))
                    .ToList();
            }

            case LoaderKind.Fabric:
            {
                var fabric = _factory.FabricMetaHost is { } host
                    ? new FabricInstaller(http, host) : new FabricInstaller(http);
                var loaders = await fabric.GetLoaders(mcVersion).ConfigureAwait(false);
                return loaders
                    .Select(l => new LoaderVersion(l.Version ?? "", l.Stable))
                    .OrderByDescending(v => v.Version, NumericVersionComparer.Instance)
                    .ToList();
            }

            case LoaderKind.Quilt:
            {
                var quilt = new QuiltInstaller(http, LauncherFactory.QuiltMetaHost);
                var loaders = await quilt.GetLoaders(mcVersion).ConfigureAwait(false);
                return loaders
                    .Select(l => new LoaderVersion(l.Version ?? "", l.Stable))
                    .OrderByDescending(v => v.Version, NumericVersionComparer.Instance)
                    .ToList();
            }

            default:
                return Array.Empty<LoaderVersion>();
        }
    }

    // ── BMCLAPI 兜底 ────────────────────────────────────────

    private static async Task<IReadOnlyList<LoaderVersion>> GetForgeVersionsFromBmclAsync(
        HttpClient http, string mcVersion, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"{MirrorHttpHandler.BmclApi}/forge/minecraft/{mcVersion}", ct)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<LoaderVersion>();

        var result = new List<LoaderVersion>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                continue;

            var recommended = item.TryGetProperty("recommended", out var flag)
                              && flag.ValueKind == JsonValueKind.True;
            result.Add(new LoaderVersion(version.GetString()!, recommended));
        }

        result.Reverse();     // BMCLAPI 返回的是新 → 旧
        return result;
    }
}
