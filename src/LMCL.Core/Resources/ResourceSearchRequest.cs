namespace LMCL.Core.Resources;

/// <summary>
/// 一次资源搜索的输入。对齐 PCL 的 <c>ResourceSearcher.SearchRequest</c>。
/// 所有筛选项都是可空的——空 = 不限制。
/// </summary>
public sealed class ResourceSearchRequest
{
    public required CommunityResourceType Type { get; init; }

    /// <summary>搜索关键字。空串 = 按热度列。</summary>
    public string? SearchText { get; init; }

    /// <summary>游戏版本筛选，如 1.20.1。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器筛选，Modrinth 分类名（forge / fabric / neoforge / quilt）。</summary>
    public string? Loader { get; init; }

    /// <summary>分类标签筛选，Modrinth 分类名。</summary>
    public string? Tag { get; init; }

    /// <summary>每页大小。Modrinth 上限 100。</summary>
    public int PageSize { get; init; } = 30;

    /// <summary>从第几条开始（分页用，0 = 第一页）。</summary>
    public int Offset { get; init; }

    /// <summary>允许的来源。暂时只有 Modrinth 可用。</summary>
    public ResourcePlatform Sources { get; init; } = ResourcePlatform.Modrinth;

    /// <summary>请求内容相同就认为是同一个搜索——用于判断「能不能接着加载下一页」。</summary>
    public bool SameAs(ResourceSearchRequest other) =>
        Type == other.Type
        && SearchText == other.SearchText
        && GameVersion == other.GameVersion
        && Loader == other.Loader
        && Tag == other.Tag;
}

/// <summary>
/// 一次搜索的结果存储器。对齐 PCL 的 <c>ResourceSearcher.SearchResult</c>：
/// 分页位置记在结果里，「加载更多」就是拿着同一个 result 再搜一次。
/// </summary>
public sealed class ResourceSearchResult
{
    /// <summary>已获取的全部结果（跨页累积）。</summary>
    public List<ResourceProject> Results { get; } = new();

    /// <summary>当前页码位置。</summary>
    public int Offset { get; set; }

    /// <summary>平台报告的总数。-1 = 还不知道。</summary>
    public int Total { get; set; } = -1;

    /// <summary>失败原因。null = 正常。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>还能不能继续翻页。</summary>
    public bool CanContinue => Total < 0 || Offset < Total;
}
