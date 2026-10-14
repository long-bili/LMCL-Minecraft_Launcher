using System.Windows;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Views.Pages;

public partial class HelpPage : PageBase
{
    public HelpPage()
    {
        InitializeComponent();
    }

    /// <summary>被「设置」页嵌进「帮助」分栏时调用：标题行由设置页的分栏承担，这里收掉。</summary>
    public override void UseEmbeddedMode()
    {
        PageRoot.Margin = new Thickness(0);
        PageHeader.Visibility = Visibility.Collapsed;
    }

    public override void OnNavigatedTo()
    {
        VersionText.Text = $"LMCL {AppServices.Version}";
        AboutText.Text = $"版本 {AppServices.Version}";
    }

    private void GoDownload_Click(object sender, RoutedEventArgs e) => GoTo("download");

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        var window = new AccountWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void Bilibili_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Ui.OpenUrl(e.Uri.ToString());
        e.Handled = true;
    }
}
