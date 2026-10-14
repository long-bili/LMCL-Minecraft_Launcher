using System.IO.Compression;
using System.Text.Json;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.ModPacks;

public sealed record ModPackDependencies(string Minecraft, string? LoaderKind, string? LoaderVersion);

public sealed record ModPackInstalled(string Name, string InstanceName, int FilesDownloaded, ModPackDependencies Dependencies);

/// <summary>
/// Modrinth 整合包的安装：读 .mrpack 清单、下载、解进实例。
/// 搜索走统一的 <see cref="Resources.ResourceSearcher"/>，这里只留安装相关的事。
///
/// .mrpack 就是个 zip，里面 <c>modrinth.index.json</c> 描述要下载哪些文件、
/// 游戏与加载器版本要求，<c>overrides/</c>、<c>client-overrides/</c> 是要覆盖过去的目录。
/// </summary>
public sealed class ModPackService
{
    private const string ProjectApi = "https://api.modrinth.com/v2/project";

    private readonly Configuration.ConfigService _config;
    private readonly ILogSink? _log;

    public ModPackService(Configuration.ConfigService config, ILogSink? log = null)
    {
        _config = config;
        _log = log;
    }

    private HttpClient CreateClient() => Http.HttpClients.Create(_config.UseMirror, _log);

    /// <summary>
    /// 只读 .mrpack 的清单，不下载任何文件。
    /// 整合包必须先知道要哪个加载器，才能先把版本建出来、再把文件解进去。
    /// </summary>
    public async Task<(string Name, ModPackDependencies Dependencies)> ReadIndexAsync(
        string mrpackPath, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("这不是有效的 .mrpack：缺少 modrinth.index.json");

        ModrinthIndex index;
        await using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<ModrinthIndex>(stream, cancellationToken: ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("modrinth.index.json 解析失败");

        var name = string.IsNullOrWhiteSpace(index.Name)
            ? Path.GetFileNameWithoutExtension(mrpackPath)
            : index.Name!;

        return (name, new ModPackDependencies(
            index.Dependencies?.GetValueOrDefault("minecraft") ?? "",
            DetectLoader(index.Dependencies),
            DetectLoaderVersion(index.Dependencies)));
    }

    /// <summary>取整合包最新的 .mrpack 下载地址。</summary>
    public async Task<string?> GetLatestDownloadAsync(string projectId, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var json = await http.GetStringAsync($"{ProjectApi}/{projectId}/version", ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;

        foreach (var file in files.EnumerateArray())
        {
            if (!file.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) continue;
            var primary = file.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
            if (primary) return url.GetString();
        }

        return files.EnumerateArray()
            .Select(f => f.TryGetProperty("url", out var u) ? u.GetString() : null)
            .FirstOrDefault(u => u is not null);
    }

    /// <summary>
    /// 把 .mrpack 下到缓存目录，返回本地路径。调用方用完负责删掉。
    ///
    /// 之所以把「下载」和「安装」拆开：安装前必须先读清单、先装好对应的加载器，
    /// 而加载器版本目录一旦建出来就不能再改名了。
    /// </summary>
    public async Task<string> DownloadAsync(
        string url, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Configuration.AppPaths.EnsureCreated();
        Directory.CreateDirectory(Configuration.AppPaths.CacheDir);

        var target = Path.Combine(Configuration.AppPaths.CacheDir, $"mrpack-{Guid.NewGuid():N}.mrpack");

        using var http = CreateClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }

        return target;
    }

    public async Task<ModPackInstalled> InstallFileAsync(
        string mrpackPath, string instanceName, ProgressReporter reporter, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("这不是有效的 .mrpack：缺少 modrinth.index.json");

        ModrinthIndex index;
        await using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<ModrinthIndex>(stream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("modrinth.index.json 解析失败");

        var root = Game.GamePaths.InstanceRoot(_config.GameDir, instanceName, isolated: true);
        Directory.CreateDirectory(root);

        var dependencies = new ModPackDependencies(
            index.Dependencies?.GetValueOrDefault("minecraft") ?? "",
            DetectLoader(index.Dependencies),
            DetectLoaderVersion(index.Dependencies));

        // 1) 逐个下载文件（mods / config / resourcepacks …）
        var files = index.Files ?? new List<ModrinthFile>();
        var done = 0;
        using var http = CreateClient();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            reporter.Status($"正在下载整合包文件 {done}/{files.Count}：{Path.GetFileName(file.Path)}");

            var destination = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination) && new FileInfo(destination).Length == file.FileSize) continue;

            var url = file.Downloads.FirstOrDefault();
            if (string.IsNullOrEmpty(url)) continue;

            try
            {
                await using var source = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
                await using var target = File.Create(destination);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个文件失败不该毁掉整个整合包安装
                reporter.Warn($"下载失败（已跳过）：{file.Path} — {ex.Message}");
            }
        }

        // 2) 覆盖 overrides / client-overrides
        ExtractDirectory(archive, "overrides", root, reporter);
        ExtractDirectory(archive, "client-overrides", root, reporter);

        reporter.Success($"整合包「{index.Name ?? instanceName}」已安装，共 {files.Count} 个文件");
        return new ModPackInstalled(index.Name ?? instanceName, instanceName, files.Count, dependencies);
    }

    private static void ExtractDirectory(ZipArchive archive, string prefix, string root, ProgressReporter reporter)
    {
        var normalized = prefix.TrimEnd('/') + "/";
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = entry.FullName[normalized.Length..];
            if (string.IsNullOrWhiteSpace(relative) || relative.EndsWith('/')) continue;

            var destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        if (count > 0) reporter.Info($"已解压 {prefix}：{count} 个文件");
    }

    private static string? DetectLoader(Dictionary<string, string>? dependencies)
    {
        if (dependencies is null) return null;
        if (dependencies.ContainsKey("neoforge")) return "neoforge";
        if (dependencies.ContainsKey("forge")) return "forge";
        if (dependencies.ContainsKey("fabric-loader")) return "fabric";
        if (dependencies.ContainsKey("quilt-loader")) return "quilt";
        return null;
    }

    private static string? DetectLoaderVersion(Dictionary<string, string>? dependencies)
    {
        if (dependencies is null) return null;
        foreach (var key in new[] { "neoforge", "forge", "fabric-loader", "quilt-loader" })
            if (dependencies.TryGetValue(key, out var value)) return value;
        return null;
    }

    private sealed class ModrinthIndex
    {
        public string? FormatVersion { get; set; }
        public string? Name { get; set; }
        public string? VersionId { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public List<ModrinthFile>? Files { get; set; }
    }

    private sealed class ModrinthFile
    {
        public string Path { get; set; } = "";
        public List<string> Downloads { get; set; } = new();
        public long FileSize { get; set; }
    }
}
