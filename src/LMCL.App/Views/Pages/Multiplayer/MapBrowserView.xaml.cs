using System.IO;
using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;
using LMCL.Core.Online;

namespace LMCL.App.Views.Pages.Multiplayer;

/// <summary>
/// 在线地图：CurseForge 地图搜索 / 下载 / 导入（界面版式照 Campfire 的 MapsPage 重排成 LMCL 卡片风）。
/// 在线部分走 Campfire 公共服务（key 由服务端持有）；服务不可用时只留本地导入。
/// 导入目标是「某个 .minecraft（或版本）目录下的 saves」——用户自己挑根目录，不猜。
/// </summary>
public partial class MapBrowserView : PageBase
{
    /// <summary>上次搜索的总条数（分页用，本页先不翻页，够用）。</summary>
    private MapDetail? _detail;

    /// <summary>下载完成后的本地文件路径（等待「导入」动作）。</summary>
    private string? _downloaded;

    private bool _loading;

    /// <summary>当前页码（0 起）与总页数。</summary>
    private int _page;

    private int _totalPages;

    /// <summary>是否已经自动加载过第一页（每次进页最多自动来一次）。</summary>
    private bool _autoLoaded;

    /// <summary>每页条数选项，与 PageSizeBox 的选择一一对应。</summary>
    private static readonly int[] PageSizes = [20, 40, 60, 100];

    public MapBrowserView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo()
    {
        try
        {
            // 进页自动加载最热的一页，不用再手点「搜索」（自检离线，跳过）
            if (!_autoLoaded && !SelfCheck.Running)
            {
                _autoLoaded = true;
                _page = 0;
                _ = SearchAsync();
            }
            else if (MapStatus.Text.Length == 0)
            {
                MapStatus.Text = "输入关键字后点「搜索」，或到「本地导入」页装手头的地图 zip。";
            }
        }
        catch (Exception ex)
        {
            // 页面初始化绝不能静默炸掉 —— 之前「整页空白」就是这么来的
            MapStatus.Text = $"页面初始化失败：{ex.Message}";
        }
    }

    // ── 搜索 ────────────────────────────────────────────────

    private void QueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _page = 0;
            _ = SearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _page = 0;
        _ = SearchAsync();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 0)
        {
            _page--;
            _ = SearchAsync();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page < _totalPages - 1)
        {
            _page++;
            _ = SearchAsync();
        }
    }

    /// <summary>换每页条数：回到第一页重新拉。</summary>
    private void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 初始化时的默认选中不打扰；自检离线也不联网
        if (QueryBox is null || SelfCheck.Running) return;
        _page = 0;
        _ = SearchAsync();
    }

    /// <summary>当前选的每页条数。</summary>
    private int PageSize =>
        PageSizeBox.SelectedIndex >= 0 && PageSizeBox.SelectedIndex < PageSizes.Length
            ? PageSizes[PageSizeBox.SelectedIndex] : 20;

    private async Task SearchAsync()
    {
        // 整个方法包死：任何一步炸了都退成一行可见的错误文案，
        // 不能再让未观察任务异常把页面变成空白
        try
        {
            if (_loading) return;
            if (BtnSearch is null || MapStatus is null || MapList is null || DetailPanel is null
                || DetailEmpty is null || PagerPanel is null || PageLabel is null
                || BtnPrevPage is null || BtnNextPage is null || SortBox is null
                || QueryBox is null || VersionBox is null || PageSizeBox is null)
            {
                return; // 控件没铺齐（理论到不了），至少不炸
            }

            _loading = true;
            BtnSearch.IsEnabled = false;
            BtnPrevPage.IsEnabled = false;
            BtnNextPage.IsEnabled = false;
            MapStatus.Text = _page == 0 ? "正在加载热门地图…" : $"正在加载第 {_page + 1} 页…";
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;

            var sort = (MapCatalog.MapSort)SortBox.SelectedIndex;
            var result = await MapCatalog.SearchAsync(QueryBox.Text, VersionBox.Text, sort, page: _page,
                    pageSize: PageSize)
                .ConfigureAwait(true);

            _totalPages = result.PageSize > 0
                ? Math.Max(1, (int)Math.Ceiling(result.Total / (double)result.PageSize))
                : 1;

            MapList.ItemsSource = result.Maps.Select(m => new MapRowVM(m)).ToList();
            MapStatus.Text = result.Maps.Count == 0
                ? "没搜到地图。换个关键字，或者清空版本过滤试试。"
                : $"共 {result.Total:N0} 个结果。选中一条看简介和可下载的文件。";

            PagerPanel.Visibility = Visibility.Visible;
            PageLabel.Text = $"第 {_page + 1} / {_totalPages} 页";
            BtnPrevPage.IsEnabled = _page > 0;
            BtnNextPage.IsEnabled = _page < _totalPages - 1;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (MapList is not null) MapList.ItemsSource = null;
            if (PagerPanel is not null) PagerPanel.Visibility = Visibility.Collapsed;
            if (MapStatus is not null) MapStatus.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
            if (BtnSearch is not null) BtnSearch.IsEnabled = true;
        }
    }

    // ── 详情 ────────────────────────────────────────────────

    private async void MapList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapList.SelectedItem is not MapRowVM row || _loading) return;
        _loading = true;
        MapStatus.Text = $"正在加载「{row.Model.Name}」的文件列表…";

        try
        {
            _detail = await MapCatalog.GetAsync(row.Model.Id).ConfigureAwait(true);

            DetailName.Text = _detail.Name;
            var meta = new List<string>();
            if (_detail.Author.Length > 0) meta.Add($"作者 {_detail.Author}");
            meta.Add($"{_detail.Downloads:N0} 次下载");
            if (_detail.Versions is { Count: > 0 })
                meta.Add(string.Join(" / ", _detail.Versions.Take(3)));
            DetailMeta.Text = string.Join("　·　", meta);
            DetailDesc.Text = _detail.Description.Length > 0 ? _detail.Description : _detail.Summary;

            // 头图：服务给了 logo 才亮；加载失败就收起，不占位
            if (Uri.TryCreate(_detail.Logo, UriKind.Absolute, out var logoUri))
            {
                try
                {
                    LogoImage.Source = new System.Windows.Media.Imaging.BitmapImage(logoUri);
                    LogoBorder.Visibility = Visibility.Visible;
                }
                catch (Exception)
                {
                    LogoBorder.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                LogoImage.Source = null;
                LogoBorder.Visibility = Visibility.Collapsed;
            }

            BtnOpenWebsite.Visibility = _detail.Website.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            FileList.ItemsSource = _detail.Files
                .Select(f => new FileRowVM(f))
                .ToList();
            DetailEmpty.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            MapStatus.Text = _detail.Files.Count == 0
                ? "这个地图没有可下载的文件，可以点「在 CurseForge 打开」去发布页看看。"
                : $"「{_detail.Name}」共 {_detail.Files.Count} 个文件，选一个下载。";
        }
        catch (Exception ex)
        {
            MapStatus.Text = $"加载失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>用默认浏览器打开地图的 CurseForge 发布页。</summary>
    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (_detail?.Website is { Length: > 0 } url)
            Ui.OpenUrl(url);
    }

    // ── 下载 ────────────────────────────────────────────────

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FileRowVM row) return;

        // 作者关了第三方分发（服务没给直链）→ 不弹下载，直接去网页
        if (string.IsNullOrEmpty(row.Model.Url))
        {
            if (_detail?.Website is { Length: > 0 } url)
            {
                Ui.OpenUrl(url);
                MapStatus.Text = "这个文件作者限制了程序直下，已在浏览器里打开发布页，下载后用上面的「本地导入」装进存档。";
            }
            else
            {
                Ui.Warn("作者限制了这个文件的直接下载，也没有留出发布页地址。");
            }
            return;
        }

        // 先弹「存到哪」：默认名用文件名，用户挑完才开始下
        var target = Ui.PickSavePath("地图压缩包 (*.zip)|*.zip", row.Model.FileName);
        if (target is null) return;

        BtnGotoImported.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadText.Text = "准备下载…";

        var progress = new Progress<double>(r =>
            DownloadText.Text = $"正在下载 {Path.GetFileName(target)}… {r * 100:0}%");

        try
        {
            var path = await MapDownloader.DownloadAsync(row.Model, progress, CancellationToken.None, target)
                .ConfigureAwait(true);
            _downloaded = path;
            DownloadText.Text = $"已下载：{path}";
            BtnGotoImported.IsEnabled = true;
            BtnGotoImported.Tag = path;
        }
        catch (Exception ex)
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            Ui.Error($"地图下载失败：\n\n{ex.Message}");
        }
    }

    // ── 导入 ────────────────────────────────────────────────

    private void ImportDownloaded_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            ImportOne(path);
    }

    /// <summary>本地导入页也走这里：把一份 zip 装进用户挑的 saves 目录。</summary>
    internal void ImportLocalZip(string zipPath) => ImportOne(zipPath);

    /// <summary>把一份 zip 装进用户挑的 saves 目录。</summary>
    private void ImportOne(string zipPath)
    {
        // 先让用户挑游戏根目录；saves 不存在就建（新装的游戏目录也没有 saves）
        var root = Ui.PickFolder(S.Config.GameDir);
        if (root is null) return;

        try
        {
            var name = MapDownloader.ImportZip(zipPath, Path.Combine(root, "saves"));
            Ui.Info($"地图「{name}」已导入到：\n{Path.Combine(root, "saves", name)}\n\n进游戏后它就在单人游戏的地图列表里。");
        }
        catch (Exception ex)
        {
            Ui.Error($"地图导入失败：\n\n{ex.Message}");
        }
    }

    // ── 行模型 ──────────────────────────────────────────────

    public sealed class MapRowVM
    {
        public MapRowVM(MapSummary model)
        {
            Model = model;
            // 行内头图：缩到 92px 解码，列表几十行也不吃内存
            if (Uri.TryCreate(model.Logo, UriKind.Absolute, out var uri))
            {
                try
                {
                    LogoImage = new System.Windows.Media.Imaging.BitmapImage();
                    LogoImage.BeginInit();
                    LogoImage.UriSource = uri;
                    LogoImage.DecodePixelWidth = 92;
                    LogoImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    LogoImage.EndInit();
                    HasLogo = true;
                }
                catch (Exception)
                {
                    HasLogo = false; // 图挂了就落回图标占位
                }
            }
        }

        public MapSummary Model { get; }

        public System.Windows.Media.Imaging.BitmapImage? LogoImage { get; }

        public bool HasLogo { get; }

        public string Name => Model.Name;

        public string AuthorLabel => Model.Author.Length > 0 ? Model.Author : "";

        public string DetailLabel
        {
            get
            {
                var versions = Model.Versions is { Count: > 0 }
                    ? Model.Versions.Take(3) : Enumerable.Empty<string>();
                return $"{Model.Downloads:N0} 次下载　{string.Join(" / ", versions)}　·　{Model.Summary}";
            }
        }
    }

    public sealed class FileRowVM
    {
        public FileRowVM(MapFile model) => Model = model;

        public MapFile Model { get; }

        public string FileName => string.IsNullOrEmpty(Model.Name) ? Model.FileName : Model.Name;

        /// <summary>有直链 = 程序下载；没有 = 作者限制，只能去网页。</summary>
        public string ActionLabel => Model.Url is null ? "网页下载" : "下载";

        public string ActionTip => Model.Url is null
            ? "作者限制了程序直接下载，点这里用浏览器打开发布页"
            : "选一个保存位置后开始下载";

        public string DetailLabel
        {
            get
            {
                var size = Model.Size >= 1024 * 1024 * 1024
                    ? $"{Model.Size / 1024.0 / 1024 / 1024:0.0} GB"
                    : $"{Model.Size / 1024.0 / 1024:0.0} MB";
                var versions = Model.Versions is { Count: > 0 }
                    ? string.Join(" / ", Model.Versions.Take(3)) : "不限版本";
                return $"{versions}　·　{size}　·　{Model.Date:yyyy-MM-dd}"
                    + (Model.Url is null ? "　（需到网页下载）" : "");
            }
        }
    }

    /// <summary>自检：关键控件都要在，离线逻辑（导入行模型）要能跑。</summary>
    internal bool LayoutReady()
    {
        if (BtnSearch is null || QueryBox is null) return false;
        if (MapList is null || FileList is null || DetailPanel is null) return false;
        if (MapStatus is null || DownloadPanel is null) return false;
        // 详情头（头图 / 元信息 / 发布页按钮）要铺齐
        if (LogoBorder is null || LogoImage is null || DetailMeta is null || BtnOpenWebsite is null) return false;
        if (DetailName is null || DetailDesc is null) return false;
        // 分页条要铺齐
        if (PagerPanel is null || BtnPrevPage is null || BtnNextPage is null || PageLabel is null) return false;
        if (PagerPanel.Visibility != Visibility.Collapsed) return false;
        // 每页条数选择与平行视界的空态占位
        if (PageSizeBox is null || DetailEmpty is null) return false;
        if (DetailEmpty.Visibility != Visibility.Visible) return false;

        // 行模型：文案必须拼得出来
        var map = new MapRowVM(new MapSummary
        {
            Id = 1, Name = "测试地图", Summary = "一个测试", Author = "作者",
            Downloads = 12345, Versions = new[] { "1.21.4", "1.21" },
        });
        if (map.DetailLabel.Length == 0 || map.AuthorLabel.Length == 0) return false;

        var file = new FileRowVM(new MapFile
        {
            Id = 2, Name = "文件", FileName = "file.zip", Size = 5 * 1024 * 1024,
            Versions = new[] { "1.21.4" }, Url = "https://example.invalid/x.zip",
        });
        if (!file.DetailLabel.Contains("5.0 MB")) return false;

        // 详情面板默认收起
        if (DetailPanel.Visibility != Visibility.Collapsed) return false;
        return true;
    }
}
