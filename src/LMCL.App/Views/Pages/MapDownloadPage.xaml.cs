using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 在线地图：照设置页的顶部导航栏版式，分「在线资源 / 本地导入」两块。
/// 在线部分（搜索 / 下载 / 详情）在 MapBrowserView 里；这里只管切换和本地导入。
/// </summary>
public partial class MapDownloadPage : PageBase
{
    public MapDownloadPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo()
    {
        ActivateTab(Tabs.SelectedIndex is < 0 ? 0 : Tabs.SelectedIndex);
    }

    /// <summary>切顶栏：在线资源 ↔ 本地导入。</summary>
    private void ActivateTab(int index)
    {
        var online = index == 0;
        PaneOnline.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        PaneLocal.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        if (online) PaneOnline.OnNavigatedTo();
    }

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PaneOnline is null || PaneLocal is null) return; // XAML 还没铺完
        ActivateTab(Tabs.SelectedIndex);
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        var picked = Ui.PickFiles("地图压缩包 (*.zip)|*.zip", multi: false);
        if (picked is { Length: > 0 }) PaneOnline.ImportLocalZip(picked[0]);
    }

    /// <summary>自检：两块内容都切得到，在线页控件铺得齐。</summary>
    internal bool LayoutReady()
    {
        if (Tabs is null || PaneOnline is null || PaneLocal is null || BtnImportLocal is null) return false;
        if (!PaneOnline.LayoutReady()) return false;

        foreach (var i in new[] { 0, 1 })
        {
            Tabs.SelectedIndex = i;
            if ((PaneOnline.Visibility == Visibility.Visible) != (i == 0)) return false;
            if ((PaneLocal.Visibility == Visibility.Visible) != (i == 1)) return false;
        }
        Tabs.SelectedIndex = 0;
        return PaneOnline.Visibility == Visibility.Visible;
    }
}
