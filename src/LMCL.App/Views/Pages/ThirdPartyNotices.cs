namespace LMCL.App.Views.Pages;

/// <summary>
/// 用到的第三方开源软件清单。
///
/// 为什么单独放一个文件、而不是把链接硬写进 XAML：
/// 这份清单会随依赖变化（升版本、换库、加新组件），
/// 写成结构化数据以后再改只动这一处，界面按顺序铺出来就行。
///
/// **加新依赖时记得回来补一条** —— 许可合规靠的就是这份表，
/// 漏掉一个就等于「用了别人的东西但没署名」。
/// </summary>
internal static class ThirdPartyNotices
{
    /// <param name="Name">显示名（库名 / 组件名）。</param>
    /// <param name="Version">版本号；运行时按需下载的写「按需下载」。</param>
    /// <param name="License">许可证标识。</param>
    /// <param name="Url">点它跳转的地址（GitHub 或官网）。</param>
    /// <param name="Note">一句话说明它在这个项目里干什么用。</param>
    /// <param name="Group">归到哪一组。</param>
    internal sealed record Entry(
        string Name, string Version, string License, string Url, string Note, Group Group);

    internal enum Group
    {
        /// <summary>编译进程序的 NuGet / 库依赖。</summary>
        Library,

        /// <summary>运行时按需下载的外部程序。</summary>
        Runtime,

        /// <summary>数据与镜像服务。</summary>
        Service,
    }

    internal static readonly Entry[] All =
    [
        // ── 库依赖 ──────────────────────────────────────────
        new("CmlLib.Core", "4.0.6", "MIT",
            "https://github.com/CmlLib/CmlLib.Core",
            "下载游戏文件、安装版本、组装启动命令（本项目的核心依赖）", Group.Library),

        new("CmlLib.Core.Installer.Forge", "2.0.0", "MIT",
            "https://github.com/CmlLib/CmlLib.Core.Installer.Forge",
            "安装 Forge 加载器", Group.Library),

        new("CmlLib.Core.Installer.NeoForge", "4.0.1", "MIT",
            "https://github.com/GamerVII-NET/CmlLib.Core.Installer.NeoForge",
            "安装 NeoForge 加载器", Group.Library),

        new("CommunityToolkit.Mvvm", "8.4.2", "MIT",
            "https://github.com/CommunityToolkit/dotnet",
            "界面数据绑定（只用到 ObservableObject）", Group.Library),

        new("HtmlAgilityPack", "（随 CmlLib 引入）", "MIT",
            "https://github.com/zzzprojects/html-agility-pack",
            "解析 HTML（由 CmlLib 间接使用）", Group.Library),

        new("SharpZipLib", "（随 CmlLib 引入）", "MIT",
            "https://github.com/icsharpcode/SharpZipLib",
            "解压 zip（由 CmlLib 间接使用）", Group.Library),

        // ── 运行时按需下载 ──────────────────────────────────
        new("authlib-injector", "按需下载", "AGPL-3.0",
            "https://github.com/yushijinhun/authlib-injector",
            "外置登录：把客户端对 Mojang 验证服务器的请求改写到第三方验证服务器", Group.Runtime),

        new("EasyTier", "2.6.4", "Apache-2.0",
            "https://github.com/EasyTier/EasyTier",
            "异地联机：用虚拟网卡把不同网络的机器拉进同一个虚拟局域网", Group.Runtime),

        new("Campfire", "开源版 main", "GPL-3.0",
            "https://github.com/Xocio/Campfire",
            "联机大厅：公开房间的服务协议与邀请码格式按其公开实现互通（独立实现，未复制代码之外的部分）", Group.Runtime),

        // ── 数据与镜像 ──────────────────────────────────────
        new("BMCLAPI", "—", "开源镜像服务",
            "https://github.com/bangbang93/openbmclapi",
            "国内下载源：镜像 Mojang / Forge / Fabric / NeoForge 的文件，不用翻墙也能下", Group.Service),

        new("Modrinth", "—", "开放 API",
            "https://github.com/modrinth",            "模组 / 资源包 / 数据包 / 光影的搜索与下载来源", Group.Service),
    ];

    /// <summary>分组标题，按 <see cref="Group"/> 的顺序显示。</summary>
    internal static string TitleOf(Group group) => group switch
    {
        Group.Library => "库依赖",
        Group.Runtime => "运行时按需下载的组件",
        _ => "数据与镜像服务",
    };

    internal static IEnumerable<Group> Groups =>
        [Group.Library, Group.Runtime, Group.Service];

    /// <summary>
    /// **必须**出现在清单里的组件。
    ///
    /// 为什么需要这份「基准」：光断言「界面上的链接数 == 清单条数」是抓不到漏项的 ——
    /// 清单少一条、界面也跟着少一条，两边自洽，测试照样绿。
    /// 而漏项的后果是「用了别人的东西没署名」，恰恰是这份清单存在的理由。
    /// 所以把关键依赖在这里列死：**删掉任何一条都会让自检失败**，
    /// 加新依赖时也必须同时往这里补，逼着人做这个动作。
    /// </summary>
    internal static readonly string[] Required =
    [
        "CmlLib.Core",
        "CmlLib.Core.Installer.Forge",
        "CmlLib.Core.Installer.NeoForge",
        "CommunityToolkit.Mvvm",
        "authlib-injector",
        "EasyTier",
        "BMCLAPI",
        "Modrinth",
    ];

    /// <summary>有没有哪条必需组件被漏掉了。</summary>
    internal static bool AllRequiredPresent() =>
        Required.All(name => All.Any(e => e.Name == name));
}
