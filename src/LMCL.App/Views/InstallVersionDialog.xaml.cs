using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;

namespace LMCL.App.Views;

/// <summary>
/// 「装哪个版本」的对话框。下载页点一条版本就弹出来：挑加载器、挑加载器版本，然后开始装。
/// 返回 <see cref="Request"/> 为 null 表示用户取消了。
/// </summary>
public partial class InstallVersionDialog : Window
{
    private readonly RemoteVersion _version;
    private readonly AppServices _services;
    private readonly List<LoaderChoice> _loaders =
    [
        new(LoaderKind.Vanilla, "原版（不带加载器）"),
        new(LoaderKind.Forge, "Forge"),
        new(LoaderKind.NeoForge, "NeoForge"),
        new(LoaderKind.Fabric, "Fabric"),
        new(LoaderKind.Quilt, "Quilt"),
    ];

    private List<LoaderOption> _options = new();
    private bool _ready;

    /// <summary>用户点了「开始安装」时给出的安装请求。</summary>
    public InstallRequest? Request { get; private set; }

    public InstallVersionDialog(RemoteVersion version, AppServices services)
    {
        _version = version;
        _services = services;

        InitializeComponent();

        TargetName.Text = version.Name;
        TargetType.Text = version.TypeLabel;
        TargetDate.Text = $"发布于 {version.ReleaseText}";

        LoaderCombo.ItemsSource = _loaders;
        LoaderCombo.SelectedIndex = 0;
        _ready = true;

        ShowVanillaState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 少了这一步，窗口背景会沿用被主窗口改成透明的 WindowBrush，
        // 整个对话框渲染成一片透亮的窟窿——背景设成什么颜色都看不出来
        WindowEffects.ApplyDialogBackdrop(this);
    }

    private void ShowVanillaState()
    {
        LoaderVersionCombo.ItemsSource = null;
        LoaderVersionCombo.IsEnabled = false;
        BtnReloadLoader.IsEnabled = false;
        LoaderHint.Text = "原版不需要加载器，装完就能直接玩。";
    }

    private LoaderKind CurrentLoader =>
        (LoaderCombo.SelectedItem as LoaderChoice)?.Kind ?? LoaderKind.Vanilla;

    private void Loader_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _ = ReloadLoaderVersionsAsync();
    }

    private void ReloadLoader_Click(object sender, RoutedEventArgs e) => _ = ReloadLoaderVersionsAsync();

    private async Task ReloadLoaderVersionsAsync()
    {
        var kind = CurrentLoader;

        if (kind == LoaderKind.Vanilla)
        {
            ShowVanillaState();
            return;
        }

        LoaderVersionCombo.IsEnabled = true;
        BtnReloadLoader.IsEnabled = true;
        LoaderVersionCombo.ItemsSource = null;
        LoaderHint.Text = "正在读取加载器版本…";

        try
        {
            var versions = await _services.Install.GetLoaderVersionsAsync(kind, _version.Name);

            if (versions.Count == 0)
            {
                LoaderHint.Text = $"{LoaderLabels.Of(kind)} 在这个游戏版本上没有可安装的版本，详情见日志。";
                return;
            }

            _options = new List<LoaderOption> { new(null, "自动选择（推荐版本）") };
            _options.AddRange(versions.Select(v => new LoaderOption(v.Version, v.Recommended ? $"{v.Version}（推荐）" : v.Version)));

            LoaderVersionCombo.ItemsSource = _options;
            LoaderVersionCombo.SelectedIndex = 0;

            var recommended = versions.FirstOrDefault(v => v.Recommended);
            LoaderHint.Text = recommended is null
                ? $"共 {versions.Count} 个可用版本，自动选择会取该类加载器的最新版。"
                : $"共 {versions.Count} 个可用版本，推荐 {recommended.Version}。";
        }
        catch (Exception ex)
        {
            _services.Log.Error($"读取 {LoaderLabels.Of(kind)} 版本列表失败：{ex.Message}");
            LoaderHint.Text = $"读取失败：{ex.Message}";
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFolder(GamePaths.VersionsDir(_services.Config.GameDir));

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        Request = new InstallRequest(
            CurrentLoader,
            _version.Name,
            (LoaderVersionCombo.SelectedItem as LoaderOption)?.Version);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ── 内部小类型 ──────────────────────────────────────────

    private sealed record LoaderChoice(LoaderKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record LoaderOption(string? Version, string Label)
    {
        public override string ToString() => Label;
    }
}

public sealed record InstallRequest(LoaderKind Kind, string GameVersion, string? LoaderVersion);
