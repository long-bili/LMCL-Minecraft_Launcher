using LMCL.Core.Game;

namespace LMCL.Core.Resources;

/// <summary>
/// 社区资源的类型。对齐 PCL 的 <c>ResourceTypes</c>：
/// Mod / 整合包 / 数据包 / 光影 / 资源包共用一套搜索与版本抽象。
/// </summary>
public enum CommunityResourceType
{
    Mod,
    ModPack,
    DataPack,
    Shader,
    ResourcePack,
}

/// <summary>社区资源平台。CurseForge 的 API 要 key，暂时只接 Modrinth，枚举先留着。</summary>
[Flags]
public enum ResourcePlatform
{
    Modrinth = 1,
    CurseForge = 2,
    Any = Modrinth | CurseForge,
}

/// <summary>
/// 平台 → 加载器的名字对照。
/// Modrinth 的分类名是小写固定几个；界面显示要转成中文标签。
/// </summary>
public static class ResourceLoaderNames
{
    /// <summary>Modrinth 的加载器分类名。空串 = 原版（不筛选）。</summary>
    public static string ApiName(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        _ => "",
    };

    /// <summary>界面显示的加载器名。</summary>
    public static string Label(LoaderKind kind) => LoaderLabels.Of(kind);

    /// <summary>从平台分类名猜加载器。认不出来返回 null（调用方按「不筛选」处理）。</summary>
    public static LoaderKind? FromApiName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        _ => null,
    };

    /// <summary>
    /// Modrinth 分类的中文名。
    /// 对齐 PCL 的做法：标签全部中文化后再给人看，认不出的原样返回。
    /// 表不用求全——常见的都在，长尾的分类显示英文也能看懂。
    /// </summary>
    public static string TagLabel(string category) => category.Trim().ToLowerInvariant() switch
    {
        // 功能向
        "optimization" => "性能优化",
        "technology" => "科技",
        "magic" => "魔法",
        "adventure" => "冒险",
        "exploration" => "探索",
        "worldgen" => "世界生成",
        "biomes" => "生物群系",
        "dimensions" => "维度",
        "structures" => "结构",
        "mobs" => "生物",
        "entities" => "实体",
        "items" => "物品",
        "equipment" => "装备",
        "combat" => "战斗",
        "pvp" => "玩家对战",
        "automation" => "自动化",
        "energy" => "能源",
        "redstone" => "红石",
        "transportation" => "运输",
        "storage" => "仓储",
        "logistics" => "物流",
        "food" => "食物",
        "farming" => "农业",
        "cooking" => "烹饪",
        "decoration" => "装饰",
        "building" => "建筑",
        "utility" => "实用",
        "qol" => "体验改良",
        "quality-of-life" => "体验改良",
        "library" => "支持库",
        "api" => "支持库",
        "fixes" => "修复",
        "bug-fixes" => "修复",
        "performance" => "性能",
        "audio" => "音频",
        "music" => "音乐",
        "gui" => "界面",
        "hud" => "界面",
        "minigame" => "小游戏",
        "skyblock" => "空岛",
        "hardcore" => "硬核",
        "kitchen-sink" => "大杂烩",
        "vanilla-plus" => "原版改良",
        "vanilla-like" => "原版风",
        "realistic" => "写实风",
        "modern" => "现代风",
        "medieval" => "中世纪",
        "fantasy" => "幻想风",
        "steampunk" => "蒸汽朋克",
        "sci-fi" => "科幻",
        "multiplayer" => "多人",
        "server" => "服务器",
        "client" => "客户端",
        "modloader" => "加载器",
        _ => category,
    };
}

/// <summary>资源类型 → Modrinth project_type 参数值。</summary>
public static class ResourceTypeNames
{
    public static string ApiName(CommunityResourceType type) => type switch
    {
        CommunityResourceType.Mod => "mod",
        CommunityResourceType.ModPack => "modpack",
        CommunityResourceType.DataPack => "datapack",
        CommunityResourceType.Shader => "shader",
        CommunityResourceType.ResourcePack => "resourcepack",
        _ => "mod",
    };
}
