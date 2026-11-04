using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.Resources;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 资源管理：模组 / 资源包 / 光影 / 存档的查看、启用、导入、删除。
/// 「启用」是靠给文件加 .disabled 后缀实现的，不移动文件——这样用户自己去看目录也一目了然。
/// </summary>
public partial class ResourcesPage : PageBase
{
    private static readonly (ResourceKind Kind, string Filter)[] Kinds =
    [
        (ResourceKind.Mods, "模组文件 (*.jar)|*.jar|所有文件 (*.*)|*.*"),
        (ResourceKind.ResourcePacks, "资源包 (*.zip)|*.zip|所有文件 (*.*)|*.*"),
        (ResourceKind.ShaderPacks, "光影包 (*.zip)|*.zip|所有文件 (*.*)|*.*"),
        (ResourceKind.Saves, "存档 (*.zip)|*.zip|所有文件 (*.*)|*.*"),
    ];

    private readonly ObservableCollection<ResourceRow> _rows = new();
    private string? _version;
    private bool _isolated = true;
    private bool _loading;

    public ResourcesPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Tabs.SelectedIndex = 0;
    }

    public override void OnNavigatedTo() => FillVersions();

    /// <summary>被「版本与资源」页嵌进右栏时调用。</summary>
    public override void UseEmbeddedMode()
    {
        VersionCombo.Visibility = Visibility.Collapsed;

        // 「版本设置」是给独立页面用的跨页跳转；嵌进来之后旁边那个分栏就是版本设置，
        // 再留一个按钮只会让人困惑
        InstanceLink.Visibility = Visibility.Collapsed;

        PageRoot.Margin = new Thickness(0);
        PageHeader.Margin = new Thickness(0, 0, 6, 6);
    }

    /// <summary>由宿主指定要管理哪个版本。隔离状态自己从配置里读，保持单一数据来源。</summary>
    public void SetVersion(string version)
    {
        _version = version;
        _isolated = GamePaths.LoadInstance(S.Config.GameDir, version).Isolated;
        Reload();
    }

    private ResourceKind CurrentKind => Kinds[Math.Max(0, Tabs.SelectedIndex)].Kind;

    // ── 版本 ────────────────────────────────────────────────

    private void FillVersions()
    {
        var installed = Installed();
        var previous = _version ?? S.Config.Current.LastVersion;

        _loading = true;
        VersionCombo.ItemsSource = installed
            .Select(v => new Entry(v.Name, $"{v.Name}    ·    {v.Subtitle}"))
            .ToList();

        var entries = VersionCombo.Items.OfType<Entry>().ToList();
        var target = entries.FirstOrDefault(e => e.Name == previous) ?? entries.FirstOrDefault();

        if (target is null)
        {
            _loading = false;
            _version = null;
            _rows.Clear();
            DirText.Text = "";
            CountText.Text = "";
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "还没有安装任何版本。\n先去「下载中心」装一个。";
            return;
        }

        // 这里选默认值不该再触发一次 Version_Changed，否则会白扫两遍目录
        VersionCombo.SelectedItem = target;
        _loading = false;

        _version = target.Name;
        _isolated = GamePaths.LoadInstance(S.Config.GameDir, target.Name).Isolated;
        Reload();
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || (VersionCombo.SelectedItem as Entry)?.Name is not { } name) return;

        _version = name;
        _isolated = GamePaths.LoadInstance(S.Config.GameDir, name).Isolated;
        Reload();
    }

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Reload();
    }

    private void Instance_Click(object sender, RoutedEventArgs e)
    {
        if (_version is { } version) GoToVersion(version);
        else Ui.Warn("先选一个版本。");
    }

    // ── 列表 ────────────────────────────────────────────────

    private void Reload()
    {
        if (_version is not { } version) return;

        var kind = CurrentKind;
        var directory = S.Resources.DirectoryFor(version, _isolated, kind);
        DirText.Text = _isolated
            ? $"实例目录（已隔离）：{directory}"
            : $"全局目录（未隔离）：{directory}";

        List<ResourceFile> files;
        try
        {
            files = S.Resources.List(version, _isolated, kind).ToList();
        }
        catch (Exception ex)
        {
            Ui.Error($"读取目录失败：\n\n{ex.Message}");
            return;
        }

        var canToggle = kind != ResourceKind.Saves;

        _rows.Clear();
        foreach (var file in files) _rows.Add(new ResourceRow(file, canToggle));

        CountText.Text = $"{ResourceLabels.Of(kind)}：{files.Count} 项"
            + (canToggle ? "　·　关掉开关会把后缀改成 .disabled，游戏就看不到它了" : "");

        EmptyHint.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = kind switch
        {
            ResourceKind.Mods => "这里还没有模组。\n可以直接把 .jar 拖进这个文件夹，或者点「导入文件」。",
            ResourceKind.ResourcePacks => "这里还没有资源包。\n点「导入文件」选 .zip 就行。",
            ResourceKind.ShaderPacks => "这里还没有光影包。\n注意光影还需要对应的模组支持（比如 Iris）。",
            _ => "这里还没有存档。\n点「导入文件」可以导入别人给的存档压缩包。",
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_version is not { } version)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        Ui.OpenFolder(S.Resources.EnsureDirectory(version, _isolated, CurrentKind));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_version is not { } version)
        {
            Ui.Warn("先选一个版本。");
            return;
        }

        var kind = CurrentKind;
        var files = Ui.PickFiles(Kinds[Math.Max(0, Tabs.SelectedIndex)].Filter);
        if (files.Length == 0) return;

        try
        {
            var copied = S.Resources.Import(version, _isolated, kind, files);
            S.Log.Success($"已导入 {copied} 个{ResourceLabels.Of(kind)}文件");
            Reload();

            if (copied < files.Length)
                Ui.Warn($"选了 {files.Length} 个，成功导入 {copied} 个，其余的可能重名或者读不了。");
        }
        catch (Exception ex)
        {
            Ui.Error($"导入失败：\n\n{ex.Message}");
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResourceRow row) return;

        try
        {
            S.Resources.SetEnabled(row.Model, row.Enabled);
            S.Log.Info($"{row.Name} 已{(row.Enabled ? "启用" : "禁用")}");
        }
        catch (Exception ex)
        {
            row.Enabled = !row.Enabled;   // 落盘失败就把开关拨回去，别让界面骗人
            Ui.Error($"{(row.Enabled ? "启用" : "禁用")}失败：\n\n{ex.Message}");
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResourceRow row) return;

        var what = row.Model.IsFolder ? "文件夹" : "文件";
        if (!Ui.Confirm($"确定要删除{what}「{row.Name}」吗？\n\n这一步不会进回收站，删了就没了。"))
            return;

        try
        {
            S.Resources.Delete(row.Model);
            S.Log.Success($"已删除 {row.Name}");
            Reload();
        }
        catch (Exception ex)
        {
            Ui.Error($"删除失败：\n\n{ex.Message}");
        }
    }

    private sealed record Entry(string Name, string Text)
    {
        // ComboBox 收起时显示 ToString()，DisplayMemberPath 只管展开的列表
        public override string ToString() => Text;
    }
}
