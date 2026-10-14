using System.Windows;
using System.Windows.Controls;
using LMCL.App.Animations;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 页面基类。
///
/// 八个页面各写各的，但有三件事每页都躲不掉：拿服务、往状态条上报进度、兜住异常。
/// 与其复制粘贴八遍，不如收在这里——顺便把「启动游戏」这条共用流程也统一了，
/// 免得主页和版本管理里的启动行为慢慢跑偏。
/// </summary>
public abstract class PageBase : UserControl, IPage
{
    protected static AppServices S => App.Services;

    public virtual void OnNavigatedTo() { }

    /// <summary>
    /// 页面进入时的内部动画：默认把最外层的内容容器当列表依次浮入。
    /// 被嵌进宿主（版本与资源、设置）的子面板不播——宿主播过了，再播一遍会叠成两层。
    /// 页面想换成别的花样（比如只动某几张卡）就 override 掉。
    /// </summary>
    public virtual void PlayEnterAnimation()
    {
        var root = Content switch
        {
            // 大多数页面是 ScrollViewer 包一个 StackPanel，往里钻一层
            ScrollViewer { Content: Panel inner } => inner,
            Panel panel => panel,
            _ => null,
        };

        if (root is null || root.Children.Count == 0) return;

        // 幅度比列表小：页面级已经有 AaPageEnter 的淡入上移了，
        // 这里只让内部各块「先后到」，滑太多会晃
        Animator.AaStack(root, 90, 20, 10);
    }

    /// <summary>供宿主调用：页面内容数据刷新后重播一次进入动画。</summary>
    public void ReplayEnterAnimation() => PlayEnterAnimation();

    // ── 游戏目录切换（版本与资源页和设置页共用）──────────

    /// <summary>「游戏目录切换」下拉的一行。Path 为空 = 默认位置。</summary>
    public sealed record GameDirChoice(string Path, string Label)
    {
        public override string ToString() => Label;

        /// <summary>
        /// 是不是「默认位置」那一行。它不是一个用户登记的目录，
        /// 所以下拉里的移除按钮对它不显示。
        /// </summary>
        public bool IsDefault => Path.Length == 0;
    }

    /// <summary>
    /// 填目录切换下拉：默认位置 + 登记过的目录 + 当前目录。
    /// 不存在的路径也列出来（标注「不存在」）——用户可能把移动硬盘拔了，
    /// 直接消失反而让人以为设置丢了。
    /// </summary>
    protected static void FillGameDirChoices(ComboBox combo)
    {
        var config = S.Config.Current;
        var candidates = new List<string> { "" };   // 空 = 默认位置

        foreach (var dir in config.GameDirs)
            if (!candidates.Contains(dir, StringComparer.OrdinalIgnoreCase))
                candidates.Add(dir);

        if (config.GameDir.Length > 0
            && !candidates.Contains(config.GameDir, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(config.GameDir);
        }

        combo.ItemsSource = candidates
            .Select(dir => new GameDirChoice(
                dir,
                dir.Length == 0
                    ? $"默认位置（{AppPaths.DefaultGameDir}）"
                    : dir + (Directory.Exists(dir) ? "" : "　（不存在）")))
            .ToList();

        combo.SelectedItem = combo.Items
            .OfType<GameDirChoice>()
            .FirstOrDefault(c => c.Path == config.GameDir);
    }

    /// <summary>
    /// 切换游戏目录：把当前目录登记进列表（之后能一键切回来），保存配置。
    /// 返回是否真的换了——调用方负责换完后刷新页面。
    /// </summary>
    protected static bool TrySwitchGameDir(GameDirChoice choice)
    {
        var config = S.Config.Current;
        if (choice.Path == config.GameDir) return false;

        if (config.GameDir.Length > 0
            && !config.GameDirs.Contains(config.GameDir, StringComparer.OrdinalIgnoreCase))
        {
            config.GameDirs.Add(config.GameDir);
        }

        config.GameDir = choice.Path;
        S.Config.Save();
        return true;
    }

    /// <summary>
    /// 把一个目录从「登记过的目录」列表里去掉（**不碰磁盘上的文件**）。
    ///
    /// 如果去掉的正好是当前目录，就把当前目录切回默认位置 ——
    /// 否则会留下一个「配置指向某个已经不在列表里的目录」的状态，
    /// 下拉框里看不到它，但界面还在用它，很费解。
    /// 返回是否真的改动了。
    /// </summary>
    protected static bool RemoveGameDirFromList(string path)
    {
        var config = S.Config.Current;
        var removed = config.GameDirs.RemoveAll(
            d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase)) > 0;

        if (string.Equals(config.GameDir, path, StringComparison.OrdinalIgnoreCase))
        {
            // 切回默认位置，并且不要把它又登记回列表
            config.GameDir = "";
            removed = true;
        }

        if (removed) S.Config.Save();
        return removed;
    }

    /// <summary>
    /// 被当作子面板嵌进宿主页面时调用（比如「版本与资源」页把版本设置与资源管理收进右栏）。
    ///
    /// 嵌入后要做的调整是一致的：收掉页面自己的版本选择器——不然会和宿主那一层的选择打架，
    /// 用户看到两个下拉框不知道该信哪个；顺带把页面边距去掉，由宿主统一控制留白。
    /// </summary>
    public virtual void UseEmbeddedMode() { }

    /// <summary>造一个把状态与进度直接打到窗口底部状态条的 reporter。</summary>
    protected static ProgressReporter Reporter() => new(App.Services.Log)
    {
        StatusChanged = ShellStatus.SetText,
        RatioChanged = ShellStatus.SetRatio,
    };

    /// <summary>
    /// 跑一个可能失败的后台操作。期间禁用指定控件，异常统一弹窗——
    /// 不能让 App 的全局兜底把出错上下文丢干净，那样用户只看到一个光秃秃的异常名。
    /// </summary>
    protected async Task RunAsync(Func<CancellationToken, Task> work,
        string what = "操作", params UIElement[] disable)
    {
        SetEnabled(disable, false);
        try
        {
            await work(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            ShellStatus.SetText("已取消");
            ShellStatus.SetRatio(0);
        }
        catch (Exception ex)
        {
            S.Log.Error($"{what}失败：{ex.Message}");
            ShellStatus.Error($"{what}失败");
            Ui.Error($"{what}失败：\n\n{ex.Message}");
        }
        finally
        {
            SetEnabled(disable, true);
        }
    }

    private static void SetEnabled(UIElement[] elements, bool enabled)
    {
        foreach (var element in elements)
            if (element is not null) element.IsEnabled = enabled;
    }

    /// <summary>
    /// 启动游戏。主页和版本管理都要用，流程也一致：校验账号 → 组装请求 → 起进程。
    /// 装齐文件这一步在 LauncherService 里做，已存在的文件会按 SHA1 跳过。
    /// </summary>
    protected async Task LaunchVersionAsync(string version)
    {
        var account = S.Accounts.Selected;
        if (account is null)
        {
            Ui.Warn("还没有可用的账号。\n\n请点右上角的账号按钮添加一个：正版登录、外置登录、离线都可以。");
            return;
        }

        var result = await S.Launcher.LaunchAsync(
            new LaunchRequest { Version = version, Account = account }, Reporter());

        if (!result.Started)
        {
            ShellStatus.Error(result.Message);
            Ui.Warn(result.Message);
            return;
        }

        // LaunchCount / LastVersion 由 LauncherService.StartProcess 落盘，这里不重复记

        if (S.Config.Current.HideLauncherOnLaunch && Window.GetWindow(this) is { } window)
            window.WindowState = WindowState.Minimized;
    }

    /// <summary>没有账号时也要能预览启动参数，给个临时的离线身份即可。</summary>
    protected static Core.Accounts.Account PreviewAccount() =>
        S.Accounts.Selected ?? new Core.Accounts.Account
        {
            Name = "Player",
            Type = Core.Accounts.AccountType.Offline,
        };

    /// <summary>已安装版本列表，按版本号从新到旧。</summary>
    protected static IReadOnlyList<InstalledVersion> Installed() => S.Versions.List();

    /// <summary>跳到别的页面（顺带让左侧导航选中项跟着走）。</summary>
    protected void GoTo(string key)
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.GoTo(key);
    }

    /// <summary>跳到「版本与资源」并选中某个版本。</summary>
    protected void GoToVersion(string version)
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.GoToVersion(version);
    }

    /// <summary>跳到「版本与资源」并直接打开某个版本的设置页。主页「版本设置」按钮用。</summary>
    protected void GoToVersionSettings(string version)
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.GoToVersionSettings(version);
    }

    /// <summary>跳到「设置」页的某个分栏（tools / help / 其它为全局设置）。</summary>
    protected void GoToSettingsTab(string tab)
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.GoToSettingsTab(tab);
    }

    /// <summary>打开账号管理窗口。</summary>
    protected void OpenAccountWindow()
    {
        if (Window.GetWindow(this) is ShellWindow shell) shell.OpenAccountWindow();
    }

    // ── 整合包安装 ──────────────────────────────────────────
    //
    // 下载页和主页都要装整合包，流程也一样，所以放这里共用。

    /// <summary>
    /// 完整安装一个本地 .mrpack，返回落成的实例名。
    ///
    /// 顺序很讲究：加载器安装器按自己的规矩给版本目录起名（forge-47.4.27-1.20.1 之类），
    /// 而整合包要求「实例名 = 包名」，所以必须先装加载器 → 改名 → 最后才把包里的文件解进去。
    /// 反过来做的话目标目录已经存在，改名会被安全地拒绝。
    /// </summary>
    protected async Task<string> InstallModPackAsync(string mrpackPath, CancellationToken ct)
    {
        var reporter = Reporter();
        var (name, deps) = await S.ModPacks.ReadIndexAsync(mrpackPath, ct);

        if (string.IsNullOrWhiteSpace(deps.Minecraft))
            throw new InvalidOperationException($"整合包「{name}」没有声明 Minecraft 版本，装不了");

        reporter.Info($"整合包「{name}」要求 Minecraft {deps.Minecraft}"
            + (deps.LoaderKind is null ? "" : $"，加载器 {deps.LoaderKind} {deps.LoaderVersion}"));

        var instance = UniqueInstance(InstallService.SanitizeInstanceName(name));

        if (deps.LoaderKind is { } loader)
        {
            await S.Install.InstallLoaderAsAsync(
                ParseLoader(loader), deps.Minecraft, deps.LoaderVersion, instance, reporter, ct);
        }
        else
        {
            // 没有加载器依赖：装原版，再建一个只声明 inheritsFrom 的隔离实例，
            // 免得整合包的文件直接糊到原版目录上
            await S.Install.InstallAsync(deps.Minecraft, reporter, ct);
            S.Install.CreateInstance(deps.Minecraft, instance);
        }

        var result = await S.ModPacks.InstallFileAsync(mrpackPath, instance, reporter, ct);

        S.Config.Current.LastVersion = instance;
        S.Config.Save();
        reporter.Success($"整合包「{result.Name}」已装好，版本名 {instance}");

        return instance;
    }

    private static LoaderKind ParseLoader(string kind) => kind.ToLowerInvariant() switch
    {
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        _ => LoaderKind.Vanilla,
    };

    /// <summary>同名版本已经存在时，往后缀数字上找空位。</summary>
    protected static string UniqueInstance(string name)
    {
        if (!S.Versions.Exists(name)) return name;

        for (var i = 2; i < 200; i++)
            if (!S.Versions.Exists($"{name}-{i}")) return $"{name}-{i}";

        return $"{name}-{DateTime.Now:HHmmss}";
    }
}
