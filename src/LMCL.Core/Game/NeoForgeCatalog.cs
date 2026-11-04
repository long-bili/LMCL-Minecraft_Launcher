using System.Text.RegularExpressions;
using LMCL.Core.Http;

namespace LMCL.Core.Game;

/// <summary>
/// NeoForge 的版本目录。
///
/// 不能用库自带的 NeoForge 版本加载器：它请求的是 <c>maven.neoforged.net/api/maven</c>，
/// 而 BMCLAPI 没有镜像这个路径（实测 404），官方站直连又经常不通（实测 25 秒超时）。
/// 所以改成直接读镜像上的 <c>maven-metadata.xml</c>，并自己还原版本号规则：
///
///   MC 1.20.4 → NeoForge 20.4.x
///   MC 1.21   → NeoForge 21.0.x     （只有两段时补一个 .0）
///   MC 1.21.1 → NeoForge 21.1.x
///   MC 26.3   → NeoForge 26.3.0.x
///
/// 注意 1.20.1 是特例：那一版的 NeoForge 发布在另一个 artifact（<c>net.neoforged:forge</c>）
/// 上，路径与命名都不一样，这里不做猜测——直接返回空，由 UI 提示改用 Forge。
/// </summary>
public static class NeoForgeCatalog
{
    private static readonly Regex VersionRegex =
        new(@"<version>\s*([^<\s]+)\s*</version>", RegexOptions.Compiled);

    public static string MetadataUrl =>
        MirrorHttpHandler.BmclApi + "/maven/net/neoforged/neoforge/maven-metadata.xml";

    /// <summary>把游戏版本换算成 NeoForge 的版本前缀；无法换算时返回 null。</summary>
    public static string? PrefixFor(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;

        var parts = mcVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        // "1.20.4" → ["20","4"]；"26.3" 保持原样
        var rest = parts[0] == "1" ? parts.Skip(1).ToArray() : parts;
        if (rest.Length == 0) return null;
        if (rest.Any(p => p.Length == 0 || !p.All(char.IsDigit))) return null;

        return rest.Length == 1 ? rest[0] + ".0" : rest[0] + "." + rest[1];
    }

    /// <summary>取某个游戏版本可用的 NeoForge 版本，新版本在前。</summary>
    public static async Task<IReadOnlyList<string>> VersionsAsync(
        HttpClient http, string mcVersion, CancellationToken ct = default)
    {
        var prefix = PrefixFor(mcVersion);
        if (prefix is null) return Array.Empty<string>();

        var xml = await http.GetStringAsync(MetadataUrl, ct).ConfigureAwait(false);

        return VersionRegex.Matches(xml)
            .Select(m => m.Groups[1].Value)
            .Where(v => v == prefix || v.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Distinct()
            .OrderByDescending(v => v, NumericVersionComparer.Instance)
            .ToList();
    }

    /// <summary>该游戏版本是否根本不提供 NeoForge。</summary>
    public static string? ExplainMissing(string mcVersion) => PrefixFor(mcVersion) switch
    {
        null => "无法识别这个游戏版本号",
        var p when p == "20.1" => "NeoForge 在 1.20.1 上发布在另一个制品里，请改用 Forge",
        _ => null,
    };
}
