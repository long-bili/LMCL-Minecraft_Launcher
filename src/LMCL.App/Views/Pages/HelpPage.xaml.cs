using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Views.Pages;

public partial class HelpPage : PageBase
{
    public HelpPage()
    {
        InitializeComponent();
        BuildNotices();
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

    /// <summary>
    /// 把第三方开源软件清单铺出来：按组分节，每条是「名字（可点）」+ 版本 + 许可证 + 一句话用途。
    ///
    /// 用代码而不是 XAML 生成：清单会随依赖变化，数据在 <see cref="ThirdPartyNotices"/> 里，
    /// 这里只负责摆版面。超链接统一挂同一个 RequestNavigate —— 点击一律交给
    /// <see cref="Ui.OpenUrl"/>，保证和项目里其它外链同一个行为（失败会提示而不是静默）。
    /// </summary>
    private void BuildNotices()
    {
        foreach (var group in ThirdPartyNotices.Groups)
        {
            var entries = ThirdPartyNotices.All.Where(e => e.Group == group).ToList();
            if (entries.Count == 0) continue;

            NoticesHost.Children.Add(new TextBlock
            {
                Text = ThirdPartyNotices.TitleOf(group),
                Style = (Style)FindResource("CaptionText"),
                Margin = new Thickness(0, 16, 0, 4),
            });

            foreach (var entry in entries)
                NoticesHost.Children.Add(BuildRow(entry));
        }
    }

    private UIElement BuildRow(ThirdPartyNotices.Entry entry)
    {
        var row = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

        // ── 第一行：名字（超链接）+ 版本 + 许可证 ──
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var link = new Hyperlink(new Run(entry.Name)) { NavigateUri = new Uri(entry.Url) };
        link.RequestNavigate += Notice_RequestNavigate;
        link.ToolTip = entry.Url;   // 鼠标悬停能看见要跳去哪
        head.Inlines.Add(link);

        // 版本号和许可证用淡一点的颜色，视觉上让「名字」更突出
        head.Inlines.Add(new Run($"  {entry.Version}") { Foreground = Muted() });
        head.Inlines.Add(new Run($"  ·  {entry.License}") { Foreground = Muted() });

        row.Children.Add(head);

        // ── 第二行：用途 ──
        row.Children.Add(new TextBlock
        {
            Text = entry.Note,
            Style = (Style)FindResource("CaptionText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return row;
    }

    /// <summary>次要文字的画笔；取不到就退回半透明黑，不让它因为资源改名而崩。</summary>
    private Brush Muted() =>
        TryFindResource("TextTertiaryBrush") as Brush
        ?? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));

    /// <summary>
    /// 自检用：第三方开源软件清单是否完整、可用。
    ///
    /// 断言四件事：
    ///   ① 清单非空，且每组的排版元素都真的挂到了界面上；
    ///   ② 没有重名（重复条目通常意味着加依赖时复制粘贴忘了改名）；
    ///   ③ 每条链接都是能打开的 http(s) 地址；
    ///   ④ 显示出来的超链接数量与清单条数一致 ——
    ///      漏挂 RequestNavigate 的话，点名字不会有任何反应，而构造阶段完全看不出来。
    /// </summary>
    internal bool NoticesReady()
    {
        try
        {
            var all = ThirdPartyNotices.All;
            if (all.Length == 0) return false;

            // 关键组件一个都不能少（这条才是真正防「漏署名」的）
            if (!ThirdPartyNotices.AllRequiredPresent()) return false;

            // ② 不能重名
            if (all.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != all.Length)
                return false;

            // ③ 链接必须是 http(s)，且字段都填了
            foreach (var e in all)
            {
                if (string.IsNullOrWhiteSpace(e.Name)) return false;
                if (string.IsNullOrWhiteSpace(e.License)) return false;
                if (string.IsNullOrWhiteSpace(e.Note)) return false;

                if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri)) return false;
                if (uri.Scheme is not ("http" or "https")) return false;
            }

            // ①④ 界面上确实铺出来了：数一数 Hyperlink
            var links = CountHyperlinks(NoticesHost);
            if (links != all.Length) return false;

            // 每组都得有一个小标题
            var captions = NoticesHost.Children.OfType<TextBlock>()
                .Count(t => ThirdPartyNotices.Groups.Any(g => ThirdPartyNotices.TitleOf(g) == t.Text));
            if (captions != ThirdPartyNotices.Groups.Count()) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>递归数一个视觉树里有多少个 Hyperlink。</summary>
    private static int CountHyperlinks(DependencyObject root)
    {
        var count = 0;

        if (root is TextBlock { } block)
        {
            foreach (var inline in block.Inlines)
                if (inline is Hyperlink) count++;
        }

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
            count += CountHyperlinks(VisualTreeHelper.GetChild(root, i));

        return count;
    }

    private void Notice_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Ui.OpenUrl(e.Uri.ToString());
        e.Handled = true;
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
