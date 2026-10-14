using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Resources;

/// <summary>
/// 社区资源统一入口：搜索、列版本、检查更新、下载安装。
/// 对齐 PCL 的 <c>ResourceSearcher</c> + <c>ModDownloadLib</c> 职责划分：
/// Mod / 整合包 / 光影 / 资源包都走这一套请求与结果结构，
/// 而不是每种资源各写一个搜索方法。
///
/// 只做 Modrinth 一家，是刻意收窄的：CurseForge 的官方 API 现在要申请 key 才能调，
/// 没 key 就只能爬网页，那种东西随时会坏，不适合塞进启动器。
/// Modrinth 的 API 是公开无鉴权的，稳定得多，而且绝大多数主流模组两边都发。
/// 请求与下载都走 <c>HttpClients.Create</c>，与游戏文件下载共用同一套镜像与重试策略。
/// </summary>
public sealed class ResourceSearcher
{
    private const string Base = "https://api.modrinth.com/v2";

    private readonly ConfigService _config;
    private readonly ILogSink? _log;

    public ResourceSearcher(ConfigService config, ILogSink? log = null)
    {
        _config = config;
        _log = log;
    }

    private HttpClient CreateClient() => Http.HttpClients.Create(_config.UseMirror, _log);

    // ── 搜索 ────────────────────────────────────────────────

    /// <summary>
    /// 按请求搜一页，结果追加进 <paramref name="result"/>，并更新分页位置。
    /// 「加载更多」就是拿着同一个 result、把 Offset 往后拨再调一次。
    /// </summary>
    public async Task SearchAsync(ResourceSearchRequest request, ResourceSearchResult result,
        CancellationToken ct = default)
    {
        var facets = new List<string> { $"[\"project_type:{ResourceTypeNames.ApiName(request.Type)}\"]" };
        if (!string.IsNullOrWhiteSpace(request.GameVersion)) facets.Add($"[\"versions:{request.GameVersion}\"]");
        if (!string.IsNullOrWhiteSpace(request.Loader)) facets.Add($"[\"categories:{NormalizeLoader(request.Loader)}\"]");
        if (!string.IsNullOrWhiteSpace(request.Tag)) facets.Add($"[\"categories:{request.Tag}\"]");

        var url = $"{Base}/search?limit={request.PageSize}&offset={request.Offset}"
                + $"&query={Uri.EscapeDataString(request.SearchText ?? "")}"
                + $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        using var http = CreateClient();
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        // 注意字段名是 total_hits 不是 total，Modrinth 的 v2 搜索就这么返回的
        if (document.RootElement.TryGetProperty("total_hits", out var total) && total.TryGetInt32(out var n))
            result.Total = n;

        if (!document.RootElement.TryGetProperty("hits", out var array)) return;

        var before = result.Results.Count;

        foreach (var item in array.EnumerateArray())
        {
            result.Results.Add(new ResourceProject(
                ResourcePlatform.Modrinth,
                request.Type,
                Text(item, "project_id"),
                Text(item, "slug"),
                Text(item, "title"),
                Text(item, "description"),
                // 有组织名的优先显示组织（比如 Sodium 的 CaffeineMC），没有才落回作者
                Text(item, "organization") is { Length: > 0 } org ? org : Text(item, "author"),
                $"https://modrinth.com/{SlugPath(request.Type)}/{Text(item, "slug")}",
                Number(item, "downloads"),
                Text(item, "icon_url"),
                Strings(item, "versions"),
                Strings(item, "categories"),
                Strings(item, "categories").Select(ResourceLoaderNames.TagLabel).ToList()));
        }

        // 分页位置往后拨「本页新增的条数」——不是总条数，
        // 不然拿着同一个 result 连点两次「加载更多」会重复请求同一页
        result.Offset += result.Results.Count - before;
    }

    private static string SlugPath(CommunityResourceType type) => type switch
    {
        CommunityResourceType.Mod => "mod",
        CommunityResourceType.ModPack => "modpack",
        CommunityResourceType.DataPack => "datapack",
        CommunityResourceType.Shader => "shader",
        CommunityResourceType.ResourcePack => "resourcepack",
        _ => "mod",
    };

    // ── 版本列表 ────────────────────────────────────────────

    /// <summary>
    /// 列出某个工程的可用版本，新的在前。
    /// 不给 gameVersion / loader 就全部返回，由调用方挑。
    /// </summary>
    public async Task<IReadOnlyList<ResourceVersion>> GetVersionsAsync(
        string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default)
    {
        var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        if (!string.IsNullOrWhiteSpace(loader))
            query.Add($"loaders={Uri.EscapeDataString($"[\"{NormalizeLoader(loader)}\"]")}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        using var http = CreateClient();
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var versions = new List<ResourceVersion>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return versions;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var file = PrimaryFile(item);
            if (file is null) continue;

            versions.Add(new ResourceVersion(
                Text(item, "id"),
                projectId,
                Text(item, "name"),
                Text(item, "version_number"),
                FirstString(item, "game_versions"),
                FirstString(item, "loaders"),
                Text(file.Value, "filename"),
                Text(file.Value, "url"),
                Number(file.Value, "size"),
                Text(item, "changelog")));
        }

        return versions;
    }

    // ── 安装 ────────────────────────────────────────────────

    /// <summary>把选中的版本下载到目标 mods 目录，返回落地的完整路径。</summary>
    public async Task<string> InstallAsync(
        string modDirectory, ResourceVersion version,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modDirectory);
        var target = Path.Combine(modDirectory, SafeFileName(version.FileName));

        // 复用统一的 HttpClient：镜像改写、超时（单文件 5 分钟）、UserAgent 都跟下载一致。
        // 关掉自动解压，免得改动字节流影响 SHA1 校验 —— Create() 里已经这么设了。
        using var http = CreateClient();

        using var response = await http.GetAsync(version.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? version.Size;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1 << 16];
        long done = 0;
        int read;

        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report(total > 0 ? (double)done / total : 0);
        }

        _log?.Info($"已安装模组：{Path.GetFileName(target)}");
        return target;
    }

    // ── 更新检查 ────────────────────────────────────────────

    /// <summary>
    /// 按文件哈希问 Modrinth「我装的这些是什么版本、有没有更新的」。
    ///
    /// 走的是 <c>/v2/version_files</c>：把本地 jar 的 sha1 批量丢过去，
    /// Modrinth 直接认出是哪个模组的哪个版本，不需要靠文件名猜。
    /// 返回的版本列表是**新的在前**，所以找到当前版本在列表里的位置，它前面的就是可更新的。
    /// </summary>
    public async Task<IReadOnlyList<ResourceUpdate>> CheckUpdatesAsync(
        IEnumerable<string> filePaths, string? gameVersion = null, string? loader = null,
        CancellationToken ct = default)
    {
        var files = filePaths.Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                             .Where(File.Exists)
                             .ToList();
        if (files.Count == 0) return Array.Empty<ResourceUpdate>();

        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var hash = Sha1(path);
            if (hash is null) continue;
            byHash.TryAdd(hash, path);
        }

        if (byHash.Count == 0) return Array.Empty<ResourceUpdate>();

        using var http = CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/version_files")
        {
            Content = JsonContent.Create(new
            {
                hashes = byHash.Keys.ToArray(),
                algorithm = "sha1",
            }),
        };

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Array.Empty<ResourceUpdate>();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var updates = new List<ResourceUpdate>();

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var hash = entry.Name;
            if (!byHash.TryGetValue(hash, out var path)) continue;

            var current = entry.Value;
            var projectId = Text(current, "project_id");
            var currentVersionId = Text(current, "id");
            var currentNumber = Text(current, "version_number");
            if (projectId.Length == 0) continue;

            var all = await GetVersionsAsync(projectId, gameVersion, loader, ct).ConfigureAwait(false);
            if (all.Count == 0) continue;

            var index = -1;
            for (var i = 0; i < all.Count; i++)
                if (all[i].Id == currentVersionId) { index = i; break; }

            // 当前版本不在筛选结果里（比如装的这个不支持当前游戏版本），就当作没有更新
            if (index <= 0) continue;

            updates.Add(new ResourceUpdate(path, Path.GetFileName(path), currentNumber, all[0]));
        }

        return updates;
    }

    // ── 小工具 ──────────────────────────────────────────────

    /// <summary>Modrinth 的加载器分类名是小写的固定几个，Forge / Fabric / Quilt / NeoForge。</summary>
    private static string NormalizeLoader(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "forge" => "forge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        _ => loader.Trim().ToLowerInvariant(),
    };

    private static string? Sha1(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        return name.Length == 0 ? "mod.jar" : name;
    }

    private static JsonElement? PrimaryFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        // 一个版本偶尔会带多个文件（主 jar + sources），主文件的 primary 是 true
        foreach (var file in files.EnumerateArray())
            if (file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                return file;

        foreach (var file in files.EnumerateArray())
            return file;

        return null;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>取字符串数组的第一个字符串。</summary>
    private static string FirstString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? "";

        return "";
    }

    private static List<string> Strings(JsonElement element, string name)
    {
        var list = new List<string>();
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);

        return list;
    }
}

/// <summary>一个已装的模组有新版本可更新。</summary>
public sealed record ResourceUpdate(
    string InstalledPath, string InstalledName, string CurrentVersion, ResourceVersion NewVersion);
