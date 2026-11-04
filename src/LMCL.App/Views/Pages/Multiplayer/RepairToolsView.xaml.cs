using System.IO;
using System.Net.Http;
using System.Windows;
using LMCL.App.Services;
using System.Windows.Controls;
using System.Windows.Media;
using LMCL.Core.Configuration;
using LMCL.Core.EasyTier;
using LMCL.Core.Online;

namespace LMCL.App.Views.Pages.Multiplayer;

/// <summary>
/// 修复工具：联机组件重装 / 网络检测 / 缓存清理 / 诊断入口。
/// 定位是「联机出问题时能自己动手修」——重装组件走的是正式下载链路
/// （EnsureInstalledAsync，含驱动 DLL 解压），不是手工删文件碰运气。
/// </summary>
public partial class RepairToolsView : PageBase
{
    /// <summary>一行检测结果。</summary>
    public sealed class NetRow
    {
        public string Icon { get; init; } = "";
        public string Name { get; init; } = "";
        public string ResultLabel { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.Gray;
    }

    public RepairToolsView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo() => RefreshState();

    /// <summary>铺组件状态与缓存占用。</summary>
    public void RefreshState()
    {
        EasyTierState.Text = EasyTierService.IsInstalled
            ? $"EasyTier 已就绪：{AppPaths.EasyTierDir}"
            : "EasyTier 还没下载。创建 / 加入房间时会自动下载，也可以在这里手动重装。";

        long cacheBytes = 0;
        try
        {
            if (Directory.Exists(MapDownloader.CacheRoot))
                cacheBytes = new DirectoryInfo(MapDownloader.CacheRoot)
                    .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception)
        {
            // 数不出来就当 0，别让状态行开天窗
        }

        RepairStatus.Text = cacheBytes > 0
            ? $"地图下载缓存目前占用 {cacheBytes / 1024.0 / 1024:0.0} MB。"
            : "地图下载缓存是空的。";
    }

    // ── 联机组件 ────────────────────────────────────────────

    /// <summary>重装：删掉整个 easytier 目录再走一遍下载。虚拟网在跑时拒绝（进程还占着文件）。</summary>
    private async void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        if (S.EasyTier.IsRunning)
        {
            Ui.Warn("虚拟网正在运行，先退出房间再重装组件。");
            return;
        }

        if (!Ui.ConfirmDanger(
                $"将删除 {AppPaths.EasyTierDir} 并重新下载 EasyTier。\n\n组件本身没有用户数据，重装不影响任何房间记录或设置。确定吗？",
                "重装联机组件"))
        {
            return;
        }

        BtnReinstall.IsEnabled = false;
        EasyTierState.Text = "正在删除旧组件…";

        try
        {
            if (Directory.Exists(AppPaths.EasyTierDir))
                Directory.Delete(AppPaths.EasyTierDir, true);
        }
        catch (Exception ex)
        {
            BtnReinstall.IsEnabled = true;
            RefreshState();
            Ui.Error($"删除旧组件失败（可能被杀毒软件占用）：\n\n{ex.Message}");
            return;
        }

        EasyTierState.Text = "正在重新下载 EasyTier…";
        var progress = new Progress<double>(r =>
            EasyTierState.Text = $"正在重新下载 EasyTier… {r * 100:0}%");

        try
        {
            var ok = await S.EasyTier.EnsureInstalledAsync(progress).ConfigureAwait(true);
            Ui.Info(ok ? "联机组件已重新下载并就绪。" : "下载失败，稍后再试（网络原因）。");
        }
        finally
        {
            BtnReinstall.IsEnabled = true;
            RefreshState();
        }
    }

    private void OpenEasyTierDir_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFolder(AppPaths.EasyTierDir);

    // ── 网络检测 ────────────────────────────────────────────

    private async void CheckNet_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckNet.IsEnabled = false;

        var rows = new List<NetRow>
        {
            Waiting("联机大厅（api.mccampfire.top）"),
            Waiting("下载镜像（BMCLAPI）"),
            Waiting("Mojang 官方源"),
            Waiting("共享节点（EasyTier 官方）"),
        };
        NetList.ItemsSource = rows;

        rows[0] = await ProbeAsync("联机大厅（api.mccampfire.top）",
            () => ProbeUrl("https://api.mccampfire.top/v1/lobby", expectJson: true)).ConfigureAwait(true);
        NetList.ItemsSource = rows.ToList();

        rows[1] = await ProbeAsync("下载镜像（BMCLAPI）",
            () => ProbeUrl("https://bmclapi2.bangbang93.com/profiles")).ConfigureAwait(true);
        NetList.ItemsSource = rows.ToList();

        rows[2] = await ProbeAsync("Mojang 官方源",
            () => ProbeUrl("https://launchermeta.mojang.com/mc/game/version_manifest.json")).ConfigureAwait(true);
        NetList.ItemsSource = rows.ToList();

        rows[3] = await ProbeAsync("共享节点（EasyTier 官方）",
            () => EasyTierService.IsSharedNodeReachableAsync()).ConfigureAwait(true);
        NetList.ItemsSource = rows.ToList();

        BtnCheckNet.IsEnabled = true;

        if (rows.All(r => r.Icon == "\uE73E"))
            Ui.Info("所有服务都通。联机或下载再出问题就不是网络的事了。");
    }

    private static NetRow Waiting(string name) => new()
    {
        Icon = "\uE9D9", Name = name, ResultLabel = "检测中…", Brush = Brushes.Gray,
    };

    private static async Task<NetRow> ProbeAsync(string name, Func<Task<bool>> probe)
    {
        try
        {
            var ok = await probe().ConfigureAwait(true);
            return ok
                ? new NetRow { Icon = "\uE73E", Name = name, ResultLabel = "通", Brush = Brushes.ForestGreen }
                : new NetRow { Icon = "\uE783", Name = name, ResultLabel = "不通", Brush = Brushes.Firebrick };
        }
        catch (Exception ex)
        {
            return new NetRow { Icon = "\uE783", Name = name, ResultLabel = $"不通（{ex.Message}）", Brush = Brushes.Firebrick };
        }
    }

    private static async Task<bool> ProbeUrl(string url, bool expectJson = false)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await client.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        if (expectJson)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return text.TrimStart().StartsWith('{');
        }
        return true;
    }

    // ── 缓存与诊断 ──────────────────────────────────────────

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var freed = MapDownloader.PruneCache();
            Ui.Info(freed > 0
                ? $"已清理地图下载缓存，释放 {freed / 1024.0 / 1024:0.0} MB。"
                : "缓存本来就是空的，没什么可清的。");
        }
        catch (Exception ex)
        {
            Ui.Error($"清理失败：\n\n{ex.Message}");
        }
        RefreshState();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LogDir);

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LauncherRoot);

    /// <summary>自检：控件都在，初始状态行铺得出来。</summary>
    internal bool LayoutReady()
    {
        if (BtnReinstall is null || BtnCheckNet is null || BtnClearCache is null) return false;
        if (NetList is null || EasyTierState is null || RepairStatus is null) return false;

        RefreshState();
        // 组件状态行必须有内容（装没装都要说一句话）
        return EasyTierState.Text.Length > 0;
    }
}
