using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LMCL.App.Animations;
using LMCL.App.Services;
using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 主页：启动优先 + 上手引导。
///
/// 两条设计原则：
///   1. 启动不需要用户先做任何选择——版本自动取「上次玩过的」，没有就取第一个。
///      下拉框只用于「想换一个」的情况，不是启动的前置条件。
///   2. 缺什么都直接说，并且能在原地解决。缺账号时这里就能建（输名字回车），
///      缺版本时一个按钮直达下载，不用用户自己猜要先干什么、去哪个页面干。
/// </summary>
public partial class HomePage : PageBase
{
    private IReadOnlyList<InstalledVersion> _installed = Array.Empty<InstalledVersion>();

    /// <summary>
    /// 要启动的版本名。没有下拉框了——「上次玩过的」就是默认目标，
    /// 用户想临时换一个就走右下角「版本选择」去版本与资源界面挑。
    /// </summary>
    private string? _selected;

    public HomePage()
    {
        InitializeComponent();
        S.Launcher.Exited += OnGameExited;
        Unloaded += (_, _) => S.Launcher.Exited -= OnGameExited;
    }

    public override void OnNavigatedTo() => Refresh();

    // ── 数据 ────────────────────────────────────────────────

    /// <summary>当前选中的版本；一个版本都没装时为 null。</summary>
    private string? CurrentVersion => _selected;

    private Account? Account => S.Accounts.Selected;

    private void Refresh()
    {
        _installed = Installed();

        // 优先级：上次玩过的 → 第一个装好的
        var target = _installed.FirstOrDefault(v => v.Name == S.Config.Current.LastVersion)
                     ?? _installed.FirstOrDefault();

        _selected = target?.Name;

        // 把实际选中的落回配置：下次启动、别的页面（下载页的「安装到」）都读它
        if (target is not null && S.Config.Current.LastVersion != target.Name)
        {
            S.Config.Current.LastVersion = target.Name;
            S.Config.Save();
        }

        ShowState();
        RefreshStats();
    }

    private void ShowState()
    {
        var account = Account;
        var version = _installed.FirstOrDefault(v => v.Name == CurrentVersion);

        // ── 启动面板 ────────────────────────────────────
        if (version is null)
        {
            BigName.Text = "还没有版本";
            LoaderText.Text = "—";
            IsolatedChip.Visibility = Visibility.Collapsed;
            InfoAccount.Text = account is null
                ? "先在下面加个账号，再去「下载」装一个版本。"
                : $"账号：{account.Name}（{account.TypeLabel}）　·　还差一个游戏版本";
        }
        else
        {
            BigName.Text = version.Name;
            LoaderText.Text = version.LoaderLabel;
            IsolatedChip.Visibility = version.Isolated ? Visibility.Visible : Visibility.Collapsed;
            InfoAccount.Text = account is null
                ? "账号：未登录　·　先在下面加个账号就能启动"
                : $"将使用账号：{account.Name}（{account.TypeLabel}）";
        }

        // ── 启动按钮的文案跟着状态走，永远告诉用户「现在点它会怎样」──
        BtnLaunch.Content = account is null ? "先加个账号"
            : version is null ? "先下载一个版本"
            : S.Launcher.IsRunning ? "结束游戏"
            : "启动游戏";

        // 没有版本时「版本设置」无处可去，禁掉；「版本选择」永远可点（去装一个）
        BtnVersionSettings.IsEnabled = version is not null;

        ShowDetail(version);

        // ── 引导卡：只有缺东西的时候才出现 ──────────────
        var needAccount = account is null;
        var needVersion = _installed.Count == 0;

        GuideCard.Visibility = needAccount || needVersion ? Visibility.Visible : Visibility.Collapsed;
        NoAccountPanel.Visibility = needAccount ? Visibility.Visible : Visibility.Collapsed;
        NoVersionPanel.Visibility = !needAccount && needVersion ? Visibility.Visible : Visibility.Collapsed;

        GuideTitle.Text = needAccount
            ? "三步就能玩上，现在只差第 1 步"
            : "还差最后一步：装一个版本";

        Mark(Step1, Step1Mark, 1, !needAccount);
        Mark(Step2, Step2Mark, 2, !needVersion);
        Mark(Step3, Step3Mark, 3, !needAccount && !needVersion);

        if (needAccount) QuickName.Focus();
    }

    /// <summary>
    /// 铺「游戏详情」那张表。没装版本时收掉表格、只留一句引导。
    ///
    /// 这里读的是版本自己的配置（<see cref="GamePaths.LoadInstance"/>），
    /// 也就是说「内存」显示的是这个版本真正会用的值，而不是全局默认值 ——
    /// 每版本覆盖是这套启动器的一个卖点，详情里必须如实反映。
    /// </summary>
    private void ShowDetail(InstalledVersion? version)
    {
        var hasVersion = version is not null;

        DetailTable.Visibility = hasVersion ? Visibility.Visible : Visibility.Collapsed;
        DetailEmpty.Visibility = hasVersion ? Visibility.Collapsed : Visibility.Visible;

        if (version is null)
        {
            DetailSubtitle.Text = "装好一个版本之后，这里会显示它的详细信息。";
            return;
        }

        var settings = GamePaths.LoadInstance(S.Config.GameDir, version.Name);
        var memoryMb = settings.MaxMemoryMb ?? S.Config.Current.MaxMemoryMb;
        var javaPath = settings.JavaPath ?? "";

        DVersionName.Text = version.Name;
        DBaseVersion.Text = version.BaseVersion.Length > 0 ? version.BaseVersion : "—";
        DLoader.Text = version.Loader == LoaderKind.Vanilla
            ? "原版（不带加载器）"
            : $"{version.LoaderLabel} {version.LoaderVersion}";
        DJava.Text = DescribeJava(javaPath);
        DIsolation.Text = settings.Isolated ? "独立（版本自己的 mods / 存档）" : "跟随全局";
        DMemory.Text = memoryMb > 0 ? $"{memoryMb / 1024.0:0.#} GB（{memoryMb} MB）" : "跟随全局";
        DGameDir.Text = S.Config.GameDir.Length > 0 ? S.Config.GameDir : AppPaths.DefaultGameDir;

        DVersionDir.Text = GamePaths.VersionDir(S.Config.GameDir, version.Name);

        DetailSubtitle.Text = version.Loader == LoaderKind.Vanilla
            ? "这个版本是纯原版，没有装加载器。"
            : $"这个版本用的是 {version.LoaderLabel}，可以装对应加载器的模组。";
    }

    /// <summary>Java 那一格：空 = 让启动器自动挑，要说明白而不是显示一串空白。</summary>
    private static string DescribeJava(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? "自动选择（启动器挑最合适的）"
            : Path.GetFileName(path);

    /// <summary>步骤徽章：已完成就把序号换成对勾、整体淡一点，一眼能看出走到哪了。</summary>
    private static void Mark(Border chip, TextBlock mark, int number, bool done)
    {
        mark.Text = done ? "\uE73E" : number.ToString();
        chip.Opacity = done ? 0.7 : 1.0;
        mark.FontSize = done ? 9 : 11;
    }

    private void RefreshStats()
    {
        // 统计数字滚动着跳出来，比「啪」地直接显示有生气（每次进主页都会重播）
        Animator.AaNumber(StatVersions, 0, _installed.Count, 640, 0, AniEase.OutFluent(AniEasePower.Strong));

        var version = _installed.FirstOrDefault(v => v.Name == CurrentVersion);
        if (version is null)
        {
            foreach (var block in new[] { StatMods, StatPacks, StatMemory })
            {
                Animator.CancelNumber(block);
                block.Text = "—";
            }
            return;
        }

        var settings = GamePaths.LoadInstance(S.Config.GameDir, version.Name);
        var counts = S.Resources.Counts(version.Name, settings.Isolated);
        var memoryGb = (settings.MaxMemoryMb ?? S.Config.Current.MaxMemoryMb) / 1024.0;

        Animator.AaNumber(StatMods, 0, counts.Mods, 640, 60, AniEase.OutFluent(AniEasePower.Strong));
        Animator.AaNumber(StatPacks, 0, counts.Packs + counts.Shaders, 640, 120, AniEase.OutFluent(AniEasePower.Strong));
        Animator.AaNumber(StatMemory, 0, memoryGb, 640, 180, AniEase.OutFluent(AniEasePower.Strong),
            format: "0.#", suffix: " GB");
    }

    // ── 事件 ────────────────────────────────────────────────

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        // 缺前置条件时这个按钮变成了「引导按钮」：点了就去做那件事，
        // 而不是弹一句干巴巴的「还没有版本」让人自己去猜
        if (Account is null)
        {
            QuickName.Focus();
            return;
        }

        if (_installed.Count == 0)
        {
            GoTo("download");
            return;
        }

        if (S.Launcher.IsRunning)
        {
            S.Launcher.Kill();
            Refresh();
            return;
        }

        if (CurrentVersion is not { } version) return;

        await RunAsync(_ => LaunchVersionAsync(version), "启动", BtnLaunch);
        Refresh();
    }

    private void OnGameExited(object? sender, int code) => Dispatcher.Invoke(Refresh);

    private void QuickName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) QuickOffline_Click(sender, e);
    }

    /// <summary>原地建一个离线账号。单人玩是 90% 的情况，不该为此跳一次窗口。</summary>
    private void QuickOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = QuickName.Text.Trim();

        if (name.Length == 0)
        {
            Ui.Warn("起个名字吧，随便什么都行。");
            QuickName.Focus();
            return;
        }

        if (name.Length > 16)
        {
            Ui.Warn("名字最多 16 个字符——这是 Minecraft 自己的限制。");
            return;
        }

        S.Accounts.Add(new Account
        {
            Name = name,
            Type = AccountType.Offline,
            // 离线身份靠名字算 UUID，同一个名字在哪台机器上都是同一个身份
            Uuid = OfflineUuid.ForName(name),
        });

        S.Log.Success($"已添加离线账号 {name}");
        QuickName.Text = "";

        Refresh();
        Ui.Info($"已经可以用「{name}」开玩了。\n\n" +
                "要连正版服或外置登录服的话，点右上角的账号按钮再添一个就行。");
    }

    private void OtherLogin_Click(object sender, RoutedEventArgs e) => OpenAccountWindow();

    private void GoDownload_Click(object sender, RoutedEventArgs e) => GoTo("download");

    /// <summary>「打开游戏目录」：直接开当前游戏目录，找不到就提示。</summary>
    private void OpenGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = S.Config.GameDir.Length > 0 ? S.Config.GameDir : AppPaths.DefaultGameDir;

        try
        {
            Directory.CreateDirectory(dir);
            Ui.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            S.Log.Warn($"打开游戏目录失败：{ex.Message}");
            Ui.Warn($"打不开游戏目录：\n\n{ex.Message}");
        }
    }

    /// <summary>「版本选择」：去版本与资源界面自己挑。</summary>
    private void GoVersions_Click(object sender, RoutedEventArgs e) => GoTo("versions");

    /// <summary>
    /// 「版本设置」：直达当前版本的设置页（隔离开关、Java、启动参数都在那）。
    /// 没装版本时按钮是禁用的，走不到这里；防御性判断留着，别让空引用崩了界面。
    /// </summary>
    private void GoVersionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentVersion is not { } version)
        {
            Ui.Warn("先装一个版本，才能进它的设置页。");
            return;
        }

        GoToVersionSettings(version);
    }
}
