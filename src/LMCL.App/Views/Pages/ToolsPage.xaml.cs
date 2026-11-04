using System.Text;
using System.Windows;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.Tools;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 工具页：启动参数、日志分析、导出日志、清缓存、打开目录、检查更新、系统信息。
/// 都是「出问题时用得上」的东西，所以放在一页，不占导航栏的位置。
/// </summary>
public partial class ToolsPage : PageBase
{
    public ToolsPage()
    {
        InitializeComponent();
    }

    /// <summary>被「设置」页嵌进「工具」分栏时调用。</summary>
    public override void UseEmbeddedMode()
    {
        // 分栏上已经写着「工具」了，这里再顶一个大标题就是重复。
        // 目标版本下拉框要留着——日志分析、启动参数都依赖它。
        PageTitleText.Visibility = Visibility.Collapsed;
        PageRoot.Margin = new Thickness(0);
        PageHeader.Margin = new Thickness(0, 0, 6, 6);
    }

    public override void OnNavigatedTo()
    {
        FillVersions();

        // 系统信息里要列 Java，顺手确保扫过了（有缓存，重复调用几乎没开销）
        _ = S.Java.DetectAsync();
    }

    private void FillVersions()
    {
        var installed = Installed();
        var previous = (VersionCombo.SelectedItem as Entry)?.Name ?? S.Config.Current.LastVersion;

        VersionCombo.ItemsSource = installed
            .Select(v => new Entry(v.Name, $"{v.Name}    ·    {v.Subtitle}"))
            .ToList();

        var entries = VersionCombo.Items.OfType<Entry>().ToList();
        VersionCombo.SelectedItem = entries.FirstOrDefault(e => e.Name == previous)
                                    ?? entries.FirstOrDefault();
    }

    private string? Selected => (VersionCombo.SelectedItem as Entry)?.Name;

    // ── 启动参数 ────────────────────────────────────────────

    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } version)
        {
            Ui.Warn("还没有版本可以生成参数。先去「下载中心」装一个。");
            return;
        }

        await RunAsync(async ct =>
        {
            var command = await S.Launcher.BuildCommandLineAsync(
                new LaunchRequest { Version = version, Account = PreviewAccount() }, Reporter(), ct);

            TextDialog.Present(Window.GetWindow(this), $"启动参数 · {version}", command,
                "这就是真正会执行的那条命令。排查内存改了没生效、Java 选错了之类的问题，看这个最直接。");
        }, "生成启动参数");
    }

    // ── 日志分析 ────────────────────────────────────────────

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var version = Selected;
        if (version is null)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        var isolated = GamePaths.LoadInstance(S.Config.GameDir, version).Isolated;
        var log = LauncherService.FindLatestLog(S.Config.GameDir, version, isolated);

        if (log is null || !File.Exists(log))
        {
            Ui.Warn($"没找到 {version} 的游戏日志。\n\n" +
                    "日志是游戏运行的时候写的，先启动一次再来看。\n" +
                    $"找的位置：{Path.Combine(GamePaths.InstanceRoot(S.Config.GameDir, version, isolated), "logs")}");
            return;
        }

        string[] lines;
        try
        {
            // 游戏日志可能被占用，用共享读的方式打开
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex)
        {
            Ui.Error($"读取日志失败：\n\n{ex.Message}");
            return;
        }

        var analysis = LauncherService.AnalyzeLog(lines);

        var body = new StringBuilder();
        body.AppendLine($"日志文件：{log}");
        body.AppendLine($"共 {lines.Length} 行");
        body.AppendLine();
        body.AppendLine("─── 分析结果 ───");
        body.AppendLine();
        body.AppendLine(analysis);
        body.AppendLine();
        body.AppendLine("─── 日志末尾 60 行 ───");
        body.AppendLine();
        foreach (var line in lines.TakeLast(60)) body.AppendLine(line.TrimEnd('\r'));

        TextDialog.Present(Window.GetWindow(this), $"日志分析 · {version}", body.ToString(),
            "分析只覆盖常见情况，没命中的话把完整日志贴到社区问一下更快。");
    }

    private void OpenGameLogDir_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } version)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        var isolated = GamePaths.LoadInstance(S.Config.GameDir, version).Isolated;
        Ui.OpenFolder(Path.Combine(GamePaths.InstanceRoot(S.Config.GameDir, version, isolated), "logs"));
    }

    // ── 启动器日志 ──────────────────────────────────────────

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        if (S.Log.Recent.Count == 0)
        {
            Ui.Info("现在还没有日志可导出。");
            return;
        }

        try
        {
            var path = S.Log.DumpToFile();
            S.Log.Success($"日志已导出到 {path}");

            if (Ui.Confirm($"已导出到：\n{path}\n\n要打开它所在的文件夹吗？"))
                Ui.RevealFile(path);
        }
        catch (Exception ex)
        {
            Ui.Error($"导出日志失败：\n\n{ex.Message}");
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, S.Log.Recent.Select(x => x.ToString()));
        if (text.Length == 0)
        {
            Ui.Info("现在还没有日志可复制。");
            return;
        }

        Ui.Copy(text);
        ShellStatus.SetText($"已复制 {S.Log.Recent.Count} 条日志");
    }

    // ── 缓存 ────────────────────────────────────────────────

    private void CacheSize_Click(object sender, RoutedEventArgs e)
    {
        var size = VersionService.DirectorySize(AppPaths.CacheDir);
        var files = Directory.Exists(AppPaths.CacheDir)
            ? Directory.GetFiles(AppPaths.CacheDir, "*", SearchOption.AllDirectories).Length
            : 0;

        CacheText.Text = files == 0
            ? "缓存目录是空的，没什么可清的。"
            : $"缓存目录里有 {files} 个文件，共占 {InstalledVersion.FormatSize(size)}。";
    }

    private void CleanCache_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(AppPaths.CacheDir))
        {
            Ui.Info("缓存目录本来就不存在，不用清。");
            return;
        }

        var size = VersionService.DirectorySize(AppPaths.CacheDir);
        if (size == 0)
        {
            Ui.Info("缓存已经是空的了。");
            return;
        }

        if (!Ui.Confirm($"要清掉缓存里的临时文件吗？\n\n" +
                        $"共 {InstalledVersion.FormatSize(size)}。\n\n" +
                        "这里只有整合包下载过程中产生的临时文件，删了不影响已经装好的版本。"))
            return;

        try
        {
            var removed = 0;
            foreach (var file in Directory.GetFiles(AppPaths.CacheDir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); removed++; }
                catch { /* 被占用就跳过，不打断清理 */ }
            }

            CacheText.Text = $"已清理 {removed} 个文件。";
            S.Log.Success($"缓存已清理，删除 {removed} 个文件");
        }
        catch (Exception ex)
        {
            Ui.Error($"清理缓存失败：\n\n{ex.Message}");
        }
    }

    // ── 目录 ────────────────────────────────────────────────

    private void OpenRoot_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LauncherRoot);

    private void OpenGame_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(S.Config.GameDir);

    private void OpenLogDir_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LogDir);

    private void OpenUpdateDir_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.UpdateDir);

    // ── 检查更新 ────────────────────────────────────────────

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var url = S.Config.Current.UpdateUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            Ui.Warn("还没有配置更新清单地址。\n\n" +
                    "去「设置 → 更新」里填一个 GitHub Releases 或者简单 JSON 清单的地址就能用了。");
            return;
        }

        await RunAsync(async ct =>
        {
            UpdateText.Text = "正在检查更新…";
            var info = await S.Updater.CheckAsync(AppServices.Version, url, ct);

            if (info is null)
            {
                UpdateText.Text = $"已经是最新版本（{AppServices.Version}）。";
                ShellStatus.Clear();
                return;
            }

            UpdateText.Text = $"发现新版本 {info.Version}。";

            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n更新说明：\n{info.Notes}";
            if (!Ui.Confirm($"发现新版本 {info.Version}（当前 {AppServices.Version}）。{notes}\n\n现在下载吗？"))
            {
                ShellStatus.Clear();
                return;
            }

            var reporter = Reporter();
            var progress = new Progress<double>(r => ShellStatus.SetRatio(r));
            var path = await S.Updater.DownloadAsync(info, progress, ct);

            ShellStatus.Clear();
            UpdateText.Text = $"已下载到 {path}";
            S.Log.Success($"新版本已下载：{path}");

            if (Ui.Confirm($"新版本已下载到：\n{path}\n\n" +
                           "启动器不会自己替换正在运行的程序。要打开它所在的文件夹吗？"))
                UpdateChecker.RevealInExplorer(path);
        }, "检查更新");
    }

    // ── 系统信息 / 崩溃日志 ─────────────────────────────────

    private void SystemInfo_Click(object sender, RoutedEventArgs e)
    {
        var javas = S.Java.Cached;
        var body = new StringBuilder();

        body.AppendLine($"LMCL 版本：{AppServices.Version}");
        body.AppendLine($"系统：{Environment.OSVersion}");
        body.AppendLine($"64 位系统：{Environment.Is64BitOperatingSystem}");
        body.AppendLine($".NET 运行时：{Environment.Version}");
        body.AppendLine($"处理器：{Environment.ProcessorCount} 核");
        body.AppendLine($"系统语言：{System.Globalization.CultureInfo.CurrentUICulture.Name}");
        body.AppendLine();
        body.AppendLine($"游戏目录：{S.Config.GameDir}");
        body.AppendLine($"启动器目录：{AppPaths.LauncherRoot}");
        body.AppendLine($"下载源：{ConfigService.DescribeSource(S.Config.Current.Source)}");
        body.AppendLine($"下载线程：{S.Config.Current.DownloadThreads}");
        body.AppendLine($"全局内存：{S.Config.Current.MaxMemoryMb} MB（最小 {S.Config.Current.MinMemoryMb} MB）");
        body.AppendLine($"主题：{S.Config.Current.Theme}");
        body.AppendLine($"累计启动次数：{S.Config.Current.LaunchCount}");
        body.AppendLine();
        body.AppendLine($"检测到的 Java（{javas.Count} 个）：");

        if (javas.Count == 0) body.AppendLine("  （无，启动时会用 Mojang 自动下载的运行时）");
        else foreach (var java in javas.OrderByDescending(j => j.Major)) body.AppendLine($"  {java}");

        body.AppendLine();
        body.AppendLine($"已安装版本（{Installed().Count} 个）：");
        foreach (var version in Installed()) body.AppendLine($"  {version.Name}　—　{version.Subtitle}");

        Ui.Copy(body.ToString());
        TextDialog.Present(Window.GetWindow(this), "系统信息", body.ToString(),
            "内容已经复制到剪贴板，反馈问题时直接粘贴即可。");
    }

    private void Crashes_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(AppPaths.LogDir))
        {
            Ui.Info("还没有任何日志目录。");
            return;
        }

        var crashes = Directory.GetFiles(AppPaths.LogDir, "crash-*.log")
            .OrderByDescending(f => f)
            .ToArray();

        if (crashes.Length == 0)
        {
            Ui.Info("没有崩溃日志。启动器到目前为止都挺稳的。");
            return;
        }

        var body = new StringBuilder();
        foreach (var file in crashes)
        {
            body.AppendLine($"════ {Path.GetFileName(file)} ════");
            body.AppendLine($"修改时间：{File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss}");
            body.AppendLine();
            try { body.AppendLine(File.ReadAllText(file)); }
            catch (Exception ex) { body.AppendLine($"（读不出来：{ex.Message}）"); }
            body.AppendLine();
        }

        TextDialog.Present(Window.GetWindow(this), $"崩溃日志（{crashes.Length} 个）", body.ToString(),
            $"来自 {AppPaths.LogDir}");
    }

    private void SelfCheck_Click(object sender, RoutedEventArgs e)
    {
        // 这个自检会把 ToolsPage 自己也再造一个，属于预期行为
        var result = SelfCheck.Execute();

        TextDialog.Present(Window.GetWindow(this),
            result.Ok ? "界面自检 · 全部通过" : $"界面自检 · {result.Failures.Count} 个失败",
            result.Report,
            result.Ok
                ? "所有页面都能正常构造。改过样式表或加了新页面之后，跑一下这个最省事。"
                : "失败的页面在打开时就会报错，把这份报告贴出来就能定位。");
    }

    private sealed record Entry(string Name, string Text)
    {
        // ComboBox 收起时显示 ToString()，DisplayMemberPath 只管展开的列表
        public override string ToString() => Text;
    }
}
