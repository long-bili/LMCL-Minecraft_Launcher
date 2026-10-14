using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.Http;
using LMCL.Core.Java;
using LMCL.Core.ModPacks;
using LMCL.Core.Resources;
using LMCL.Core.Tools;

namespace LMCL.App.Services;

/// <summary>
/// 全应用的服务容器。刻意用最朴素的写法——启动器规模不大，
/// 引入 DI 框架只会让调用链更难读。
/// </summary>
public sealed class AppServices
{
    public ConfigService Config { get; }

    public AccountStore Accounts { get; }

    public LogBus Log { get; }

    public JavaService Java { get; }

    public LauncherFactory Factory { get; }

    public VersionService Versions { get; }

    public InstallService Install { get; }

    public LauncherService Launcher { get; }

    /// <summary>本地资源管理（模组 / 资源包 / 光影 / 存档的查看、启用、删除）。</summary>
    public ResourceService Resources { get; }

    /// <summary>
    /// 社区资源统一入口（Modrinth 搜索、列版本、更新检查、下载）。
    /// Mod / 整合包共用一套请求与结果结构，对齐 PCL 的 ResourceSearcher。
    /// </summary>
    public ResourceSearcher Community { get; }

    public ModPackService ModPacks { get; }

    public MicrosoftAuth Microsoft { get; }

    public ThirdPartyAuth Third { get; }

    /// <summary>皮肤 / 披风的读取与切换（仅微软正版账号有意义）。</summary>
    public SkinService Skin { get; }

    /// <summary>本地皮肤库：Steve / Alex 内置位 + 一个自定义位。</summary>
    public SkinLibrary SkinLibrary { get; }

    public AuthlibInjector Injector { get; }

    public UpdateChecker Updater { get; }

    public const string Version = "2.0.1";

    public AppServices()
    {
        Config = new ConfigService();
        Accounts = new AccountStore();
        Log = new LogBus();
        Java = new JavaService(Config, Log);
        Factory = new LauncherFactory(Config, Java, Log);
        Versions = new VersionService(Config, Log);
        Install = new InstallService(Config, Java, Factory, Log);
        Injector = new AuthlibInjector(Log);
        // 皮肤库要先建出来：LauncherService 启动离线账号时要按它解析该下发哪张皮肤。
        // 注意这里只是构造（会读索引文件），种子由 Initialize 里的 EnsureSeededAsync 补
        SkinLibrary = new SkinLibrary();
        Launcher = new LauncherService(Config, Java, Factory, Injector, SkinLibrary, Log);
        Resources = new ResourceService(Config, Log);
        Community = new ResourceSearcher(Config, Log);
        ModPacks = new ModPackService(Config, Log);
        // 微软登录端点不走镜像
        Microsoft = new MicrosoftAuth(HttpClients.Create(useMirror: false, Log), Log);
        // 外置登录的服务器地址是用户自己填的，同样不要拿镜像规则去改写它
        Third = new ThirdPartyAuth(HttpClients.Create(useMirror: false, Log), Log);
        // 皮肤 / 披风同样直连 Minecraft 服务（textures.minecraft.net 的图也走这条线）
        Skin = new SkinService(Microsoft, HttpClients.Create(useMirror: false, Log), Log);
        Updater = new UpdateChecker(Log);
    }

    public void Initialize()
    {
        AppPaths.EnsureCreated();
        Config.Load();
        Accounts.Load();

        // 外观三件事要按顺序来：先换主题（会重建颜色字典、清掉 alpha 基准），
        // 再套强调色，最后按用户的通透度缩放底色 alpha。
        // 顺序反了的话，主题一换通透度就被重置回原样
        ThemeManager.Apply(Config.Current.Theme);
        ThemeManager.ApplyAccent(Config.Current.Accent, Config.Current.AccentR,
            Config.Current.AccentG, Config.Current.AccentB);
        ThemeManager.ApplyTransparency(Config.Current.Transparency);

        // 皮肤库要先备好种子（Steve / Alex），自检时它可能被直接构造出来
        _ = SkinLibrary.EnsureSeededAsync(Config.GameDir);

        Log.Info($"LMCL {Version} 启动");
        Log.Info($"游戏目录：{Config.GameDir}");
        Log.Info($"下载源：{ConfigService.DescribeSource(Config.Current.Source)}");
    }
}
