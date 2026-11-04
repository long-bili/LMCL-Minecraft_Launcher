namespace LMCL.Core.Resources;

/// <summary>
/// 社区资源的工程信息。对齐 PCL 的 <c>ResourceProject</c>：
/// 一次搜索拿到的「这个 Mod / 整合包是什么」，不包含具体版本。
/// </summary>
public sealed record ResourceProject(
    ResourcePlatform Platform,
    CommunityResourceType Type,
    /// <summary>Modrinth 的工程 ID（乱码串）。</summary>
    string Id,
    /// <summary>短名，用于拼网址（modrinth.com/mod/{slug}）。</summary>
    string Slug,
    string Title,
    string Description,
    string Author,
    /// <summary>来源站工程页网址。</summary>
    string WebsiteUrl,
    long Downloads,
    /// <summary>图标地址，没有则为空串。</summary>
    string IconUrl,
    /// <summary>支持的游戏版本，升序。列表末尾是支持到的最新版。</summary>
    IReadOnlyList<string> GameVersions,
    /// <summary>支持的加载器分类名（forge / fabric …）。</summary>
    IReadOnlyList<string> Loaders,
    /// <summary>已中文化的标签。</summary>
    IReadOnlyList<string> Tags)
{
    /// <summary>支持到的最新游戏版本，没有则为空串。</summary>
    public string LatestGameVersion =>
        GameVersions.Count > 0 ? GameVersions[^1] : "";

    /// <summary>下载量按中文习惯折算：亿 → 万 → 原值。</summary>
    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:0.#} 亿次下载",
        >= 10_000 => $"{Downloads / 10_000.0:0.#} 万次下载",
        _ => $"{Downloads} 次下载",
    };

    public string AuthorText =>
        string.IsNullOrWhiteSpace(Author) ? "未知作者" : Author;
}

/// <summary>
/// 社区资源的一个具体版本。
/// 对齐 PCL 的 <c>ResourceVersion</c>：选版本、下载、比对更新都以它为单位。
/// </summary>
public sealed record ResourceVersion(
    string Id,
    string ProjectId,
    /// <summary>作者起的版本名（mc1.20.1-0.5.13-fabric 这种）。</summary>
    string Name,
    /// <summary>版本号（0.5.13 这种）。</summary>
    string VersionNumber,
    string GameVersion,
    string Loader,
    string FileName,
    string Url,
    long Size,
    /// <summary>更新日志原文，可能是空串。</summary>
    string Changelog = "");
