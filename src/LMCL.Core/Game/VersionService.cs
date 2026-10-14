using System.Text.Json;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Game;

public enum LoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    OptiFine,
    LiteLoader,
    Unknown,
}

public static class LoaderLabels
{
    public static string Of(LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "原版",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.OptiFine => "OptiFine",
        LoaderKind.LiteLoader => "LiteLoader",
        _ => "未知",
    };
}

/// <summary>一个已安装的版本。</summary>
public sealed class InstalledVersion
{
    public required string Name { get; init; }

    /// <summary>对应的原版版本号（如 1.20.1）。</summary>
    public string BaseVersion { get; init; } = "";

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    public string LoaderVersion { get; init; } = "";

    public long SizeBytes { get; set; }

    public DateTime InstalledAt { get; init; }

    public bool Isolated { get; set; } = true;

    public string LoaderLabel => LoaderLabels.Of(Loader);

    /// <summary>列表里显示的一行副标题。</summary>
    public string Subtitle => Loader == LoaderKind.Vanilla
        ? "原版游戏"
        : $"{LoaderLabel} {LoaderVersion} · 基于 {BaseVersion}";

    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>已安装版本的扫描、识别与删除。</summary>
public sealed class VersionService
{
    private readonly Configuration.ConfigService _config;
    private readonly ILogSink? _log;

    public VersionService(Configuration.ConfigService config, ILogSink? log = null)
    {
        _config = config;
        _log = log;
    }

    private string GameDir => _config.GameDir;

    public IReadOnlyList<InstalledVersion> List(bool includeSize = false)
    {
        var result = new List<InstalledVersion>();
        var versionsDir = GamePaths.VersionsDir(GameDir);
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            var jsonFile = Path.Combine(dir, name + ".json");
            if (!File.Exists(jsonFile)) continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonFile));
                var detected = Detect(name, document.RootElement);
                var settings = GamePaths.LoadInstance(GameDir, name);

                result.Add(new InstalledVersion
                {
                    Name = name,
                    BaseVersion = detected.BaseVersion,
                    Loader = detected.Kind,
                    LoaderVersion = detected.LoaderVersion,
                    InstalledAt = File.GetLastWriteTime(jsonFile),
                    Isolated = settings.Isolated,
                    SizeBytes = includeSize ? DirectorySize(dir) : 0,
                });
            }
            catch (Exception ex)
            {
                _log?.Warn($"版本 {name} 的 JSON 无法解析：{ex.Message}");
            }
        }

        return result
            .OrderByDescending(v => VersionKey(v.BaseVersion))
            .ThenBy(v => v.Loader)
            .ThenByDescending(v => v.InstalledAt)
            .ToList();
    }

    /// <summary>给「磁盘占用」之类的地方用，比较慢，放后台线程跑。</summary>
    public void FillSizes(IEnumerable<InstalledVersion> versions)
    {
        foreach (var version in versions)
            version.SizeBytes = DirectorySize(GamePaths.VersionDir(GameDir, version.Name));
    }

    public bool Exists(string version) =>
        File.Exists(GamePaths.VersionJson(GameDir, version));

    /// <summary>
    /// 删除版本。注意：开了版本隔离的话，这个版本的 mods / 存档就在版本目录里，
    /// 会跟着一起删掉——UI 上必须先确认。
    /// </summary>
    public long Delete(string version)
    {
        var dir = GamePaths.VersionDir(GameDir, version);
        if (!Directory.Exists(dir)) return 0;

        var freed = DirectorySize(dir);
        Directory.Delete(dir, recursive: true);
        _log?.Info($"已删除版本 {version}，释放 {InstalledVersion.FormatSize(freed)}");
        return freed;
    }

    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch { /* 目录被占用等情况直接放弃统计 */ }
        return total;
    }

    // ── 识别 ────────────────────────────────────────────────

    private static (LoaderKind Kind, string LoaderVersion, string BaseVersion) Detect(
        string name, JsonElement root)
    {
        var baseVersion = ReadString(root, "inheritsFrom") ?? "";
        var kind = LoaderKind.Vanilla;
        var loaderVersion = "";

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                var coordinate = ReadString(library, "name") ?? "";
                var (found, version) = MatchLibrary(coordinate);
                if (found == LoaderKind.Unknown) continue;
                kind = found;
                loaderVersion = version;
                break;
            }
        }

        if (kind == LoaderKind.Vanilla)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("neoforge")) kind = LoaderKind.NeoForge;
            else if (lower.Contains("forge")) kind = LoaderKind.Forge;
            else if (lower.Contains("fabric")) kind = LoaderKind.Fabric;
            else if (lower.Contains("quilt")) kind = LoaderKind.Quilt;
            else if (lower.Contains("optifine")) kind = LoaderKind.OptiFine;
            else if (lower.Contains("liteloader")) kind = LoaderKind.LiteLoader;
        }

        if (string.IsNullOrEmpty(baseVersion)) baseVersion = GuessBaseVersion(name, kind);

        return (kind, loaderVersion, baseVersion);
    }

    private static (LoaderKind Kind, string Version) MatchLibrary(string coordinate)
    {
        const StringComparison I = StringComparison.OrdinalIgnoreCase;

        if (coordinate.StartsWith("net.neoforged:neoforge:", I) || coordinate.StartsWith("net.neoforged:forge:", I))
            return (LoaderKind.NeoForge, After(coordinate));
        if (coordinate.StartsWith("net.minecraftforge:forge:", I))
            return (LoaderKind.Forge, After(coordinate));
        if (coordinate.StartsWith("net.fabricmc:fabric-loader:", I))
            return (LoaderKind.Fabric, After(coordinate));
        if (coordinate.StartsWith("org.quiltmc:quilt-loader:", I))
            return (LoaderKind.Quilt, After(coordinate));
        if (coordinate.StartsWith("com.mumfrey:liteloader:", I))
            return (LoaderKind.LiteLoader, After(coordinate));
        if (coordinate.Contains(":optifine:", I) || coordinate.Contains("optifine:OptiFine", I))
            return (LoaderKind.OptiFine, After(coordinate));

        return (LoaderKind.Unknown, "");
    }

    private static string After(string coordinate)
    {
        var parts = coordinate.Split(':');
        return parts.Length >= 3 ? parts[2] : "";
    }

    private static string GuessBaseVersion(string name, LoaderKind kind)
    {
        // fabric-loader-0.15.11-1.20.1 / quilt-loader-… / 1.20.1-forge-47.4.24
        var parts = name.Split('-');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Length > 0 && char.IsDigit(parts[i][0]) && parts[i].Contains('.'))
                return parts[i];
        }
        return name;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>把 1.20.1 / 1.21 / 1.7.10 之类变成可比较的数字。</summary>
    public static double VersionKey(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.');
        double value = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number)) break;
            value += number * scale;
            scale /= 1000;
            if (scale < 0.000001) break;
        }
        return value;
    }
}
