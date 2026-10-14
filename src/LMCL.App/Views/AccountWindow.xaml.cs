using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LMCL.App.Services;
using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Views;

/// <summary>
/// 账号管理。
///
/// 结构照着 PCL 的路子来：左边一列是「有哪些账号」，右边是「当前这个的详情」。
/// 添加账号不另开窗口，而是把右侧整块切成「添加」页——顶部一排胶囊选登录方式，
/// 底下是表单，左上角一个「返回」回到详情。
///
/// 和 PCL 唯一有意不一样的地方：PCL 的账号选择器就是「要用哪个账号启动」，
/// 点一下等于换账号；这里左边点一下只是**查看**，真要换还得按「设为当前账号」。
/// 原因很简单——这个窗口叫「管理」，用户点列表十有八九只是想看看，
/// 顺手把当前账号改掉属于误操作。真想一步换账号的话，主界面标题栏那个下拉才是入口。
/// </summary>
public partial class AccountWindow : Window
{
    /// <summary>
    /// 微软正版登录的入口开关。默认开。
    ///
    /// 页签、面板、登录流程本来就是齐全的，这个开关只控制入口显不显示；
    /// BuildTabs() 与 PanelIndex() 会自动把索引对齐，不用手工挪。
    ///
    /// 登录用的是内置的官方 client_id（见 MicrosoftAuth.ClientId），走的是
    /// login.live.com 的老式 Live 授权码流，回调固定为桌面地址
    /// https://login.live.com/oauth20_desktop.srf —— 这个内置 id 在 Live 端点上
    /// 只认这一个回调，本机回环地址一律不认（之前用随机端口回环当 redirect_uri，
    /// 会被微软以「redirect_uri is not valid」拒掉）。所以流程是：打开浏览器登录 →
    /// 把地址栏里的授权码粘回来两步完成，不再尝试在本机自动接码。
    ///
    /// 刻意写成 static readonly 而不是 const：const 会让编译器把下面那句
    /// Tabs.Items.Add("微软正版") 判定成不可达代码并报警告。
    /// </summary>
    private static readonly bool MicrosoftLoginEnabled = true;

    private readonly ObservableCollection<AccountRow> _rows = new();
    private CancellationTokenSource? _msaCts;

    /// <summary>
    /// 窗口开始关闭后置 true。异步登录收尾时若窗口已在关闭，就不要再碰界面、
    /// 更不要弹 MessageBox——在正在关闭的窗口上 Show 对话框会抛
    /// InvalidOperationException（"Cannot ... call Show ... while a Window is closing"）。
    /// </summary>
    private bool _closing;

    /// <summary>重入保护：Refresh() 里改 SelectedItem 会触发 Account_Changed。</summary>
    private bool _refreshing;

    /// <summary>重入保护：BuildTabs() 里设 SelectedIndex 会触发 Tabs_Changed。</summary>
    private bool _buildingTabs;

    /// <summary>左列里正在「查看」的那个账号，可能不是当前使用的那个。</summary>
    private AccountRow? _viewing;

    /// <summary>
    /// 外观区的「代次」。每换一次展示对象就 +1，异步纹理下载回来时对不上就直接丢弃——
    /// 不然用户快速点过好几个账号之后，先发的请求后到，会把别人的皮肤画到当前这个账号上。
    /// </summary>
    private int _appearanceLoad;

    /// <summary>已经自动拉过档案的账号 Id。拉失败也不反复重试，免得每次点列表都卡一下。</summary>
    private readonly HashSet<string> _appearanceTried = new();

    public AccountWindow()
    {
        InitializeComponent();

        AccountList.ItemsSource = _rows;

        _buildingTabs = true;
        BuildTabs();
        _buildingTabs = false;

        // 关键的一步。页签的选中事件被 _buildingTabs 挡掉了，所以面板可见性
        // 必须在这里显式同步一次——之前漏了这句，右侧整块一直是 Collapsed，
        // 表现就是「账号界面渲染有问题」：打开窗口右边一片空白。
        SyncAddPanels();

        Closed += (_, _) => _msaCts?.Cancel();
        // 在 Closing（而非 Closed）里置位：Closed 会取消登录令牌，异步收尾一旦跑起来
        // 就能立刻看到 _closing，从而跳过弹窗与界面改动，避免上面那个 InvalidOperationException。
        Closing += (_, _) => _closing = true;

        Refresh();
        RefreshInjector();
    }

    private static AppServices S => App.Services;

    // ── 自检探针 ────────────────────────────────────────────
    // 「构造没抛异常」并不能说明界面是好的：面板全都 Collapsed 也照样不抛异常，
    // 打开却是一片空白。下面这三个成员就是给 SelfCheck 断言用的。

    /// <summary>详情面板当前是不是可见的。</summary>
    internal bool DetailPaneVisible => PaneDetail.Visibility == Visibility.Visible;

    /// <summary>切到添加模式。给自检用。</summary>
    internal void OpenAddPaneForCheck() => ShowPane(true);

    /// <summary>
    /// 用一份「假的微软账号」把皮肤页铺出来，给人工截图检查用。
    ///
    /// 本机不一定有正版账号，没有的话皮肤页的「上传皮肤 / 切换披风」那几张卡
    /// 平时根本看不到。这个方法把探针账号塞进去并切到皮肤页，方便肉眼过一遍版式。
    /// **纯本地**：账号里的 Skins / Capes 带几条假数据，不联网。
    /// </summary>
    internal void PreviewMicrosoftSkinForCheck()
    {
        var skins = new List<SkinInfo>
        {
            new() { Id = "s1", Url = "", Variant = "CLASSIC", State = "ACTIVE", Alias = "standard" },
            new() { Id = "s2", Url = "", Variant = "SLIM", State = "INACTIVE", Alias = "slim" },
        };
        var capes = new List<CapeInfo>
        {
            new() { Id = "c1", Url = "", State = "ACTIVE", Alias = "Migrator" },
            new() { Id = "c2", Url = "", State = "INACTIVE", Alias = "MapMaker" },
        };

        _viewing = new AccountRow(new Account
        {
            Name = "Steve（预览）",
            Type = AccountType.Microsoft,
            Uuid = "00000000000000000000000000000001",
            Skins = skins,
            Capes = capes,
        }, false);

        RefreshDetail();
        ShowSkinPane();
    }

    /// <summary>三个登录方式面板是不是「恰好一个可见」。</summary>
    internal bool ExactlyOneLoginPanelVisible
    {
        get
        {
            var panels = new[] { PanelOffline, PanelMicrosoft, PanelThird };
            return panels.Count(p => p.Visibility == Visibility.Visible) == 1;
        }
    }

    /// <summary>
    /// 把「一个微软账号」临时铺进详情区，再走进皮肤页，看该亮的是不是都亮了。
    ///
    /// 光断言「构造没抛异常」是查不出「控件写了但一直藏着」这种事的——和当初
    /// 详情面板全收起那次一个道理。这里不联网：给探针账号塞两个空列表，
    /// 就不会触发自动拉取档案。
    /// </summary>
    internal bool AppearanceVisibleForMicrosoft()
    {
        var previous = _viewing;

        _viewing = new AccountRow(new Account
        {
            Name = "__selfcheck__",
            Type = AccountType.Microsoft,
            Skins = new List<SkinInfo>(),
            Capes = new List<CapeInfo>(),
        }, false);

        RefreshDetail();

        // 顶栏那个入口按钮必须能看见，否则用户根本进不去皮肤页
        var entryVisible = BtnOpenSkin.Visibility == Visibility.Visible
                           && PaneDetail.Visibility == Visibility.Visible;

        ShowSkinPane();
        var visible = entryVisible
                      && PaneSkin.Visibility == Visibility.Visible
                      && ServerSkinBlock.Visibility == Visibility.Visible
                      && CapeCard.Visibility == Visibility.Visible
                      && LocalSkinCard.Visibility == Visibility.Visible;

        // 复原，别把窗口留在探针状态上
        _viewing = previous;
        ShowPane(false);

        return visible;
    }

    /// <summary>
    /// 把一个「离线账号」临时铺进去，看本地皮肤库是不是亮着、服务端那两块是不是收着。
    ///
    /// 离线账号没有服务端档案，**披风更是完全不存在**——
    /// 所以左栏那块披风预览（CapePreviewBlock）也必须一起收起来，
    /// 否则用户会看到一个永远空着的披风框。
    /// </summary>
    internal bool LocalSkinVisibleForOffline()
    {
        var previous = _viewing;

        _viewing = new AccountRow(new Account
        {
            Name = "__selfcheck__",
            Type = AccountType.Offline,
        }, false);

        RefreshDetail();
        ShowSkinPane();

        var visible = PaneSkin.Visibility == Visibility.Visible
                      && LocalSkinCard.Visibility == Visibility.Visible
                      && ServerSkinBlock.Visibility == Visibility.Collapsed
                      && CapeCard.Visibility == Visibility.Collapsed
                      && CapePreviewBlock.Visibility == Visibility.Collapsed;

        _viewing = previous;
        ShowPane(false);

        return visible;
    }

    /// <summary>
    /// 第三方账号：材质由那台服务器决定，启动器改不了。
    /// 所以顶栏那个「皮肤 / 披风」入口**必须收起来**——
    /// 给一个点进去只有「改不了」的按钮，比没有按钮更糟。
    /// </summary>
    internal bool SkinEntryHiddenForThirdParty()
    {
        var previous = _viewing;

        _viewing = new AccountRow(new Account
        {
            Name = "__selfcheck__",
            Type = AccountType.ThirdParty,
            Server = "https://example.com/api/yggdrasil",
        }, false);

        RefreshDetail();

        var hidden = SkinEntryBlock.Visibility == Visibility.Collapsed
                     && PaneDetail.Visibility == Visibility.Visible
                     && PaneSkin.Visibility == Visibility.Collapsed;

        _viewing = previous;
        ShowPane(false);

        return hidden;
    }

    /// <summary>
    /// 多自定义皮肤：导入两张之后列表里应该有两项，删掉一张就只剩一项。
    ///
    /// 这个探针会**真的往皮肤库目录写两个小 PNG**，跑完自己清理干净。
    /// 之所以要动真格的：多皮肤的核心就是「列表长度随导入变化」，
    /// 光看界面构造是查不出「导入了但列表没刷新」这类问题的。
    /// </summary>
    internal bool MultiCustomSkinWorks()
    {
        var kind = SkinLibraryKind.Offline;

        // 造两张最小的合法 64×64 PNG（纯色即可，只要能被识别成 PNG 并且尺寸对）
        var png = TinyPng.Create(64, 64);
        var tempDir = Path.Combine(Path.GetTempPath(), "lmcl-selfcheck-skins");
        Directory.CreateDirectory(tempDir);
        var a = Path.Combine(tempDir, "selfcheck-a.png");
        var b = Path.Combine(tempDir, "selfcheck-b.png");
        File.WriteAllBytes(a, png);
        File.WriteAllBytes(b, png);

        var before = S.SkinLibrary.ListUserSkins(kind).Count;
        var created = new List<SkinEntry>();

        try
        {
            created.Add(S.SkinLibrary.AddAsync(kind, a, SkinArmModel.Classic).GetAwaiter().GetResult());
            created.Add(S.SkinLibrary.AddAsync(kind, b, SkinArmModel.Slim).GetAwaiter().GetResult());

            var afterAdd = S.SkinLibrary.ListUserSkins(kind).Count;
            if (afterAdd != before + 2) return false;

            // 两张的 id 必须不同，否则就是「又写回同一个槽」了
            if (string.Equals(created[0].Id, created[1].Id, StringComparison.OrdinalIgnoreCase))
                return false;

            S.SkinLibrary.Delete(created[0]);

            var afterDelete = S.SkinLibrary.ListUserSkins(kind).Count;
            if (afterDelete != before + 1) return false;

            // 删掉的那张不该还在
            if (S.SkinLibrary.Find(created[0].Id) is not null) return false;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // 清干净：把这次造出来的都删掉，别污染用户的皮肤库
            foreach (var entry in created.ToList())
            {
                if (S.SkinLibrary.Find(entry.Id) is not null) S.SkinLibrary.Delete(entry);
            }

            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清不掉无妨 */ }
        }
    }

    /// <summary>
    /// 自检用：皮肤页里那几个「操作 ⇄ 内容」的容器是不是真的分开了。
    ///
    /// 早先这些标题行是没写 ColumnDefinitions 的 Grid，说明文字和右侧按钮
    /// 落在同一个格子里互相压盖。这种错构造阶段完全不报，只能断言布局。
    /// </summary>
    internal bool SkinLayoutNotOverlapping()
    {
        // 皮肤页可见时，左栏预览和右栏清单不该有重叠区域
        var left = SkinPreview;
        var right = SkinList;

        if (left.ActualWidth <= 0 || right.ActualWidth <= 0) return true; // 还没布局，跳过

        var leftRect = left.TransformToAncestor(PaneSkin)
            .TransformBounds(new Rect(0, 0, left.ActualWidth, left.ActualHeight));
        var rightRect = right.TransformToAncestor(PaneSkin)
            .TransformBounds(new Rect(0, 0, right.ActualWidth, right.ActualHeight));

        return !leftRect.IntersectsWith(rightRect);
    }

    // ── 页签 ────────────────────────────────────────────────

    /// <summary>
    /// 页签项按开关生成。写死在 XAML 里的话，收起微软那一栏就要手工挪后面的索引，
    /// 很容易漏掉一处导致点「外置登录」出来的是离线面板。
    /// </summary>
    private void BuildTabs()
    {
        Tabs.Items.Add(new ListBoxItem { Content = "离线账号" });
        if (MicrosoftLoginEnabled) Tabs.Items.Add(new ListBoxItem { Content = "微软正版" });
        Tabs.Items.Add(new ListBoxItem { Content = "外置登录" });

        Tabs.SelectedIndex = 0;
    }

    /// <summary>页签在界面上的序号 → 内部面板序号（微软收起时要顺次前移）。</summary>
    private static int PanelIndex(int tabIndex) =>
        MicrosoftLoginEnabled || tabIndex == 0 ? tabIndex : tabIndex + 1;

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingTabs) return;
        SyncAddPanels();
    }

    /// <summary>把「当前选中的登录方式」落到三个面板的可见性上。</summary>
    private void SyncAddPanels()
    {
        var index = PanelIndex(Math.Max(0, Tabs.SelectedIndex));

        PanelOffline.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelMicrosoft.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelThird.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        AddHint.Text = index switch
        {
            0 => "单人游戏、本地测试用这个就够了。",
            1 => "用微软账号登录，能进正版服务器。",
            _ => "进用 authlib-injector 做外置登录的服务器时用这个。",
        };

        // 切到离线就把焦点给输入框，省得用户再点一下
        if (index == 0) OfflineName.Focus();
    }

    // ── 左列：列表 ──────────────────────────────────────────

    private void Refresh()
    {
        _refreshing = true;

        // 刷新前正在看谁，刷新后尽量还看谁；它要是被删了就退回当前账号
        var keepId = _viewing?.Model.Id;

        _rows.Clear();
        foreach (var account in S.Accounts.Accounts.OrderByDescending(a => a.LastUsed))
            _rows.Add(new AccountRow(account, account.Id == S.Accounts.SelectedId));

        var target = _rows.FirstOrDefault(r => r.Model.Id == keepId)
                     ?? _rows.FirstOrDefault(r => r.IsCurrent)
                     ?? _rows.FirstOrDefault();

        AccountList.SelectedItem = target;
        _viewing = target;

        var empty = _rows.Count == 0;
        ListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountCount.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        AccountCountText.Text = _rows.Count.ToString();

        _refreshing = false;

        RefreshDetail();
        RefreshFooter();
    }

    /// <summary>
    /// 左列点了另一行。这里只换「正在看谁」，不动当前账号——
    /// 换账号是 SetCurrent_Click() 的事，得用户明确点一下。
    /// </summary>
    private void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;

        _viewing = AccountList.SelectedItem as AccountRow;
        RefreshDetail();

        // 正在皮肤页 / 添加页时换了人，就把视图退回账号详情：
        // 留在皮肤页上，用户既看不到新账号的信息，也容易以为改的是刚点的那个人
        if (PaneSkin.Visibility == Visibility.Visible || PaneAdd.Visibility == Visibility.Visible)
            ShowPane(false);
    }

    // ── 右列：详情 ──────────────────────────────────────────

    /// <summary>
    /// 右侧三大块的切换：详情 / 皮肤管理 / 添加。
    /// 三块一直挂在树上，只改可见性——切回来时不用重建，也不会有白屏。
    /// </summary>
    private void ShowPane(bool add)
    {
        PaneAdd.Visibility = add ? Visibility.Visible : Visibility.Collapsed;
        PaneDetail.Visibility = add ? Visibility.Collapsed : Visibility.Visible;
        PaneSkin.Visibility = Visibility.Collapsed;

        if (!add) RefreshDetail();
    }

    /// <summary>进皮肤管理页。</summary>
    private void OpenSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_viewing is null) return;
        ShowSkinPane();
    }

    private void ShowSkinPane()
    {
        PaneSkin.Visibility = Visibility.Visible;
        PaneDetail.Visibility = Visibility.Collapsed;
        PaneAdd.Visibility = Visibility.Collapsed;

        // 进页面时顺手刷新一次档案（自检时不联网）
        if (_viewing is { } row && row.Model.SupportsAppearance && !SelfCheck.Running)
            _ = FetchAppearanceAsync(row.Model, _appearanceLoad);
    }

    /// <summary>把 _viewing 的内容铺到详情区。没有任何账号时显示引导。</summary>
    private void RefreshDetail()
    {
        var row = _viewing;

        DetailEmpty.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;

        if (row is null)
        {
            // 代次 +1：把在途的纹理下载全部作废，别等它们回来再往空界面上画
            _appearanceLoad++;
            return;
        }

        var model = row.Model;

        DetailInitial.Text = row.Initial;
        DetailName.Text = model.Name;
        DetailType.Text = row.Subtitle;
        DetailCurrentChip.Visibility = row.IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        InfoName.Text = model.Name;
        InfoUuid.Text = string.IsNullOrEmpty(model.Uuid) ? "—" : model.Uuid;
        InfoKind.Text = model.Type == AccountType.ThirdParty && model.Server.Length > 0
            ? $"{model.TypeLabel}（{model.Server}）"
            : model.TypeLabel;
        InfoCredential.Text = DescribeCredential(model);

        // 已经是当前账号了就别再让人点一次，按钮直接说明白
        BtnSetCurrent.Content = row.IsCurrent ? "已经是当前账号" : "设为当前账号";
        BtnSetCurrent.IsEnabled = !row.IsCurrent;

        ShowAppearance(model);
    }

    private static string DescribeCredential(Account account) => account.Type switch
    {
        AccountType.Offline => "离线账号不联网验证，不需要令牌。",
        _ when account.IsExpired => "暂存令牌已过期，下次启动这个账号时会自动续期。",
        _ when account.ExpiresAt is { } time => $"有效至 {time:yyyy-MM-dd HH:mm}。",
        _ => "已有可用的登录凭证。",
    };

    private void RefreshFooter()
    {
        var current = S.Accounts.Selected;

        FooterText.Text = _rows.Count == 0
            ? "还没有账号。左边点「添加账号」加一个就能开始玩。"
            : current is null
                ? $"共 {_rows.Count} 个账号。还没有指定用哪个启动游戏。"
                : $"共 {_rows.Count} 个账号，当前使用「{current.Name}」（{current.TypeLabel}）。";
    }

    // ── 右列：外观（皮肤 / 披风）────────────────────────────
    //
    // 只有微软正版账号有这套东西：离线账号没有档案，第三方账号的材质挂在
    // authlib-injector 那套验证体系上，Minecraft 服务这边读不到。
    // 数据来自 GET /minecraft/profile，切换走 PATCH，上传 / 重置走 PUT / DELETE，
    // 全部封在 Core 的 SkinService 里；这里只负责画和守卫。

    /// <summary>决定皮肤页上几张卡片显不显示，并把当前账号的外观铺上去。</summary>
    private void ShowAppearance(Account account)
    {
        // 代次 +1：把在途的下载 / 渲染全部作废，别等它们回来再往空界面上画
        var generation = ++_appearanceLoad;

        SkinPageSubtitle.Text = $"{account.Name}　·　{account.TypeLabel}";

        // 第三方账号的材质挂在 authlib-injector 那套验证体系上，
        // Minecraft 服务这边读不到，本地皮肤库对它也没有意义。
        //
        // 而且**顶栏那个「皮肤 / 披风」入口按钮也要收起来**：点进去只有一句
        // 「改不了」，那就不该给用户一个点了白点的按钮。
        if (account.Type == AccountType.ThirdParty)
        {
            SkinEntryBlock.Visibility = Visibility.Collapsed;
            HeaderSkinImage.Source = null;

            // 万一用户是在皮肤页上切过来的，退回详情页，别把人留在那一页
            if (PaneSkin.Visibility == Visibility.Visible) ShowPane(false);
            return;
        }

        SkinEntryBlock.Visibility = Visibility.Visible;

        // 微软账号：服务端档案 + 披风 + 本地皮肤库；离线账号：只有本地皮肤库
        ServerSkinBlock.Visibility = account.SupportsAppearance ? Visibility.Visible : Visibility.Collapsed;
        CapeCard.Visibility = account.SupportsAppearance ? Visibility.Visible : Visibility.Collapsed;

        // 离线账号连「披风」这个概念都没有，左栏那块预览也一起收
        CapePreviewBlock.Visibility = account.SupportsAppearance ? Visibility.Visible : Visibility.Collapsed;

        LocalSkinCard.Visibility = Visibility.Visible;

        if (!account.SupportsAppearance)
        {
            SkinPageSubtitle.Text =
                $"{account.Name}　·　离线账号的皮肤只存在本机，别人看不到。";
        }

        SkinStatus.Text = "";
        RenderLocalLibrary(account);

        if (!account.SupportsAppearance)
        {
            // 离线账号：没有服务端档案，左栏预览用本地那一张就够了
            UpdateHeaderThumb(account);
            return;
        }

        // 缓存里有的纹理立刻就能画出来；没缓存的在后台补，补完再重画一次
        RenderAppearance(account);
        UpdateHeaderThumb(account);

        // 自检是离线跑的，别让它在这里发起网络请求
        if (SelfCheck.Running) return;

        _ = LoadAppearanceImagesAsync(account, generation);

        // 从没拉过档案的账号顺手拉一次，否则打开窗口只能看到一片空白。
        // 失败过的账号不再重试，免得每次点列表都卡一下。
        if (account.Skins is null && _appearanceTried.Add(account.Id))
            _ = FetchAppearanceAsync(account, generation);
    }

    /// <summary>顶栏那张小头像：优先账号绑定的本地皮肤，其次服务端当前皮肤。</summary>
    private void UpdateHeaderThumb(Account account)
    {
        if (account.Type == AccountType.ThirdParty)
        {
            HeaderSkinImage.Source = null;
            return;
        }

        var path = LocalSkinPathOf(account) ?? SkinService.CachedTexturePath(account.SkinUrl);
        HeaderSkinImage.Source = SkinPreviewRenderer.Head(path, 80);
    }

    // ── 本地皮肤库 ──────────────────────────────────────────

    /// <summary>账号该用哪一套本地皮肤库：离线一套、微软一套。</summary>
    private static SkinLibraryKind LibraryOf(Account account) =>
        account.Type == AccountType.Microsoft ? SkinLibraryKind.Microsoft : SkinLibraryKind.Offline;

    /// <summary>
    /// 把本地皮肤库画上去：内置的 Steve / Alex 一排，用户导入的一排（可多张、可删）。
    /// 点缩略图就把账号绑到那一张；用户导入的那排每张右上角带一个小删除按钮。
    /// </summary>
    private void RenderLocalLibrary(Account account)
    {
        var kind = LibraryOf(account);
        var builtIn = S.SkinLibrary.ListBuiltIn(kind);
        var userSkins = S.SkinLibrary.ListUserSkins(kind);

        var bound = S.SkinLibrary.Find(account.LocalSkinId)
                    ?? builtIn.FirstOrDefault();

        var local = bound is not null && S.SkinLibrary.IsReady(bound)
            ? S.SkinLibrary.PathOf(bound)
            : null;

        // 微软账号已经绑了服务端皮肤、又没指定本地那一张，就用服务端的当前皮肤画
        var preview = local ?? SkinService.CachedTexturePath(account.SkinUrl);

        var head = SkinPreviewRenderer.Head(preview, 128);
        SkinPreview.Source = head;
        SkinPreview.Visibility = head is null ? Visibility.Collapsed : Visibility.Visible;

        LocalSkinState.Text = bound is null
            ? "本地还没有可用的皮肤。"
            : $"当前：{bound.DisplayName}　·　{bound.ArmModelLabel}"
              + (S.SkinLibrary.IsReady(bound) ? "" : "（皮肤文件不见了，重新导入一张）");

        // ── 内置那两张 ──
        BuiltInSkinList.Children.Clear();
        foreach (var entry in builtIn)
            BuiltInSkinList.Children.Add(MakeSkinTile(entry, bound?.Id, deletable: false));

        // ── 用户导入的 ──
        UserSkinList.Children.Clear();
        foreach (var entry in userSkins)
            UserSkinList.Children.Add(MakeSkinTile(entry, bound?.Id, deletable: true));

        UserSkinEmpty.Visibility = userSkins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateHeaderThumb(account);
    }

    /// <summary>
    /// 一块皮肤缩略图。用户导入的那些右上角还叠一个小叉用来删除。
    /// 返回 UIElement 而不是 Button，因为带删除键时返回的是一个 Grid
    /// （WrapPanel 只收一个元素，所以缩略图和删除键必须先包在一起）。
    /// </summary>
    private UIElement MakeSkinTile(SkinEntry entry, string? boundId, bool deletable)
    {
        var ready = S.SkinLibrary.IsReady(entry);
        var active = entry.Id == boundId;

        var tooltip = $"{entry.DisplayName}\n{entry.ArmModelLabel}"
                      + (ready ? "" : "\n（皮肤文件不见了）")
                      + (active ? "\n（当前使用中）" : "\n点一下切换到这一张");

        var tile = MakeTile(
            ready ? SkinPreviewRenderer.Head(S.SkinLibrary.PathOf(entry), 64) : null,
            active,
            ready ? null : "缺",
            tooltip,
            () => ApplyLocalSkin(entry));

        if (!deletable) return tile;

        var delete = new Button
        {
            Content = "\uE711",             // 小叉
            Width = 18,
            Height = 18,
            FontSize = 9,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"删除「{entry.DisplayName}」",
        };
        delete.SetResourceReference(FontFamilyProperty, "IconFont");
        delete.SetResourceReference(BackgroundProperty, "ErrorBrush");
        delete.SetResourceReference(ForegroundProperty, "TextOnAccentBrush");
        delete.Click += (_, e) => DeleteUserSkin(entry);

        var host = new Grid { Margin = new Thickness(0, 0, 4, 4) };
        host.Children.Add(tile);
        host.Children.Add(delete);
        return host;
    }

    private void ApplyLocalSkin(SkinEntry entry)
    {
        if (_viewing is not { } row) return;

        var account = row.Model;
        account.LocalSkinId = entry.Id;
        account.LocalSkinArmModel = entry.ArmModel.ToString().ToUpperInvariant();
        S.Accounts.Update(account);

        S.Log.Info($"账号 {account.Name} 的本地皮肤已切换为 {entry.DisplayName}");
        RenderLocalLibrary(account);
        LocalSkinState.Text = $"已切换：{entry.DisplayName}。";
    }

    /// <summary>
    /// 导入一张皮肤。**支持多选**：一次挑好几张 PNG，每张都会成为一张独立的皮肤，
    /// 并且把最后导入的那张设为当前。
    /// </summary>
    private async void SkinImport_Click(object sender, RoutedEventArgs e)
    {
        if (_viewing is not { } row || row.Model.Type == AccountType.ThirdParty) return;

        var files = Ui.PickFiles("PNG 图片 (*.png)|*.png", multi: true);
        if (files.Length == 0) return;

        var kind = LibraryOf(row.Model);
        var arm = LocalSkinModel.SelectedIndex == 1 ? SkinArmModel.Slim : SkinArmModel.Classic;

        BtnSkinImport.IsEnabled = false;

        var added = new List<SkinEntry>();
        var failed = new List<string>();

        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                LocalSkinState.Text = files.Length == 1
                    ? $"正在导入 {Path.GetFileName(files[i])}…"
                    : $"正在导入 {i + 1}/{files.Length}：{Path.GetFileName(files[i])}…";

                try
                {
                    added.Add(await S.SkinLibrary.AddAsync(kind, files[i], arm));
                }
                catch (Exception ex)
                {
                    // 一张坏图不该让整批导入失败
                    failed.Add($"{Path.GetFileName(files[i])}：{ex.Message}");
                    S.Log.Warn($"导入皮肤 {Path.GetFileName(files[i])} 失败：{ex.Message}");
                }
            }

            // 最后成功导入的那张设为当前
            if (added.Count > 0)
            {
                var last = added[^1];
                row.Model.LocalSkinId = last.Id;
                row.Model.LocalSkinArmModel = last.ArmModel.ToString().ToUpperInvariant();
                S.Accounts.Update(row.Model);
            }

            RenderLocalLibrary(row.Model);

            LocalSkinState.Text = (added.Count, failed.Count) switch
            {
                (0, _) => "没有导入任何皮肤。",
                (1, 0) => $"已导入并应用「{added[0].DisplayName}」。",
                (_, 0) => $"已导入 {added.Count} 张皮肤，当前用「{added[^1].DisplayName}」。",
                _ => $"导入 {added.Count} 张，{failed.Count} 张失败。",
            };

            if (failed.Count > 0)
                Ui.Warn("有几张没能导入：\n\n" + string.Join("\n", failed));
        }
        finally
        {
            BtnSkinImport.IsEnabled = true;
        }
    }

    /// <summary>删掉一张用户导入的皮肤。正在用的那张被删掉时，自动退回 Steve。</summary>
    private void DeleteUserSkin(SkinEntry entry)
    {
        if (entry.IsBuiltIn || _viewing is not { } row) return;

        if (!Ui.Confirm($"确定要删除皮肤「{entry.DisplayName}」吗？\n\n文件会一起删掉。"))
            return;

        var kind = LibraryOf(row.Model);

        // 删之前先看账号有没有绑着它——绑着就得改绑，不然会指向一个不存在的 id
        var wasBound = string.Equals(row.Model.LocalSkinId, entry.Id, StringComparison.OrdinalIgnoreCase);

        S.SkinLibrary.Delete(entry);

        if (wasBound)
        {
            row.Model.LocalSkinId = SkinLibrary.SteveId(kind);
            row.Model.LocalSkinArmModel = "CLASSIC";
            S.Accounts.Update(row.Model);
        }

        S.Log.Info($"已删除皮肤 {entry.DisplayName}");
        RenderLocalLibrary(row.Model);
        LocalSkinState.Text = wasBound
            ? $"已删除「{entry.DisplayName}」，当前改用 Steve。"
            : $"已删除「{entry.DisplayName}」。";
    }

    /// <summary>把账号里缓存的皮肤 / 披风画到界面上。纯本地操作，不联网。</summary>
    private void RenderAppearance(Account account)
    {
        var skins = account.Skins ?? new List<SkinInfo>();
        var capes = account.Capes ?? new List<CapeInfo>();

        // ── 左栏预览 ──
        // 已经绑了本地皮肤就画本地那张，否则画服务端当前的
        var localPath = LocalSkinPathOf(account);
        var skinImage = localPath is not null ? LoadTextureFromFile(localPath) : LoadTexture(account.SkinUrl);
        SkinPreview.Source = skinImage;
        SkinPreview.Visibility = skinImage is null ? Visibility.Collapsed : Visibility.Visible;

        SkinVariantText.Text = skinImage is null
            ? "还没读取到皮肤。"
            : localPath is not null
                ? "本地皮肤：" + (account.LocalSkinArmModel ?? "CLASSIC").ToUpperInvariant() switch
                  {
                      "SLIM" => "纤细（Alex）",
                      _ => "经典（Steve）",
                  }
                : "模型：" + (string.Equals(account.SkinVariant, "SLIM", StringComparison.OrdinalIgnoreCase)
                    ? "纤细（Alex）" : "经典（Steve）");

        var capeImage = LoadCapeTexture(account.CapeUrl);
        CapePreview.Source = capeImage;
        CapePreviewBox.Visibility = capeImage is null ? Visibility.Collapsed : Visibility.Visible;
        CapeNoneText.Visibility = capeImage is null ? Visibility.Visible : Visibility.Collapsed;

        // ── 皮肤清单 ──
        SkinList.Children.Clear();
        SkinListEmpty.Visibility = skins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in skins)
        {
            var skin = item;
            SkinList.Children.Add(MakeTile(
                HeadTexture(skin.Url),
                skin.IsActive,
                skin.IsSlim ? "纤细" : "经典",
                $"{skin.VariantLabel}{(skin.IsActive ? "（当前）" : "")}\n点一下切换成这一款",
                () => ApplySkin(skin)));
        }

        // ── 披风清单 ──
        CapeList.Children.Clear();
        CapeListEmpty.Visibility = capes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in capes)
        {
            var cape = item;
            CapeList.Children.Add(MakeTile(
                LoadCapeTexture(cape.Url),
                cape.IsActive,
                null,
                $"{cape.DisplayName}{(cape.IsActive ? "（当前）" : "")}\n点一下戴上",
                () => ApplyCape(cape)));
        }

        BtnCapeNone.IsEnabled = capes.Any(c => c.IsActive);
        _hasActiveCape = capes.Any(c => c.IsActive);
    }

    /// <summary>账号当前绑定的本地皮肤文件路径；没绑定或文件不在就返回 null。</summary>
    private static string? LocalSkinPathOf(Account account)
    {
        if (account.Type == AccountType.ThirdParty) return null;

        var kind = LibraryOf(account);
        var entries = S.SkinLibrary.List(kind);
        var bound = entries.FirstOrDefault(e => e.Id == account.LocalSkinId);
        if (bound is null || !S.SkinLibrary.IsReady(bound)) return null;

        // 微软账号：没显式绑本地皮肤时应该看服务端那一张，别被内置的 Steve 盖掉
        if (account.Type == AccountType.Microsoft && bound.IsBuiltIn) return null;

        return S.SkinLibrary.PathOf(bound);
    }

    /// <summary>把这一屏要用到的纹理都补进本地缓存，然后重画一次。</summary>
    private async Task LoadAppearanceImagesAsync(Account account, int generation)
    {
        if (SelfCheck.Running) return;

        var urls = new List<string>();
        if (!string.IsNullOrEmpty(account.SkinUrl)) urls.Add(account.SkinUrl);
        if (!string.IsNullOrEmpty(account.CapeUrl)) urls.Add(account.CapeUrl);
        foreach (var skin in account.Skins ?? new List<SkinInfo>())
            if (!string.IsNullOrEmpty(skin.Url)) urls.Add(skin.Url);
        foreach (var cape in account.Capes ?? new List<CapeInfo>())
            if (!string.IsNullOrEmpty(cape.Url)) urls.Add(cape.Url);

        var missing = urls.Distinct()
            .Where(url => !File.Exists(SkinService.CachedTexturePath(url)))
            .ToList();

        if (missing.Count == 0) return;

        var got = false;
        foreach (var url in missing)
        {
            try
            {
                await S.Skin.EnsureTextureAsync(url, CancellationToken.None);
                got = true;
            }
            catch (Exception ex)
            {
                // 单张图下载失败不该影响其它图，也不该弹窗——日志里留一条就够了
                S.Log.Warn($"纹理下载失败（{url}）：{ex.Message}");
            }
        }

        // 用户可能已经切走了、窗口可能已经关了，这时别再动界面
        if (!got || _closing || generation != _appearanceLoad) return;

        RenderAppearance(account);
    }

    /// <summary>首次展示某个账号时自动拉一次档案。失败只写一行状态，不弹窗。</summary>
    private async Task FetchAppearanceAsync(Account account, int generation)
    {
        BeginAppearanceBusy("正在读取外观信息…");
        try
        {
            await S.Skin.RefreshAsync(account, CancellationToken.None);
            S.Accounts.Update(account);

            if (_closing || generation != _appearanceLoad) return;

            RenderAppearance(account);
            _ = LoadAppearanceImagesAsync(account, generation);
            SkinStatus.Text = "";
        }
        catch (Exception ex)
        {
            S.Log.Warn($"读取外观失败：{ex.Message}");
            if (_closing || generation != _appearanceLoad) return;
            SkinStatus.Text = $"读取外观失败：{ex.Message}";
        }
        finally
        {
            if (!_closing && generation == _appearanceLoad) EndAppearanceBusy();
        }
    }

    /// <summary>
    /// 外观操作的统一外壳：先摆出「进行中」，跑完存账号并重画，出错写一行状态。
    /// 所有网络动作都从这里走，省得每个按钮各写一遍守卫。
    /// </summary>
    private async void RunAppearanceAction(string busy, Func<Account, Task<string>> action)
    {
        if (_viewing is not { } row || !row.Model.SupportsAppearance) return;

        var account = row.Model;
        var generation = _appearanceLoad;

        BeginAppearanceBusy(busy);
        try
        {
            var message = await action(account);
            S.Accounts.Update(account);

            if (_closing || generation != _appearanceLoad) return;

            RenderAppearance(account);
            _ = LoadAppearanceImagesAsync(account, generation);
            SkinStatus.Text = message;
        }
        catch (Exception ex)
        {
            S.Log.Error($"外观操作失败：{ex.Message}");
            if (_closing || generation != _appearanceLoad) return;
            SkinStatus.Text = $"操作失败：{ex.Message}";
        }
        finally
        {
            if (!_closing && generation == _appearanceLoad) EndAppearanceBusy();
        }
    }

    private void SkinRefresh_Click(object sender, RoutedEventArgs e) =>
        RunAppearanceAction("正在读取最新外观信息…", async account =>
        {
            await S.Skin.RefreshAsync(account, CancellationToken.None);
            return $"已刷新：{account.Skins?.Count ?? 0} 款皮肤，{account.Capes?.Count ?? 0} 款披风。";
        });

    private void SkinReset_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("把皮肤重置成默认的 Steve / Alex？\n\n" +
                        "你自己上传过的皮肤不会被删掉，之后还能在列表里切回来。"))
            return;

        RunAppearanceAction("正在重置皮肤…", async account =>
        {
            await S.Skin.ResetSkinAsync(account, CancellationToken.None);
            return "皮肤已重置为默认。";
        });
    }

    private async void SkinUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_viewing is not { } row || !row.Model.SupportsAppearance) return;

        var files = Ui.PickFiles("PNG 图片 (*.png)|*.png", multi: false);
        if (files.Length == 0) return;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(files[0]);
        }
        catch (Exception ex)
        {
            Ui.Warn($"读不了这个文件：{ex.Message}");
            return;
        }

        // 模型由下拉框决定：经典（4px 手臂）或纤细（3px）
        var variant = SkinModel.SelectedIndex == 1 ? "SLIM" : "CLASSIC";
        var fileName = Path.GetFileName(files[0]);

        RunAppearanceAction("正在上传皮肤…", async account =>
        {
            await S.Skin.UploadSkinAsync(account, bytes, variant, CancellationToken.None);
            return $"已上传并应用「{fileName}」。";
        });
    }

    private void ApplySkin(SkinInfo skin) =>
        RunAppearanceAction($"正在切换成{skin.VariantLabel}皮肤…", async account =>
        {
            await S.Skin.SelectSkinAsync(account, skin.Id, CancellationToken.None);
            return $"已切换皮肤：{skin.VariantLabel}。";
        });

    private void ApplyCape(CapeInfo cape) =>
        RunAppearanceAction($"正在戴上「{cape.DisplayName}」…", async account =>
        {
            await S.Skin.SelectCapeAsync(account, cape.Id, CancellationToken.None);
            return $"已戴上「{cape.DisplayName}」。";
        });

    private void RemoveCape() =>
        RunAppearanceAction("正在取消披风…", async account =>
        {
            await S.Skin.ClearCapeAsync(account, CancellationToken.None);
            return "已取消披风。";
        });

    /// <summary>「不戴披风」按钮。</summary>
    private void CapeNone_Click(object sender, RoutedEventArgs e) => RemoveCape();

    private void BeginAppearanceBusy(string message)
    {
        SkinMarquee.Visibility = Visibility.Visible;
        SkinStatus.Text = message;
        SetAppearanceEnabled(false);
    }

    private void EndAppearanceBusy()
    {
        SkinMarquee.Visibility = Visibility.Collapsed;
        SetAppearanceEnabled(true);
    }

    private void SetAppearanceEnabled(bool enabled)
    {
        BtnSkinRefresh.IsEnabled = enabled;
        BtnSkinUpload.IsEnabled = enabled;
        BtnSkinReset.IsEnabled = enabled;
        SkinModel.IsEnabled = enabled;

        // 自定义皮肤的「导入」是入口；删除是每张缩略图右上角的小叉，
        // 那些按钮在 RenderLocalLibrary 里现建，忙碌时直接重画一遍即可
        BtnSkinImport.IsEnabled = enabled;
        LocalSkinModel.IsEnabled = enabled;

        // 「不戴披风」只有在确实戴着披风时才有意义。
        // 忙碌时全部按钮置灰，忙完再按「有没有戴披风」还原。
        BtnCapeNone.IsEnabled = enabled && _hasActiveCape;
    }

    /// <summary>当前账号是不是戴着披风。决定「不戴披风」按钮的可用性。</summary>
    private bool _hasActiveCape;

    // ── 外观：控件与图片 ────────────────────────────────────

    /// <summary>做一块可点的缩略图。image 为 null 时退化成一行文字（「无」「经典」）。</summary>
    private Button MakeTile(ImageSource? image, bool active, string? glyph, string tooltip, Action onClick)
    {
        var tile = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(active ? 2 : 1),
            SnapsToDevicePixels = true,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "LayerBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "StrokeBrush");

        if (image is not null)
        {
            var picture = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
            tile.Child = picture;
        }
        else
        {
            var text = new TextBlock
            {
                Text = glyph ?? "—",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            tile.Child = text;
        }

        var button = new Button
        {
            Style = (Style)FindResource("SubtleButton"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 8),
            Content = tile,
            ToolTip = tooltip,
            // 已经是当前那一件了就别让人再点一次，按钮直接说明白
            IsEnabled = !active,
        };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => onClick();

        return button;
    }

    /// <summary>
    /// 读一张已经缓存到本地的纹理；没缓存、或文件坏了，都返回 null。
    ///
    /// 统一转成 Bgra32：Minecraft 官方的皮肤（Steve / Alex）是调色板 PNG，
    /// WPF 解出来是 Indexed8（每像素 1 字节），下游按 4 字节读会错位、渲染成黑块。
    /// 用户自己导入的 PNG 是 RGBA，所以只有官方那几张会中招——很难从现象反推。
    /// </summary>
    private static BitmapSource? LoadTexture(string? url)
    {
        var path = SkinService.CachedTexturePath(url);
        return LoadTextureFromFile(path);
    }

    private static BitmapSource? LoadTextureFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            // OnLoad + IgnoreImageCache：读完就断开文件句柄，之后重新下载覆盖同一个文件时
            // 也不会被 WPF 的图片缓存拦住、继续显示旧图
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            BitmapSource result = frame;

            if (frame.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = frame;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                result = converted;
            }

            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 皮肤缩略图：只取头部那一块（脸 + 帽子第二层），比整张 64×64 缩到 46px 看得清得多。
    /// </summary>
    private static ImageSource? HeadTexture(string? url)
    {
        var bitmap = LoadTexture(url);
        return bitmap is null ? null : SkinPreviewRenderer.Head(bitmap, 64) ?? bitmap;
    }

    /// <summary>
    /// 披风缩略图 / 预览：整张纹理里只有左上角那一小块是真正穿在身上的部分。
    /// 现代格式是 64×32 里的 (1,1,10,16)，老格式是 22×17 的整张。
    /// </summary>
    private static ImageSource? LoadCapeTexture(string? url)
    {
        var bitmap = LoadTexture(url);
        return bitmap is null ? null : SkinPreviewRenderer.Cape(bitmap, 60, 96) ?? bitmap;
    }

    private void SetCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_viewing is not { } row) return;

        S.Accounts.Select(row.Model.Id);
        S.Log.Info($"已切换到账号 {row.Model.Name}");

        Refresh();
        FooterText.Text = $"已切换：现在用「{row.Model.Name}」启动游戏。";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewing is not { } row) return;

        if (!Ui.Confirm($"确定要删除账号「{row.Model.Name}」吗？\n\n下次要用得重新登录。"))
            return;

        var name = row.Model.Name;
        S.Accounts.Remove(row.Model.Id);
        S.Log.Info($"已删除账号 {name}");

        _viewing = null;
        Refresh();
        FooterText.Text = $"已删除账号「{name}」。";
    }

    // ── 右列：添加 ──────────────────────────────────────────

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        ShowPane(true);
        SyncAddPanels();
    }

    private void BackToDetail_Click(object sender, RoutedEventArgs e) => ShowPane(false);

    // ── 离线 ────────────────────────────────────────────────

    private void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineName.Text.Trim();

        if (name.Length == 0)
        {
            Ui.Warn("起个名字吧，随便什么都行。");
            OfflineName.Focus();
            return;
        }

        if (name.Length > 16)
        {
            Ui.Warn("名字最多 16 个字符——这是 Minecraft 自己的限制。");
            return;
        }

        // 重名会变成同一份离线身份，但列表里会出现两条一模一样的，看着像出了 bug
        if (S.Accounts.Accounts.Any(a => a.Type == AccountType.Offline
                                         && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Warn($"已经有一个叫「{name}」的离线账号了。\n\n换个名字，或者直接返回去用那一个。");
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

        OfflineName.Text = "";
        _viewing = null;
        Refresh();

        // 加完不关窗口：直接切到新账号的详情，用户能马上看到结果，
        // 想接着加第二个也不用重新打开。以前这里是 Close()，点完窗口就没了。
        ShowPane(false);
        FooterText.Text = $"已添加离线账号「{name}」，并设为当前账号。";
    }

    // ── 微软正版 ────────────────────────────────────────────
    //
    // 流程分两步（原因见 MicrosoftAuth 的类注释）：内置 client_id 只能走桌面回调，
    // 没法在本机自动接码，所以第一步打开浏览器、第二步让用户把地址栏里的 code 粘回来。

    private void MsaBegin_Click(object sender, RoutedEventArgs e)
    {
        var url = S.Microsoft.BuildAuthorizeUrl();

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            S.Log.Error($"微软登录：打开浏览器失败：{ex.Message}");
            if (!_closing)
                Ui.Error($"打不开浏览器。可以手动把这个地址粘到浏览器里登录：\n\n{url}");
            return;
        }

        S.Log.Info("微软登录：已打开浏览器，等待用户把 code 粘回来");

        // 切到「粘贴授权码」界面：藏起开始按钮，亮出输入框
        BtnMsaStart.Visibility = Visibility.Collapsed;
        MsaPaste.Visibility = Visibility.Visible;
        BtnMsaCancel.Visibility = Visibility.Visible;
        MsaMarquee.Visibility = Visibility.Visible;
        MsaStatus.Text = "已在浏览器打开微软登录页。登录完把地址栏里的 code 粘到下面，再点「完成登录」。";
        MsaCode.Focus();
    }

    private async void MsaFinish_Click(object sender, RoutedEventArgs e)
    {
        var raw = MsaCode.Text.Trim();
        if (raw.Length == 0)
        {
            Ui.Warn("先把浏览器地址栏里的 code 粘进来。");
            MsaCode.Focus();
            return;
        }

        _msaCts = new CancellationTokenSource();
        BtnMsaFinish.IsEnabled = false;
        MsaMarquee.Visibility = Visibility.Visible;
        MsaStatus.Text = "正在用授权码换令牌…";

        var canceled = false;
        try
        {
            var result = await S.Microsoft.ExchangeCodeAsync(raw, _msaCts.Token);

            var account = new Account
            {
                Name = result.Name,
                Uuid = result.Uuid,
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                Xuid = result.Xuid,
                Type = AccountType.Microsoft,
                ExpiresAt = result.ExpiresAt,
            };

            // 登录时档案接口顺手把皮肤和披风也返回了，直接存下来——
            // 详情页一打开就有图看，不用再让用户点一次「刷新」。
            if (result.Skins is not null || result.Capes is not null)
                SkinService.Apply(account, result.Skins, result.Capes);

            S.Accounts.Add(account);

            S.Log.Success($"微软账号 {result.Name} 登录成功");

            _viewing = null;
            Refresh();
            ShowPane(false);

            // 不弹 MessageBox：浏览器那一步刚结束，再弹窗很打断人，底部状态条写清楚就够了
            FooterText.Text = $"已添加微软账号「{result.Name}」，并设为当前账号。";
            return;
        }
        catch (OperationCanceledException)
        {
            MsaStatus.Text = "已取消登录。";
            canceled = true;
        }
        catch (Exception ex)
        {
            MsaStatus.Text = $"登录失败：{ex.Message}";
            S.Log.Error($"微软登录失败：{ex.Message}");
            if (!_closing)
                Ui.Error($"微软登录失败：\n\n{ex.Message}");
        }
        finally
        {
            MsaMarquee.Visibility = Visibility.Collapsed;
            BtnMsaFinish.IsEnabled = true;
            _msaCts?.Dispose();
            _msaCts = null;
        }

        // 失败就保留粘贴框让用户改了重试；取消则整体回到初始态
        if (canceled) ResetMsaUi();
    }

    private void MsaCancel_Click(object sender, RoutedEventArgs e)
    {
        _msaCts?.Cancel();
        MsaStatus.Text = "已取消登录。";
        ResetMsaUi();
    }

    /// <summary>把微软登录区交还到初始态：显示「打开浏览器」、藏起粘贴框与取消。</summary>
    private void ResetMsaUi()
    {
        BtnMsaStart.Visibility = Visibility.Visible;
        MsaPaste.Visibility = Visibility.Collapsed;
        BtnMsaCancel.Visibility = Visibility.Collapsed;
        MsaMarquee.Visibility = Visibility.Collapsed;
        MsaCode.Text = "";
    }

    // ── 外置登录 ────────────────────────────────────────────

    private async void ThirdProbe_Click(object sender, RoutedEventArgs e)
    {
        var server = ThirdServer.Text.Trim();
        if (server.Length == 0)
        {
            Ui.Warn("先填服务器地址。");
            ThirdServer.Focus();
            return;
        }

        ThirdProbeHint.Text = "正在探测…";

        try
        {
            var name = await S.Third.DescribeServerAsync(server);
            ThirdProbeHint.Text = string.IsNullOrEmpty(name)
                ? "这个地址能连上，但没返回服务器名——通常仍然可以用，继续填账号登录试试。"
                : $"识别到服务器：{name}";
        }
        catch (Exception ex)
        {
            ThirdProbeHint.Text = $"探测失败：{ex.Message}";
        }
    }

    private async void ThirdLogin_Click(object sender, RoutedEventArgs e)
    {
        var server = ThirdServer.Text.Trim();
        var user = ThirdUser.Text.Trim();
        var password = ThirdPassword.Password;

        if (server.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            Ui.Warn("服务器地址、账号、密码都要填。");
            return;
        }

        BtnThirdLogin.IsEnabled = false;
        ThirdStatus.Text = "正在登录…";

        try
        {
            var result = await S.Third.LoginAsync(server, user, password);

            S.Accounts.Add(new Account
            {
                Name = result.Name,
                Uuid = result.Uuid,
                AccessToken = result.AccessToken,
                Type = AccountType.ThirdParty,
                Server = server,
                ExpiresAt = result.ExpiresAt,
            });

            S.Log.Success($"外置登录成功：{result.Name} @ {server}");

            ThirdPassword.Clear();
            ThirdStatus.Text = $"登录成功：{result.Name}";

            _viewing = null;
            Refresh();

            // 同样不关窗口，直接把这个新账号的详情摆出来
            ShowPane(false);
            FooterText.Text = $"已添加外置登录账号「{result.Name}」，并设为当前账号。启动时会自动挂上 authlib-injector。";
        }
        catch (Exception ex)
        {
            ThirdStatus.Text = $"登录失败：{ex.Message}";
            S.Log.Error($"外置登录失败：{ex.Message}");
            Ui.Error($"外置登录失败：\n\n{ex.Message}");
        }
        finally
        {
            BtnThirdLogin.IsEnabled = true;
        }
    }

    // ── 外置登录组件（authlib-injector）──────────────────────

    /// <summary>把本地 jar 的状态同步到界面上。</summary>
    private void RefreshInjector()
    {
        if (AuthlibInjector.LocalPath is not { } jar)
        {
            InjectorStatus.Text = "还没准备。启动外置登录账号时会自动下载，也可以现在点一下提前备好。";
            BtnInjector.Content = "现在准备";
            return;
        }

        long size = 0;
        try { size = new FileInfo(jar).Length / 1024; } catch { /* 读不到就当 0 */ }

        var when = AuthlibInjector.LocalTimestamp?.ToString("yyyy-MM-dd HH:mm") ?? "未知时间";
        var source = S.Config.UseMirror ? "BMCLAPI 镜像" : "官方源";
        InjectorStatus.Text = $"已就绪：{size} KB，{when} 下载。启动这个账号时会自动挂上。（下载源：{source}）";
        BtnInjector.Content = "重新下载";
    }

    private async void InjectorPrepare_Click(object sender, RoutedEventArgs e)
    {
        var refresh = false;
        try
        {
            // 已经有一份了，这个按钮的语义就变成「重新下载」
            refresh = AuthlibInjector.LocalPath is not null;

            BtnInjector.IsEnabled = false;
            InjectorStatus.Text = "正在读取版本信息…";

            var progress = new Progress<double>(r =>
                InjectorStatus.Text = $"正在下载外置登录组件… {(int)(r * 100)}%");

            var jar = await S.Injector.EnsureAsync(S.Config.UseMirror, progress, force: refresh);
            if (jar is null)
            {
                InjectorStatus.Text = "准备失败。检查一下网络或换个下载源，再试一次。";
                return;
            }

            S.Log.Success($"外置登录组件已就绪：{jar}");
        }
        catch (Exception ex)
        {
            InjectorStatus.Text = $"准备失败：{ex.Message}";
            S.Log.Error($"准备外置登录组件失败：{ex.Message}");
        }
        finally
        {
            BtnInjector.IsEnabled = true;
            RefreshInjector();
        }
    }

    private void InjectorFolder_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFolder(AppPaths.LauncherRoot);

    // ── 窗口 ────────────────────────────────────────────────

    /// <summary>云母是不是真的开起来了。开不起来就得给窗口一个不透明底色。</summary>
    private bool _micaApplied;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ApplyBackdrop();
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>
    /// 和主窗口用同一套云母设置。DialogBrush 兜底的理由见 WindowEffects.ApplyDialogBackdrop。
    /// </summary>
    private void ApplyBackdrop()
    {
        _micaApplied = WindowEffects.ApplyDialogBackdrop(this);

        App.Services.Log.Info(_micaApplied
            ? "账号窗口材质：云母（Mica）已启用"
            : "账号窗口材质：系统不支持云母，已改用纯色背景");
    }

    private void OnThemeChanged() => Dispatcher.Invoke(() =>
    {
        WindowEffects.ApplyImmersiveDarkMode(this, ThemeManager.IsDark);
        ApplyBackdrop();
    });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>列表里的一行。</summary>
    public sealed class AccountRow
    {
        public AccountRow(Account model, bool isCurrent)
        {
            Model = model;
            IsCurrent = isCurrent;
        }

        public Account Model { get; }

        public bool IsCurrent { get; }

        public string Name => Model.Name;

        public string Initial => string.IsNullOrEmpty(Model.Name)
            ? "?" : Model.Name[..1].ToUpperInvariant();

        public string Subtitle
        {
            get
            {
                var parts = new List<string> { Model.TypeLabel };
                if (Model.IsExpired) parts.Add("凭证已过期");

                var text = string.Join("　·　", parts);
                return Model.Type == AccountType.ThirdParty && Model.Server.Length > 0
                    ? $"{text}（{Model.Server}）"
                    : text;
            }
        }
    }
}
