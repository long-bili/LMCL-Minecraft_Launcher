using System.Text;
using System.Windows;
using LMCL.App.Views;
using LMCL.App.Views.Pages;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Services;

/// <summary>自检结果。</summary>
internal sealed record SelfCheckResult(int Passed, int Total, IReadOnlyList<string> Failures, string Report)
{
    public bool Ok => Failures.Count == 0;
}

/// <summary>
/// 界面自检：把每个页面和对话框都构造一遍。
///
/// 为什么需要它：页面是懒加载的，只有点进那一页才会创建，所以「启动器能打开」并不等于
/// 「每一页都能打开」。样式表里写错的 StaticResource 键、绑到不存在的元素名，
/// 这类问题都要到真正打开那一页时才炸。改过样式表或者加过新页面之后跑一遍这个，
/// 比一页页点过去快得多，也不会漏。
///
/// 两种用法：命令行 <c>LMCL.exe --self-check</c>（看退出码，0 = 全过），
/// 或者「工具 → 界面自检」（报告直接弹出来看）。
/// </summary>
internal static class SelfCheck
{
    /// <summary>
    /// 自检是不是正在跑。自检必须离线、快速、可重复：界面里那些「打开窗口就顺手联网」
    /// 的行为（比如账号窗口自动拉一次皮肤档案）看到这个标志就收敛，不去碰网络。
    /// </summary>
    public static bool Running { get; private set; }

    public static SelfCheckResult Execute()
    {
        var log = App.Services.Log;
        var lines = new List<string> { "界面自检：逐个构造页面与对话框", "" };

        Running = true;
        try
        {
            return Run(lines, log);
        }
        finally
        {
            Running = false;
        }
    }

    private static SelfCheckResult Run(List<string> lines, LogBus log)
    {
        // 顺序无所谓，但把主页放第一个，出问题时最先看到。
        // 第三项是可选的断言：构造完顺手验一下「某些关键部分真的显示出来了」，
        // 光看构造没抛异常，是查不出「构造成功但界面一片空白」这种问题的
        //（账号窗口就出过这个事：页签选中事件被重入保护挡掉，右侧面板全是收起的）。
        var probes = new (string Name, Func<FrameworkElement> Build, Func<FrameworkElement, string?>? Check)[]
        {
            ("外壳窗口", () => new ShellWindow(), null),
            ("主页", () => new HomePage(), null),
            ("下载", () => new DownloadPage(), null),
            ("下载 · 社区资源分区", () => new DownloadPage(), page =>
            {
                // 切到资源分区这条路靠 SelectionChanged 驱动，构造成功但切过去一片空白的事故
                // 已经出过一次了，这里必须断言
                var report = ((DownloadPage)page).CatalogProbe();
                if (!report.Contains("PaneCatalog=Visible"))
                    return $"资源分区切过去不可见：{report}";
                if (!report.Contains("空提示=Visible"))
                    return $"列表区没有引导文案，用户看到的是一片空白：{report}";
                return null;
            }),
            // 左边那一列六个分区，逐个点一遍——「点了没反应」这种事故构造阶段查不出来，
            // 只能像这样真的一次次切过去看
            ("下载 · 分区导航", () => new DownloadPage(), page =>
            {
                var report = ((DownloadPage)page).SectionNavProbe();
                return report.Contains("BAD")
                    ? $"有分区点过去没反应：{report}"
                    : null;
            }),
            // 分类下拉是按资源类型重建的，更新面板是默认收起的——
            // 两者都属于「构造得出来但可能一直是空的 / 一直藏着」的毛病
            ("下载 · 筛选与更新面板", () => new DownloadPage(), page =>
            {
                var report = ((DownloadPage)page).FilterProbe();
                return report.Contains("BAD")
                    ? $"筛选或更新面板有问题：{report}"
                    : null;
            }),
            ("版本与资源", () => new VersionsPage(), null),
            // 日志页：它里面那个 LogPanel 订阅的是全局日志总线，
            // 「构造不抛异常」不代表面板真的铺开了，这里顺手确认关键控件都在
            ("运行日志", () => new LogPage(), page =>
                ((LogPage)page).PanelReady
                    ? null
                    : "日志页里的面板或目录提示没有初始化"),
            ("设置（全局设置 / 工具 / 帮助）", () => new SettingsPage(), null),
            // 外观面板：RGB 调色盘和「自定义图片」那两段是条件显示的，
            // 可见性分支写错会造成「选了却调不出来」，构造阶段查不出来
            ("设置 · 外观面板", () => new SettingsPage(), page =>
                ((SettingsPage)page).AppearanceControlsReady()
                    ? null
                    : "外观面板控件不齐，或者选了「自定义」RGB 面板 / 「自定义图片」没出现"),
            // 下面四个现在是被嵌进上面两页的子面板，单独也要过一遍：
            // 嵌入模式会改它们的边距、收起版本选择器，那条路径同样可能写崩
            ("版本设置（子面板）", Embedded(() => new InstancePage()), null),
            ("资源管理（子面板）", Embedded(() => new ResourcesPage()), null),
            ("工具（子面板）", Embedded(() => new ToolsPage()), null),
            ("帮助（子面板）", Embedded(() => new HelpPage()), null),
            ("账号窗口 · 详情模式", () => new AccountWindow(), win =>
                ((AccountWindow)win).DetailPaneVisible
                    ? null
                    : "右侧详情面板不可见 —— 窗口打开会是一片空白"),
            ("账号窗口 · 添加模式",
                () => { var w = new AccountWindow(); w.OpenAddPaneForCheck(); return w; },
                win => ((AccountWindow)win).ExactlyOneLoginPanelVisible
                    ? null
                    : "离线 / 微软 / 外置 三个面板的可见性不是「恰好一个」"),
            // 皮肤管理是独立的一整页，从顶栏的「皮肤 / 披风」按钮进去。
            // 这里断言：入口按钮可见、点进去页面真的切过去了、该亮的卡片都亮了。
            ("账号窗口 · 皮肤披风管理", () => new AccountWindow(), win =>
                ((AccountWindow)win).AppearanceVisibleForMicrosoft()
                    ? null
                    : "微软账号的皮肤页没打开，或者皮肤 / 披风 / 本地库三张卡没有全部显示"),
            // 离线账号没有服务端档案，也不可能有披风：本地皮肤库要亮，
            // 服务端那两块 + 左栏的披风预览都要收
            ("账号窗口 · 本地皮肤库", () => new AccountWindow(), win =>
                ((AccountWindow)win).LocalSkinVisibleForOffline()
                    ? null
                    : "离线账号的皮肤页里，本地皮肤库没显示出来，或者披风相关的那几块没收起"),
            // 第三方账号的材质由服务器决定，顶栏那个入口按钮就该收起来
            ("账号窗口 · 第三方无皮肤入口", () => new AccountWindow(), win =>
                ((AccountWindow)win).SkinEntryHiddenForThirdParty()
                    ? null
                    : "第三方账号仍然显示着「皮肤 / 披风」入口按钮"),
            // 多自定义皮肤：导入两张 → 列表两项；删一张 → 只剩一项。
            // 这条会真的往皮肤库写两张小 PNG，跑完自己清理
            ("账号窗口 · 多自定义皮肤", () => new AccountWindow(), win =>
                ((AccountWindow)win).MultiCustomSkinWorks()
                    ? null
                    : "自定义皮肤加不进列表，或者删掉之后列表没跟着变"),
            // 皮肤页的左栏（预览）和右栏（清单）不能叠在一起。
            // 早先那些「标题 + 操作」的 Grid 漏写 ColumnDefinitions，按钮直接压在
            // 说明文字上——构造阶段不报错，只能靠坐标断言查出来
            ("账号窗口 · 皮肤页版式", () => new AccountWindow(), win =>
                ((AccountWindow)win).SkinLayoutNotOverlapping()
                    ? null
                    : "皮肤页左右两栏重叠了，文字会被操作按钮压住"),
            // 消息框：五种语气各构造一遍。
            // 断言的不是「窗口能造出来」，而是**样式真的套上了** ——
            // 图标、图标颜色、按钮组合都随语气变化，任何一个资源键写错
            // 都会静默退化成灰色问号，只有检查一下才看得见
            ("消息框 · 五种语气", () => new MessageDialog(), win =>
                ((MessageDialog)win).KindsRendered()
                    ? null
                    : "消息框的图标 / 配色 / 按钮组合没有按语气切换"),
            ("文本查看窗口", () => new TextDialog(), null),
            // 光影装到哪由用户在下载时挑，这个对话框不能是空的
            ("选择安装位置窗口", () => new PickTargetDialog(
                App.Services, LMCL.Core.Resources.ResourceKind.ShaderPacks,
                "Shaders.zip", 1024, "示例光影"), win =>
                ((PickTargetDialog)win).HasChoicesOrFallback
                    ? null
                    : "版本列表与「选择其他文件夹」都不给用，用户无从选择"),
        };

        var failures = new List<string>();
        var passed = 0;

        foreach (var (name, build, check) in probes)
        {
            try
            {
                var element = build();

                if (check?.Invoke(element) is { } problem)
                    throw new InvalidOperationException(problem);

                lines.Add($"  [通过] {name}（{element.GetType().Name}）");
                log.Info($"[自检] {name} 构造成功");
                Cleanup(element);
                passed++;
            }
            catch (Exception ex)
            {
                lines.Add($"  [失败] {name}：{ex.GetType().Name}：{ex.Message}");
                log.Error($"[自检] {name} 构造失败：{ex.Message}");
                failures.Add($"{name}：{ex.Message}");
            }
        }

        lines.Add("");
        lines.Add(failures.Count == 0
            ? $"结果：{passed}/{probes.Length} 全部通过。"
            : $"结果：{passed}/{probes.Length} 通过，{failures.Count} 个失败。");

        if (failures.Count == 0) log.Success($"自检通过：{passed} 个界面全部可以正常构造");
        else log.Error($"自检失败：{failures.Count} 个界面有问题");

        // WinExe 没有控制台，命令行模式下只能靠退出码判断，所以报告得留在文件里
        var path = ReportPath();
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(path, string.Join(Environment.NewLine, lines), Encoding.UTF8);
            lines.Add("");
            lines.Add($"报告已写入：{path}");
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add($"（报告写文件失败：{ex.Message}）");
        }

        return new SelfCheckResult(passed, probes.Length, failures, string.Join(Environment.NewLine, lines));
    }

    /// <summary>命令行入口：全过退出码 0，有失败退出码 1。</summary>
    public static int Run() => Execute().Ok ? 0 : 1;

    /// <summary>包一层「造出来之后切到嵌入模式」，用于验证「被宿主嵌进去」这条路径。</summary>
    private static Func<FrameworkElement> Embedded(Func<PageBase> build) => () =>
    {
        var page = build();
        page.UseEmbeddedMode();
        return page;
    };

    private static string ReportPath() => Path.Combine(AppPaths.LogDir, "last-self-check.log");

    /// <summary>窗口用完要关掉，否则进程会因为还有窗口活着而不退出。</summary>
    private static void Cleanup(FrameworkElement element)
    {
        if (element is Window window) window.Close();
    }
}
