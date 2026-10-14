using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LMCL.App.Animations;
using LMCL.App.Services;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 版本与资源：左边列版本，右边是选中版本的详情（概览 / 版本设置 / 资源管理）。
///
/// 这一页把原来三个平级入口收成了一处：选版本、改设置、管模组、看目录、删除，
/// 全在同一个上下文里完成，不用来回跳页。
///
/// 里面嵌的就是原本的 <see cref="InstancePage"/> 与 <see cref="ResourcesPage"/>——
/// 那两页的 XAML 与逻辑一行没改，只是调 UseEmbeddedMode() 收掉它们自己的版本选择器，
/// 并由这里用 SetVersion() 喂当前版本。这样既省了一大坨重复代码，也不会有两处选择打架。
///
/// 体积统计要走后台：一个实例动辄上万个文件，同步扫会明显卡顿。
/// </summary>
public partial class VersionsPage : PageBase
{
    private readonly ObservableCollection<VersionRow> _rows = new();

    private List<VersionRow> _all = new();
    private int _measureToken;
    private bool _switchingTabs;

    private InstancePage? _instance;
    private ResourcesPage? _resources;

    public VersionsPage()
    {
        InitializeComponent();

        List.ItemsSource = _rows;
        SearchBox.TextChanged += (_, _) => ApplyFilter();

        // 构造函数里定初值不该触发加载：那时候还没有选中的版本
        _switchingTabs = true;
        Tabs.SelectedIndex = 0;
        _switchingTabs = false;

        S.Launcher.Exited += OnGameExited;
        Unloaded += (_, _) => S.Launcher.Exited -= OnGameExited;
    }

    public override void OnNavigatedTo() => Reload();

    private string? Selected => (List.SelectedItem as VersionRow)?.Name;

    // ── 列表 ────────────────────────────────────────────────

    private void Reload()
    {
        var previous = Selected;

        _all = Installed().Select(v => new VersionRow(v)).ToList();
        ApplyFilter();

        // 尽量保持原来选中的版本；它没了就落到第一个
        var target = _all.FirstOrDefault(r => r.Name == previous)
                     ?? _all.FirstOrDefault(r => r.Name == S.Config.Current.LastVersion)
                     ?? _all.FirstOrDefault();

        List.SelectedItem = target;
        ShowDetail(target);
        MarkCurrent();

        FillGameDirChoices(GameDirCombo);

        _ = MeasureSizesAsync();
    }

    /// <summary>从别处跳过来时指定要选中的版本（ShellWindow.GoToVersion）。</summary>
    public void Select(string version)
    {
        var row = _all.FirstOrDefault(r => r.Name == version);
        if (row is null)
        {
            Reload();
            row = _all.FirstOrDefault(r => r.Name == version);
        }

        List.SelectedItem = row;
        ShowDetail(row);
        MarkCurrent();
    }

    /// <summary>选中版本并直接打开它的设置页。主页「版本设置」按钮用。</summary>
    public void SelectAndShowSettings(string version)
    {
        Select(version);
        // 切 SelectedIndex 会自己触发 Tabs_Changed，不用手动调处理器
        Tabs.SelectedIndex = 1;
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();

        var matched = string.IsNullOrEmpty(keyword)
            ? _all
            : _all.Where(r =>
                r.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.LoaderLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.Model.BaseVersion.Contains(keyword, StringComparison.OrdinalIgnoreCase))
              .ToList();

        _rows.Clear();
        foreach (var row in matched) _rows.Add(row);

        CountText.Text = _all.Count == 0
            ? "还没有版本"
            : string.IsNullOrEmpty(keyword)
                ? $"共 {_all.Count} 个版本"
                : $"匹配 {matched.Count} / {_all.Count} 个版本";

        EmptyHint.Visibility = _all.Count == 0 || matched.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (_all.Count == 0)
            EmptyHint.Text = "这里还没有版本。\n点下面的「下载新版本」装一个吧。";
        else if (matched.Count == 0)
            EmptyHint.Text = "没有匹配的版本。";
    }

    /// <summary>刷新每行「当前」标记，让列表里哪个是首页版本一目了然。</summary>
    private void MarkCurrent()
    {
        var current = S.Config.Current.LastVersion;
        foreach (var row in _rows) row.IsCurrent = row.Name == current;
    }

    /// <summary>
    /// 体积统计。一个版本目录动辄上万个文件，必须在后台线程算，
    /// 算完再回填到行上——用 token 防止「刷新过好几轮之后旧结果才回来」把新数据覆盖掉。
    /// </summary>
    private async Task MeasureSizesAsync()
    {
        var token = ++_measureToken;
        var snapshot = _all;
        if (snapshot.Count == 0) return;

        await Task.Run(() => S.Versions.FillSizes(snapshot.Select(r => r.Model)));

        // 期间用户可能已经刷新过了，那就别再往旧行上写
        if (token != _measureToken) return;

        foreach (var row in snapshot) row.SizeText = row.Model.SizeText;

        if (Selected is { } name && _all.FirstOrDefault(r => r.Name == name) is { } current)
            InfoSize.Text = current.Model.SizeText;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is VersionRow row) ShowDetail(row);
    }

    // ── 详情 ────────────────────────────────────────────────

    private void ShowDetail(VersionRow? row)
    {
        if (row is null)
        {
            NoSelection.Visibility = Visibility.Visible;
            Detail.Visibility = Visibility.Collapsed;
            UpdateLaunchButton();
            return;
        }

        NoSelection.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        var model = row.Model;
        var settings = GamePaths.LoadInstance(S.Config.GameDir, model.Name);

        DetailName.Text = model.Name;
        DetailLoader.Text = model.LoaderLabel;
        DetailIsolatedChip.Visibility = model.Isolated ? Visibility.Visible : Visibility.Collapsed;
        DetailMeta.Text = row.Meta;

        InfoBase.Text = string.IsNullOrEmpty(model.BaseVersion) ? model.Name : model.BaseVersion;
        InfoLoader.Text = model.Loader == LoaderKind.Vanilla
            ? "原版（没有加载器）"
            : $"{model.LoaderLabel} {model.LoaderVersion}".Trim();
        InfoSize.Text = model.SizeBytes > 0 ? model.SizeText : "统计中…";
        InfoTime.Text = $"{model.InstalledAt:yyyy-MM-dd HH:mm}";
        InfoDir.Text = GamePaths.InstanceRoot(S.Config.GameDir, model.Name, model.Isolated);

        var max = settings.MaxMemoryMb ?? S.Config.Current.MaxMemoryMb;
        var min = settings.MinMemoryMb ?? S.Config.Current.MinMemoryMb;
        InfoMemory.Text = settings.MaxMemoryMb is null
            ? $"最大 {max} MB（跟随全局）· 最小 {min} MB"
            : $"最大 {max} MB（这个版本单独设置的）· 最小 {min} MB";

        UpdateLaunchButton();

        // 已经建好的嵌入面板要跟着换版本
        _instance?.SetVersion(model.Name);
        _resources?.SetVersion(model.Name);
    }

    private void UpdateLaunchButton() =>
        BtnLaunch.Content = S.Launcher.IsRunning ? "结束游戏" : "启动";

    private void OnGameExited(object? sender, int code) =>
        Dispatcher.Invoke(UpdateLaunchButton);

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingTabs) return;

        var index = Math.Max(0, Tabs.SelectedIndex);

        if (index == 1) EnsureInstance();
        if (index == 2) EnsureResources();

        OverviewPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstanceHost.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ResourceHost.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 右栏页签切换：新面板的内容依次浮入。
        // 子面板是嵌进来的，走它们自己的 ReplayEnterAnimation（默认动画在 PageBase）
        switch (index)
        {
            case 0 when OverviewPane.Content is Panel overview:
                Animator.AaStack(overview, 90, 20, 10);
                break;
            case 1 when _instance is { } instance:
                instance.ReplayEnterAnimation();
                break;
            case 2 when _resources is { } resources:
                resources.ReplayEnterAnimation();
                break;
        }
    }

    private void EnsureInstance()
    {
        if (_instance is not null)
        {
            if (Selected is { } name) _instance.SetVersion(name);
            return;
        }

        _instance = new InstancePage();
        _instance.UseEmbeddedMode();
        InstanceHost.Content = _instance;

        if (Selected is { } version) _instance.SetVersion(version);
    }

    private void EnsureResources()
    {
        if (_resources is not null)
        {
            if (Selected is { } name) _resources.SetVersion(name);
            return;
        }

        _resources = new ResourcesPage();
        _resources.UseEmbeddedMode();
        ResourceHost.Content = _resources;

        if (Selected is { } version) _resources.SetVersion(version);
    }

    // ── 操作 ────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>
    /// 把某个版本「选为首页版本」：写进 LastVersion 并落盘，然后跳回首页——
    /// 首页的启动目标就是 LastVersion，回去就会立刻显示并启动这个版本。
    /// 按钮在列表项的图标上，点击源的 DataContext 就是对应的 VersionRow。
    /// </summary>
    private void ChooseForHome_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VersionRow row) return;

        S.Config.Current.LastVersion = row.Name;
        S.Config.Save();

        MarkCurrent();
        S.Log.Success($"已把「{row.Name}」设为首页版本");

        GoTo("home");
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFolder(GamePaths.VersionsDir(S.Config.GameDir));

    /// <summary>
    /// 点「移除」叉时置位。
    ///
    /// 为什么需要这个标志：鼠标按下时 ComboBox **会先把这一项选中**，
    /// 于是 SelectionChanged 先于按钮的 Click 触发，
    /// `e.Handled = true` 那时候已经晚了 —— 目录已经被切过去了。
    /// 所以在 PreviewMouseLeftButtonDown（隧道阶段，比选中更早）就把标志立起来，
    /// 让 GameDirCombo_Changed 直接跳过这一次。
    /// </summary>
    private bool _removingGameDir;

    private void GameDirRemove_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _removingGameDir = true;

    /// <summary>
    /// 下拉里某一项的「移除」叉被点了。
    ///
    /// 关键是**不能让这次点击顺带把目录切过去**：点击落在 ComboBoxItem 内部，
    /// ListBox 默认会把这一项选中（于是触发 SelectionChanged → 切换目录），
    /// 而用户想做的只是「把它从列表里去掉」。
    /// </summary>
    private void GameDirRemove_Click(object sender, RoutedEventArgs e)
    {
        // 别让这次点击冒泡上去、把这一项选成当前目录
        e.Handled = true;

        // 无论后面走哪条分支，标志都要复位
        try
        {
            HandleGameDirRemove(sender);
        }
        finally
        {
            _removingGameDir = false;
        }
    }

    private void HandleGameDirRemove(object sender)
    {
        if (sender is not FrameworkElement { DataContext: GameDirChoice choice }) return;

        // 默认位置不是真实目录，没什么可移除的
        if (choice.IsDefault) return;

        var dialog = new RemoveGameDirDialog(choice.Path)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true) return;

        switch (dialog.Removal)
        {
            case GameDirRemoval.ListOnly:
                RemoveFromList(choice.Path);
                break;

            case GameDirRemoval.DeleteFolder:
                DeleteFolder(choice.Path);
                break;
        }
    }

    /// <summary>只从列表去掉，磁盘不动。</summary>
    private void RemoveFromList(string path)
    {
        var wasCurrent = string.Equals(S.Config.Current.GameDir, path, StringComparison.OrdinalIgnoreCase);

        if (!RemoveGameDirFromList(path))
        {
            // 没改到说明它本来就不在列表里（比如当前目录但没登记过）
            FillGameDirChoices(GameDirCombo);
            return;
        }

        Reload();
        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshAll();

        S.Log.Success($"已从列表移除游戏目录：{path}");
        Ui.Info(wasCurrent
            ? $"已移除，并把当前游戏目录切回默认位置。\n\n文件夹还在原处，随时可以用「浏览…」加回来。"
            : "已从列表移除。文件夹还在原处，随时可以用「浏览…」加回来。");
    }

    /// <summary>
    /// 连文件夹一起删。用户已经在对话框里确认过一次，这里不再重复问，
    /// 但**先把它从列表摘掉**再删：万一删到一半失败，也不会留下一个
    /// 「列表里还指着一个残缺目录」的状态。
    /// </summary>
    private void DeleteFolder(string path)
    {
        // 正在被游戏用着的话删不掉（文件被占用），先提醒
        if (S.Launcher.IsRunning)
        {
            Ui.Warn("游戏正在运行，文件夹被占用，没法删除。\n\n先结束游戏再试。");
            return;
        }

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            S.Log.Error($"删除游戏目录失败：{ex.Message}");
            Ui.Error($"删除失败：\n\n{ex.Message}\n\n" +
                     "常见原因：目录里还有程序在用它（游戏没退干净、资源管理器停在那个目录）。");
            return;
        }

        RemoveGameDirFromList(path);
        Reload();
        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshAll();

        S.Log.Success($"已删除游戏目录：{path}");
        Ui.Info($"已删除：\n\n{path}");
    }

    /// <summary>切换游戏目录：即时生效，无需保存。换完整个页面重读版本列表。</summary>
    private void GameDirCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 这次选中是「点移除叉」顺带造成的，不是用户想切目录 —— 跳过
        if (_removingGameDir)
        {
            _removingGameDir = false;
            return;
        }

        if (GameDirCombo.SelectedItem is not GameDirChoice choice) return;

        // 目标不存在就提示，但不强制阻止——用户可能把移动硬盘拔了想先看看
        if (choice.Path.Length > 0 && !Directory.Exists(choice.Path)
            && !Ui.Confirm($"目录不存在：\n{choice.Path}\n\n还是要切换过去吗？"))
        {
            FillGameDirChoices(GameDirCombo);
            return;
        }

        if (!TrySwitchGameDir(choice)) return;

        Reload();
        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshAll();
        S.Log.Success($"已切换游戏目录：{(choice.Path.Length == 0 ? "默认位置" : choice.Path)}");
    }

    /// <summary>打开文件资源管理器自由选择版本文件夹，选中后立即切换过去。</summary>
    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var picked = Ui.PickFolder(S.Config.Current.GameDir);
        if (picked is null) return;

        if (!TrySwitchGameDir(new GameDirChoice(picked, picked))) return;

        Reload();
        if (Window.GetWindow(this) is ShellWindow shell) shell.RefreshAll();
        S.Log.Success($"已切换游戏目录：{picked}");
    }

    private void Download_Click(object sender, RoutedEventArgs e) => GoTo("download");

    /// <summary>切到「版本设置」分栏。改 SelectedIndex 会自己触发 SelectionChanged，不用手动调处理器。</summary>
    private void GoSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (S.Launcher.IsRunning)
        {
            S.Launcher.Kill();
            UpdateLaunchButton();
            return;
        }

        if (Selected is not { } version) return;

        await RunAsync(_ => LaunchVersionAsync(version), "启动", BtnLaunch);
        UpdateLaunchButton();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } version) return;

        var settings = GamePaths.LoadInstance(S.Config.GameDir, version);
        Ui.OpenFolder(GamePaths.InstanceRoot(S.Config.GameDir, version, settings.Isolated));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } version) return;

        var row = _all.FirstOrDefault(r => r.Name == version);
        var isolated = row?.Isolated ?? true;
        var size = row?.SizeText ?? "未知";

        // 隔离的版本把 mods / 存档都放在版本目录里，删版本等于连存档一起删——
        // 这一点必须在确认框里说清楚，不能只写「删除版本」。
        var warning = isolated
            ? "\n\n⚠ 这个版本开了隔离，它自己的 mods、资源包和存档都在版本目录里，" +
              "删除后这些内容会一并丢失，无法恢复。"
            : "\n\n这个版本没有开隔离，只会删掉版本本身，共用的 mods 与存档不受影响。";

        if (!Ui.Confirm($"确定要删除版本「{version}」吗？\n\n占用空间：{size}{warning}"))
            return;

        try
        {
            var freed = S.Versions.Delete(version);
            S.Log.Success($"已删除 {version}，释放 {InstalledVersion.FormatSize(freed)}");
            Reload();
        }
        catch (Exception ex)
        {
            Ui.Error($"删除失败：\n\n{ex.Message}");
        }
    }
}
