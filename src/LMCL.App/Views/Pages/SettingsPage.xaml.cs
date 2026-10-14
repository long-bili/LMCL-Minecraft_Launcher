using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;
using LMCL.Core.Java;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 全局设置 / 工具 / 帮助。
///
/// 这三块原本是三个平级导航项，找「清缓存」要点两次还得认路；现在收成一页的分栏。
/// 工具与帮助是懒加载的：切到那一栏才构造，省掉启动时的开销。
///
/// 全局设置内部又分五个二级分类（目录与下载 / 外观 / 版本 / 性能 / Java）。
/// 所有改动**即时保存**——没有保存按钮，改一个项就落一次盘。
/// </summary>
public partial class SettingsPage : PageBase
{
    private readonly List<SourceChoice> _sources =
    [
        new(DownloadSource.Bmclapi, "BMCLAPI 镜像（国内推荐）"),
        new(DownloadSource.Official, "Mojang 官方源"),
    ];

    private readonly List<ThemeChoice> _themes =
    [
        new("system", "跟随系统"),
        new("light", "亮色"),
        new("dark", "暗色"),
    ];

    /// <summary>强调色。Id 对应 Themes/AccentPalettes.xaml 里的色板 Key，Custom = 用下面的 RGB。</summary>
    private readonly List<AppearanceChoice> _accents =
    [
        new("Blue", "默认蓝"),
        new("Green", "翡翠绿"),
        new("Purple", "紫"),
        new("Orange", "橙"),
        new("Rose", "玫瑰"),
        new("Teal", "青"),
        new("Custom", "自定义（调 RGB）"),
    ];

    private readonly List<AppearanceChoice> _backdrops =
    [
        new("mica", "云母（跟随桌面色调）"),
        new("acrylic", "亚克力（模糊桌面）"),
        new("image", "自定义图片"),
        new("solid", "纯色（最省资源）"),
    ];

    private readonly List<AppearanceChoice> _stretches =
    [
        new("fill", "拉伸铺满（会变形）"),
        new("uniformToFill", "铺满并裁掉多余（推荐）"),
        new("uniform", "完整显示（可能留黑边）"),
    ];

    /// <summary>
    /// Load 和事件处理之间防递归：填控件时会触发 SelectionChanged，那时不能当作用户改动。
    ///
    /// **初值给 true**：XAML 解析阶段控件是按声明顺序逐个建的，Slider 一建出来就可能
    /// 触发 ValueChanged，而此时排在它后面的 TextBlock 还是 null。用这个标志把
    /// 构造期的事件全部挡掉，构造函数末尾才放开。
    /// </summary>
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        SourceCombo.ItemsSource = _sources;
        ThemeCombo.ItemsSource = _themes;
        AccentCombo.ItemsSource = _accents;
        BackdropCombo.ItemsSource = _backdrops;
        StretchCombo.ItemsSource = _stretches;

        // 定初值会触发 Tabs_Changed，正好把三个面板的初始可见性摆好
        Tabs.SelectedIndex = 0;
        CategoryList.SelectedIndex = 0;

        // 控件都建好了，现在才允许事件被当成「用户改动」处理
        _loading = false;
    }

    public override void OnNavigatedTo()
    {
        Load();
    }

    // ── 分栏 ────────────────────────────────────────────────

    /// <summary>
    /// 设置 / 工具 / 帮助 原本是三个平级入口，找「清缓存」要翻半天。
    /// 现在收在一页的三个分栏里，左侧导航也少了两项。
    /// </summary>
    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        var index = Math.Max(0, Tabs.SelectedIndex);

        if (index == 1) EnsureTools();
        if (index == 2) EnsureHelp();

        SettingsPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolsHost.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        HelpHost.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 保存条只对「全局设置」有意义
        SaveBar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>供外壳窗口跳转用（左下角的「关于与帮助」）。</summary>
    public void ShowTab(string tab)
    {
        Tabs.SelectedIndex = tab switch
        {
            "tools" => 1,
            "help" => 2,
            _ => 0,
        };
    }

    // ── 自检探针 ────────────────────────────────────────────

    /// <summary>
    /// 自检用：外观面板的关键部件都在，而且「自定义强调色」能把 RGB 面板亮出来。
    ///
    /// 为什么值得断言：RGB 那三个滑杆和预览块是「选了自定义才显示」的，
    /// 写错一个可见性分支就会出现「选了自定义但滑杆不出现」，
    /// 而构造阶段一切正常、自检不报错。这里主动切一遍并检查。
    /// </summary>
    internal bool AppearanceControlsReady()
    {
        // 用局部变量接一下再判空：编译器对字段的流分析不跨方法调用，
        // 直接写字段会在下面报 CS8602（"解引用可能出现空引用"）
        var customPanel = AccentCustomPanel;
        var imagePanel = BackgroundImagePanel;

        if (customPanel is null || imagePanel is null) return false;
        if (AccentR is null || AccentG is null || AccentB is null) return false;
        if (AccentPreviewSwatch is null) return false;
        if (TransparencySlider is null || BlurSlider is null || BrightnessSlider is null) return false;

        // 切到「自定义」→ RGB 面板必须出现
        var previous = AccentCombo.SelectedItem;
        AccentCombo.SelectedItem = _accents.First(a => a.Id == "Custom");
        var shown = customPanel.Visibility == Visibility.Visible;

        // 切到「自定义图片」→ 图片那一段必须出现
        var previousBackdrop = BackdropCombo.SelectedItem;
        BackdropCombo.SelectedItem = _backdrops.First(b => b.Id == "image");
        var imageShown = imagePanel.Visibility == Visibility.Visible;

        // 复原
        AccentCombo.SelectedItem = previous;
        BackdropCombo.SelectedItem = previousBackdrop;
        SyncAppearancePanels();

        return shown && imageShown;
    }

    /// <summary>二级分类导航：目录与下载 / 外观 / 版本 / 性能 / Java。</summary>
    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var tag = (CategoryList.SelectedItem as ListBoxItem)?.Tag as string ?? "dirs";

        PaneDirs.Visibility = tag == "dirs" ? Visibility.Visible : Visibility.Collapsed;
        PaneLook.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        PaneVersion.Visibility = tag == "version" ? Visibility.Visible : Visibility.Collapsed;
        PaneSpeed.Visibility = tag == "speed" ? Visibility.Visible : Visibility.Collapsed;
        PaneJava.Visibility = tag == "java" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "java" && JavaCombo.ItemsSource is null) _ = FillJavaAsync();
    }

    private void EnsureTools()
    {
        if (ToolsHost.Content is not null) return;

        var tools = new ToolsPage();
        tools.UseEmbeddedMode();
        ToolsHost.Content = tools;
        tools.OnNavigatedTo();
    }

    private void EnsureHelp()
    {
        if (HelpHost.Content is not null) return;

        var help = new HelpPage();
        help.UseEmbeddedMode();
        HelpHost.Content = help;
        help.OnNavigatedTo();
    }

    // ── 读取 ────────────────────────────────────────────────

    private void Load()
    {
        _loading = true;
        var config = S.Config.Current;

        SourceCombo.SelectedItem = _sources.FirstOrDefault(s => s.Source == config.Source) ?? _sources[0];
        ThreadsBox.Text = config.DownloadThreads.ToString();
        ThemeCombo.SelectedItem = _themes.FirstOrDefault(t => t.Id == config.Theme) ?? _themes[0];
        AccentCombo.SelectedItem = _accents.FirstOrDefault(a => a.Id == config.Accent) ?? _accents[0];
        BackdropCombo.SelectedItem = _backdrops.FirstOrDefault(b => b.Id == config.Backdrop) ?? _backdrops[0];
        HideSwitch.IsChecked = config.HideLauncherOnLaunch;
        DefaultIsolateSwitch.IsChecked = config.DefaultIsolate;
        MaxMemBox.Text = config.MaxMemoryMb.ToString();
        MinMemBox.Text = config.MinMemoryMb.ToString();
        AutoJavaSwitch.IsChecked = config.AutoJava;

        // RGB 调色盘 + 背景观感几支滑杆
        AccentR.Value = config.AccentR;
        AccentG.Value = config.AccentG;
        AccentB.Value = config.AccentB;
        TransparencySlider.Value = config.Transparency;
        BlurSlider.Value = config.BlurRadius;
        BrightnessSlider.Value = config.Brightness;

        UpdateAccentPreview();
        UpdateAppearanceLabels();
        UpdateBackgroundPath();
        SyncAppearancePanels();

        _loading = false;
        SaveHint.Text = "";
    }

    /// <summary>几张面板的显隐：自定义强调色才展开 RGB，选了图片才展开图片那一段。</summary>
    private void SyncAppearancePanels()
    {
        var custom = (AccentCombo.SelectedItem as AppearanceChoice)?.Id == "Custom";
        AccentCustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        var image = (BackdropCombo.SelectedItem as AppearanceChoice)?.Id == "image";
        BackgroundImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAppearanceLabels()
    {
        TransparencyText.Text = $"{TransparencySlider.Value * 100:0}%";
        BlurText.Text = $"{BlurSlider.Value:0}";
        BrightnessText.Text = $"{BrightnessSlider.Value:0.00}×";
    }

    private void UpdateBackgroundPath()
    {
        var path = S.Config.Current.BackgroundImage;
        BackgroundPathText.Text = string.IsNullOrWhiteSpace(path)
            ? "还没有选择图片。"
            : path;
        BtnClearBackground.IsEnabled = !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>把当前 RGB 画到预览块和三个数字上。</summary>
    private void UpdateAccentPreview()
    {
        var r = (byte)Math.Clamp(AccentR.Value, 0, 255);
        var g = (byte)Math.Clamp(AccentG.Value, 0, 255);
        var b = (byte)Math.Clamp(AccentB.Value, 0, 255);

        AccentPreviewSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        AccentPreviewText.Text = $"#{r:X2}{g:X2}{b:X2}　RGB({r}, {g}, {b})";
        AccentRText.Text = r.ToString();
        AccentGText.Text = g.ToString();
        AccentBText.Text = b.ToString();
    }

    // ── 即时保存 ────────────────────────────────────────────
    //
    // 没有保存按钮：每个控件改完就存一次盘。设置这种东西还要用户记得按保存，
    // 改完忘了按就白改，纯属找茬。

    /// <summary>存一次盘，顺带刷新侧栏摘要与底部提示。</summary>
    private void SaveSetting(string what)
    {
        try
        {
            S.Config.Save();
        }
        catch (Exception ex)
        {
            Ui.Error($"保存失败（{what}）：\n\n{ex.Message}");
            return;
        }

        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshSideInfo();
        SaveHint.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
    }

    private void SourceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.Source = (SourceCombo.SelectedItem as SourceChoice)?.Source ?? DownloadSource.Bmclapi;
        SaveSetting("下载源");
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.Theme = (ThemeCombo.SelectedItem as ThemeChoice)?.Id ?? "system";
        ThemeManager.Apply(S.Config.Current.Theme);
        SaveSetting("主题");
    }

    private void AccentCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var config = S.Config.Current;
        config.Accent = (AccentCombo.SelectedItem as AppearanceChoice)?.Id ?? "Blue";

        SyncAppearancePanels();
        ThemeManager.ApplyAccent(config.Accent, config.AccentR, config.AccentG, config.AccentB);
        ThemeManager.ReapplyTransparency();
        SaveSetting("强调色");
    }

    /// <summary>
    /// RGB 滑杆。拖动过程中只做实时预览、**不写盘**——拖一次会触发几十次 ValueChanged，
    /// 每次都存盘既慢又没意义；松手（DragCompleted / LostMouseCapture）才落库。
    /// </summary>
    private void AccentRgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 构造期先挡掉：XAML 还在逐个建控件，排在后面的预览块 / 数字标签可能还是 null
        if (_loading) return;

        UpdateAccentPreview();

        var config = S.Config.Current;
        config.AccentR = (int)Math.Round(AccentR.Value);
        config.AccentG = (int)Math.Round(AccentG.Value);
        config.AccentB = (int)Math.Round(AccentB.Value);

        // 拖滑杆时应当自动切到「自定义」——否则改了 RGB 却不生效，很像坏了
        if (config.Accent != "Custom")
        {
            config.Accent = "Custom";
            _loading = true;
            AccentCombo.SelectedItem = _accents.First(a => a.Id == "Custom");
            _loading = false;
            SyncAppearancePanels();
        }

        ThemeManager.ApplyAccent("Custom", config.AccentR, config.AccentG, config.AccentB);
        ThemeManager.ReapplyTransparency();

        // 存盘放到松手时（见 AccentSlider_DragCompleted），这里只更新提示
        SaveHint.Text = "正在调整强调色…";
    }

    /// <summary>滑杆松手：这才真正写盘。</summary>
    private void AccentSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveSetting("强调色");
    }

    /// <summary>通透度 / 模糊 / 明暗三支滑杆松手后落盘（拖动过程里只实时预览）。</summary>
    private void AppearanceSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveSetting("外观");
    }

    private void BackdropCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.Backdrop = (BackdropCombo.SelectedItem as AppearanceChoice)?.Id ?? "mica";

        SyncAppearancePanels();

        if (Window.GetWindow(this) is ShellWindow shell) shell.ReapplyBackdrop();
        SaveSetting("背景效果");
    }

    private void StretchCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.BackgroundStretch = (StretchCombo.SelectedItem as AppearanceChoice)?.Id ?? "uniformToFill";
        if (Window.GetWindow(this) is ShellWindow shell) shell.ReapplyBackdrop();
        SaveSetting("背景铺法");
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickFiles(
            "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
            multi: false);

        if (files.Length == 0) return;

        S.Config.Current.BackgroundImage = files[0];

        // 选了图片就顺手把背景效果切到「自定义图片」，否则图选了却不显示，很费解
        if (S.Config.Current.Backdrop != "image")
        {
            S.Config.Current.Backdrop = "image";
            _loading = true;
            BackdropCombo.SelectedItem = _backdrops.First(b => b.Id == "image");
            _loading = false;
        }

        SyncAppearancePanels();
        UpdateBackgroundPath();

        if (Window.GetWindow(this) is ShellWindow shell) shell.ReapplyBackdrop();
        SaveSetting("背景图片");
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        S.Config.Current.BackgroundImage = "";
        UpdateBackgroundPath();

        if (Window.GetWindow(this) is ShellWindow shell) shell.ReapplyBackdrop();
        SaveSetting("背景图片");
    }

    private void Transparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        UpdateAppearanceLabels();
        S.Config.Current.Transparency = TransparencySlider.Value;
        ThemeManager.ApplyTransparency(TransparencySlider.Value);
        SaveHint.Text = "正在调整通透度…";
    }

    private void Blur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        UpdateAppearanceLabels();
        S.Config.Current.BlurRadius = BlurSlider.Value;

        // 模糊是自己按像素算的，所以只重算背景图，不重铺窗口材质
        if (Window.GetWindow(this) is ShellWindow shell) shell.ReprepareBackground();
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        UpdateAppearanceLabels();
        S.Config.Current.Brightness = BrightnessSlider.Value;
        if (Window.GetWindow(this) is ShellWindow shell) shell.ReapplyBackdrop();
        SaveHint.Text = "正在调整背景明暗…";
    }

    private void HideSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.HideLauncherOnLaunch = HideSwitch.IsChecked == true;
        SaveSetting("启动后最小化");
    }

    private void DefaultIsolateSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.DefaultIsolate = DefaultIsolateSwitch.IsChecked == true;
        SaveSetting("新版本默认独立");
    }

    private void AutoJavaSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.AutoJava = AutoJavaSwitch.IsChecked == true;
        SaveSetting("自动选择 Java");
    }

    private void JavaCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        S.Config.Current.JavaPath = (JavaCombo.SelectedItem as JavaOption)?.Path is { Length: > 0 } path ? path : "";
        SaveSetting("Java 路径");
    }

    private void ThreadsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!Check(ThreadsBox.Text, "同时下载文件数", 1, 32))
        {
            ThreadsBox.Text = S.Config.Current.DownloadThreads.ToString();
            return;
        }

        S.Config.Current.DownloadThreads = int.Parse(ThreadsBox.Text.Trim());
        SaveSetting("下载线程数");
    }

    /// <summary>内存两个框互相校验：最小大于最大时游戏起不来，拦下来并把最小值压回最大值。</summary>
    private void MaxMemBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!Check(MaxMemBox.Text, "最大内存", 512, 65536))
        {
            MaxMemBox.Text = S.Config.Current.MaxMemoryMb.ToString();
            return;
        }

        var max = int.Parse(MaxMemBox.Text.Trim());
        var min = int.Parse(MinMemBox.Text.Trim());
        if (min > max)
        {
            MinMemBox.Text = max.ToString();
            S.Config.Current.MinMemoryMb = max;
        }

        S.Config.Current.MaxMemoryMb = max;
        SaveSetting("最大内存");
    }

    private void MinMemBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!Check(MinMemBox.Text, "最小内存", 256, 65536))
        {
            MinMemBox.Text = S.Config.Current.MinMemoryMb.ToString();
            return;
        }

        var min = int.Parse(MinMemBox.Text.Trim());
        var max = int.Parse(MaxMemBox.Text.Trim());
        if (min > max)
        {
            Ui.Warn($"最小内存（{min} MB）比最大内存（{max} MB）还大，这样是起不来的。");
            MinMemBox.Text = S.Config.Current.MinMemoryMb.ToString();
            return;
        }

        S.Config.Current.MinMemoryMb = min;
        SaveSetting("最小内存");
    }

    // ── 恢复默认 ────────────────────────────────────────────

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("把所有设置恢复成默认值吗？")) return;

        var config = S.Config.Current;
        var previousGameDir = config.GameDir;

        config.Source = DownloadSource.Bmclapi;
        config.DownloadThreads = 8;
        config.Theme = "system";
        config.Accent = "Blue";
        config.AccentR = 0;
        config.AccentG = 95;
        config.AccentB = 184;
        config.Backdrop = "mica";
        config.BackgroundImage = "";
        config.BackgroundStretch = "uniformToFill";
        config.Transparency = 0.7;
        config.BlurRadius = 0;
        config.Brightness = 1.0;
        config.HideLauncherOnLaunch = true;
        config.DefaultIsolate = true;
        config.MaxMemoryMb = 4096;
        config.MinMemoryMb = 512;
        config.AutoJava = true;
        config.JavaPath = "";

        SaveSetting("恢复默认");

        ThemeManager.Apply(config.Theme);
        ThemeManager.ApplyAccent(config.Accent, config.AccentR, config.AccentG, config.AccentB);
        ThemeManager.ApplyTransparency(config.Transparency);
        if (Window.GetWindow(this) is ShellWindow shell)
        {
            shell.ReapplyBackdrop();
            if (previousGameDir != config.GameDir) shell.RefreshAll();
        }

        Load();
        Ui.Info("已恢复默认设置。");
    }

    // ── Java ────────────────────────────────────────────────

    private async Task FillJavaAsync(bool force = false)
    {
        try
        {
            var javas = await S.Java.DetectAsync(force);
            var options = new List<JavaOption> { new("", "（不指定，按自动选择开关决定）") };
            options.AddRange(javas.Select(j => new JavaOption(j.Path, $"{j.Detail}　{j.Path}")));

            JavaCombo.ItemsSource = options;
            SelectJavaPath(S.Config.Current.JavaPath);

            JavaHint.Text = javas.Count == 0
                ? "本机没找到 Java，启动时会用自动下载的运行时。"
                : $"检测到 {javas.Count} 个 Java：{string.Join(" / ", javas.Select(j => j.Major).Distinct().OrderBy(m => m))}。";
        }
        catch (Exception ex)
        {
            JavaHint.Text = $"检测 Java 失败：{ex.Message}";
        }
    }

    private void SelectJavaPath(string path)
    {
        var options = JavaCombo.Items.OfType<JavaOption>().ToList();

        if (!string.IsNullOrEmpty(path) && options.All(o => o.Path != path))
        {
            options.Insert(1, new JavaOption(path, $"自定义　{path}"));
            JavaCombo.ItemsSource = options;
        }

        JavaCombo.SelectedItem = options.FirstOrDefault(o => o.Path == path) ?? options.FirstOrDefault();
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await FillJavaAsync(true);

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickFiles("Java 可执行文件 (java.exe)|java.exe|可执行文件 (*.exe)|*.exe", multi: false);
        if (files.Length == 0) return;

        SelectJavaPath(files[0]);
        JavaHint.Text = "已选定自定义 Java。";
    }

    // ── 杂项 ────────────────────────────────────────────────

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            Ui.Info("配置文件还没生成，保存一次设置就有了。");
            return;
        }

        Ui.RevealFile(AppPaths.ConfigFile);
    }

    private static bool Check(string text, string label, int min, int max)
    {
        if (int.TryParse(text.Trim(), out var value) && value >= min && value <= max) return true;

        Ui.Warn($"{label}要填 {min} 到 {max} 之间的整数。");
        return false;
    }

    // ComboBox 收起时显示 ToString()，DisplayMemberPath 只管展开的列表
    private sealed record SourceChoice(DownloadSource Source, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ThemeChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AppearanceChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record JavaOption(string Path, string Label)
    {
        public override string ToString() => Label;
    }
}
