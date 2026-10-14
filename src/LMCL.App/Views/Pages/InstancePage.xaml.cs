using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.Java;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 版本设置：一个版本自己的配置，存成 versions/&lt;版本&gt;/lmcl.json。
/// 凡是留空的项都表示「跟随全局设置」，不是「关掉」——这一点在界面上有明确提示。
/// </summary>
public partial class InstancePage : PageBase
{
    /// <summary>从别的页面指定要配置哪个版本（ShellWindow 那边的跳转会写进来）。</summary>
    private static string? _pending;

    private string? _version;
    private bool _loading;

    public InstancePage()
    {
        InitializeComponent();
    }

    public static void Request(string version) => _pending = version;

    /// <summary>被「版本与资源」页嵌进右栏时调用。</summary>
    public override void UseEmbeddedMode()
    {
        VersionCombo.Visibility = Visibility.Collapsed;
        PageRoot.Margin = new Thickness(0);
        PageHeader.Margin = new Thickness(0, 0, 6, 6);
    }

    /// <summary>由宿主指定要配置哪个版本。刻意复用下拉框那条加载路径，免得两套逻辑慢慢跑偏。</summary>
    public void SetVersion(string version) => FillVersions(version);

    public override void OnNavigatedTo()
    {
        FillVersions();
        _ = FillJavaAsync();
    }

    // ── 版本下拉 ────────────────────────────────────────────

    private void FillVersions(string? wanted = null)
    {
        var installed = Installed();
        var previous = wanted ?? _pending ?? _version ?? S.Config.Current.LastVersion;
        _pending = null;

        _loading = true;
        VersionCombo.ItemsSource = installed
            .Select(v => new Entry(v.Name, $"{v.Name}    ·    {v.Subtitle}"))
            .ToList();
        _loading = false;

        var entries = VersionCombo.Items.OfType<Entry>().ToList();
        var target = entries.FirstOrDefault(e => e.Name == previous) ?? entries.FirstOrDefault();

        if (target is null)
        {
            _version = null;
            SubtitleText.Text = "还没有安装任何版本，先去「下载中心」装一个。";
            SaveHint.Text = "没有可配置的版本。";
            return;
        }

        VersionCombo.SelectedItem = target;
        Load(target.Name);
    }

    private string? Selected => (VersionCombo.SelectedItem as Entry)?.Name;

    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } name) return;
        Load(name);
    }

    // ── 读写配置 ────────────────────────────────────────────

    private void Load(string version)
    {
        _version = version;

        var settings = GamePaths.LoadInstance(S.Config.GameDir, version);
        var installed = Installed().FirstOrDefault(v => v.Name == version);

        SubtitleText.Text = installed?.Subtitle ?? "";

        IsolateSwitch.IsChecked = settings.Isolated;
        FullScreenSwitch.IsChecked = settings.FullScreen;

        WidthBox.Text = settings.WindowWidth > 0 ? settings.WindowWidth.ToString() : "";
        HeightBox.Text = settings.WindowHeight > 0 ? settings.WindowHeight.ToString() : "";

        MaxMemBox.Text = settings.MaxMemoryMb?.ToString() ?? "";
        MinMemBox.Text = settings.MinMemoryMb?.ToString() ?? "";
        MaxMemHint.Text = $"留空则跟全局走（{S.Config.Current.MaxMemoryMb} MB）";

        AutoJavaSwitch.IsChecked = settings.AutoJava ?? S.Config.Current.AutoJava;
        SelectJavaPath(settings.JavaPath ?? "");

        JvmArgsBox.Text = settings.ExtraJvmArgs ?? "";
        GameArgsBox.Text = settings.ExtraGameArgs ?? "";

        var root = GamePaths.InstanceRoot(S.Config.GameDir, version, settings.Isolated);
        PathText.Text = root;
        SaveHint.Text = settings.Isolated
            ? "这个版本开了隔离。"
            : "这个版本跟全局共用 mods 与存档。";
    }

    private InstanceSettings Collect()
    {
        var settings = new InstanceSettings
        {
            Isolated = IsolateSwitch.IsChecked == true,
            FullScreen = FullScreenSwitch.IsChecked == true,
            MaxMemoryMb = ParsePositive(MaxMemBox.Text),
            MinMemoryMb = ParsePositive(MinMemBox.Text),
            AutoJava = AutoJavaSwitch.IsChecked == true,
            JavaPath = (JavaCombo.SelectedItem as JavaOption)?.Path is { Length: > 0 } path ? path : null,
            ExtraJvmArgs = Blank(JvmArgsBox.Text),
            ExtraGameArgs = Blank(GameArgsBox.Text),
            WindowWidth = (int?)ParsePositive(WidthBox.Text) ?? 0,
            WindowHeight = (int?)ParsePositive(HeightBox.Text) ?? 0,
        };

        return settings;
    }

    /// <summary>空/非法一律当作「未设置」而不是 0，否则会把全局值意外覆盖成 0。</summary>
    private static int? ParsePositive(string text) =>
        int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>留空是合法的（表示跟随全局），但填了东西就必须是个像样的数。</summary>
    private static bool Check(string text, string label, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (int.TryParse(text.Trim(), out var value) && value >= min && value <= max) return true;

        Ui.Warn($"{label}要填 {min} 到 {max} 之间的整数，现在填的是「{text.Trim()}」。");
        return false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_version is not { } version)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        var settings = Collect();

        if (!Check(MaxMemBox.Text, "最大内存", 512, 65536)) return;
        if (!Check(MinMemBox.Text, "最小内存", 256, 65536)) return;
        if (!Check(WidthBox.Text, "窗口宽度", 320, 20000)) return;
        if (!Check(HeightBox.Text, "窗口高度", 240, 20000)) return;

        if (settings.MaxMemoryMb is { } max && settings.MinMemoryMb is { } min && min > max)
        {
            Ui.Warn($"最小内存（{min} MB）比最大内存（{max} MB）还大，这样是起不来的。");
            return;
        }

        try
        {
            GamePaths.SaveInstance(S.Config.GameDir, version, settings);

            // 刚打开隔离的话，把资源目录先建出来，省得用户自己去找
            if (settings.Isolated) GamePaths.EnsureVersionDirs(S.Config.GameDir, version, true);

            S.Log.Success($"版本 {version} 的设置已保存");
            Load(version);
            SaveHint.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Ui.Error($"保存失败：\n\n{ex.Message}");
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_version is null) return;
        if (!Ui.Confirm("把这一页全部恢复成默认值吗？（保存后生效）")) return;

        IsolateSwitch.IsChecked = true;
        FullScreenSwitch.IsChecked = false;
        WidthBox.Text = "";
        HeightBox.Text = "";
        MaxMemBox.Text = "";
        MinMemBox.Text = "";
        AutoJavaSwitch.IsChecked = true;
        JavaCombo.SelectedIndex = 0;
        JvmArgsBox.Text = "";
        GameArgsBox.Text = "";
        SaveHint.Text = "已恢复默认值，记得点「保存」。";
    }

    // ── Java ────────────────────────────────────────────────

    private async Task FillJavaAsync(bool force = false)
    {
        try
        {
            var javas = await S.Java.DetectAsync(force);
            var options = new List<JavaOption> { new("", "（不指定，按下面的开关决定）") };
            options.AddRange(javas.Select((j, i) => new JavaOption(j.Path, $"{j.Detail}　{i + 1}")));

            var current = (JavaCombo.SelectedItem as JavaOption)?.Path ?? "";
            JavaCombo.ItemsSource = options;
            SelectJavaPath(current);

            JavaHint.Text = javas.Count == 0
                ? "本机没找到 Java。启动时会自动下载 Mojang 运行时，一般也能跑起来。"
                : $"本机检测到 {javas.Count} 个 Java：{string.Join(" / ", javas.Select(j => j.Major).Distinct().OrderBy(m => m))}。";
        }
        catch (Exception ex)
        {
            JavaHint.Text = $"检测 Java 失败：{ex.Message}";
        }
    }

    private void SelectJavaPath(string path)
    {
        var options = JavaCombo.Items.OfType<JavaOption>().ToList();

        // 配置里写的是自定义路径，列表里没有的话临时插一条，免得它看起来像没设置
        if (!string.IsNullOrEmpty(path) && options.All(o => o.Path != path))
        {
            options.Insert(1, new JavaOption(path, $"自定义　{path}"));
            JavaCombo.ItemsSource = options;
        }

        JavaCombo.SelectedItem = options.FirstOrDefault(o => o.Path == path) ?? options.FirstOrDefault();
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) =>
        await FillJavaAsync(force: true);

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickFiles("Java 可执行文件 (java.exe)|java.exe|可执行文件 (*.exe)|*.exe", multi: false);
        if (files.Length == 0) return;

        var path = files[0];
        SelectJavaPath(path);
        JavaHint.Text = "已选定自定义 Java，记得点「保存」。";
    }

    // ── 其他 ────────────────────────────────────────────────

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        if (_version is not { } version) return;
        Ui.OpenFolder(GamePaths.InstanceRoot(S.Config.GameDir, version, IsolateSwitch.IsChecked == true));
    }

    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (_version is not { } version)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        await RunAsync(async ct =>
        {
            var command = await S.Launcher.BuildCommandLineAsync(
                new LaunchRequest { Version = version, Account = PreviewAccount() }, Reporter(), ct);

            TextDialog.Present(Window.GetWindow(this), "启动参数", command,
                $"版本 {version} · 反映的是已经保存的设置，改了没保存的话这里看不到");
        }, "生成启动参数");
    }

    private sealed record Entry(string Name, string Text)
    {
        // ComboBox 收起时显示 ToString()，DisplayMemberPath 只管展开的列表
        public override string ToString() => Text;
    }

    private sealed record JavaOption(string Path, string Label)
    {
        public override string ToString() => Label;
    }
}
