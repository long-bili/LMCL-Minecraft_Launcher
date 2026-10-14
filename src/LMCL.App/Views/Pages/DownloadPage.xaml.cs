using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using LMCL.App.Animations;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.ModPacks;
using LMCL.Core.Resources;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 下载中心。
///
/// 左边一条分区导航（游戏版本 / 模组 / 资源包 / 数据包 / 光影 / 整合包），
/// 右边一次只显示一个分区，切换时来一段轻微的淡入 + 右移，让「换了一批」看得出来。
///
/// 分区里有两类完全不同的活：
///   · 游戏版本 —— 装原版，或者在原版上装 Forge / NeoForge / Fabric / Quilt；
///   · 其余五个 —— 从 Modrinth 搜东西往某个实例里装。
/// 整合包单独占一格：它装出来就是一个完整实例，不是往已有实例里塞文件。
/// </summary>
public partial class DownloadPage : PageBase
{
    private const string AnyVersion = "任意";
    private const int PageSize = 20;

    /// <summary>
    /// 切换分区的动画时长。
    /// 110ms 太短，等面板刚铺好就得收尾，观感上像「闪了一下」；160ms 能看清是换了一屏，
    /// 又不会让人等——和全项目其它过渡（140~340ms）同一个量级。
    /// </summary>
    private static readonly TimeSpan SectionAnimDuration = TimeSpan.FromMilliseconds(160);

    private enum Section { Minecraft, Mod, ResourcePack, DataPack, ShaderPack, Modpack }

    /// <summary>
    /// 版本类型筛选。官方清单里一共有 release / snapshot / old_beta / old_alpha / pending 五种，
    /// 但清单是远端拉来的，所以按「清单里实际出现过的类型」动态生成——
    /// Mojang 以后再加新类型时，不改代码也能露出来。
    /// </summary>
    private readonly List<FilterChoice> _typeFilters = [new("", "全部")];

    private static readonly (string Id, string Label)[] KnownTypes =
    [
        ("release", "正式版"),
        ("snapshot", "快照"),
        ("old_beta", "远古 Beta"),
        ("old_alpha", "远古 Alpha"),
        ("pending", "未发布"),
    ];

    private readonly List<LoaderFilter> _modLoaders =
    [
        new(null, "任意"),
        new("forge", "Forge"),
        new("neoforge", "NeoForge"),
        new("fabric", "Fabric"),
        new("quilt", "Quilt"),
    ];

    private readonly List<LoaderFilter> _packLoaders =
    [
        new(null, "任意"),
        new("forge", "Forge"),
        new("neoforge", "NeoForge"),
        new("fabric", "Fabric"),
        new("quilt", "Quilt"),
    ];

    /// <summary>
    /// 分类（Tag）筛选。分类名必须**严格取自 Modrinth 的官方分类名**，
    /// 写错一个字母整次搜索就会 400（服务端认不出这个 facet）。
    /// 表是照着 <c>GET /v2/tag/category</c> 的实际返回值列的，
    /// 各类型的分类集合互不相同，所以按类型分表、切换分区时重建下拉项。
    ///
    /// 注意 <see cref="CommunityResourceType.DataPack"/> 故意留空：
    /// Modrinth 目前**没有**给数据包定义任何分类，那边只能按关键字搜。
    /// </summary>
    private static readonly Dictionary<CommunityResourceType, (string Id, string Label)[]> TagsByType = new()
    {
        // GET /v2/tag/category → project_type=mod
        [CommunityResourceType.Mod] =
        [
            ("optimization", "性能优化"), ("technology", "科技"), ("magic", "魔法"),
            ("adventure", "冒险"), ("worldgen", "世界生成"), ("mobs", "生物"),
            ("equipment", "装备"), ("decoration", "装饰"), ("storage", "仓储"),
            ("food", "食物"), ("utility", "实用"), ("library", "支持库"),
            ("transportation", "运输"), ("management", "管理"), ("minigame", "小游戏"),
            ("economy", "经济"), ("social", "社交"), ("game-mechanics", "游戏机制"),
        ],
        // GET /v2/tag/category → project_type=resourcepack
        [CommunityResourceType.ResourcePack] =
        [
            ("realistic", "写实风"), ("vanilla-like", "原版风"), ("themed", "主题风"),
            ("simplistic", "简约风"), ("audio", "音频"), ("gui", "界面"),
            ("fonts", "字体"), ("locale", "语言"), ("blocks", "方块"),
            ("items", "物品"), ("entities", "实体"), ("environment", "环境"),
            ("equipment", "装备"), ("combat", "战斗"), ("decoration", "装饰"),
            ("modded", "模组支持"), ("core-shaders", "核心光影"), ("tweaks", "细节调整"),
        ],
        // Modrinth 没有给数据包定义分类（该类型下返回空表），所以这里不提供筛选
        [CommunityResourceType.DataPack] = [],
        // GET /v2/tag/category → project_type=shader
        [CommunityResourceType.Shader] =
        [
            ("realistic", "写实风"), ("semi-realistic", "半写实"), ("fantasy", "幻想风"),
            ("cartoon", "卡通"), ("vanilla-like", "原版风"), ("atmosphere", "氛围"),
            ("bloom", "泛光"), ("shadows", "阴影"), ("reflections", "反射"),
            ("colored-lighting", "彩色光照"), ("pbr", "PBR 材质"), ("path-tracing", "光追"),
            ("foliage", "植被"), ("screenshot", "截图向"), ("potato", "低配"),
            ("high", "高配"), ("medium", "中配"), ("low", "低配"),
        ],
        // GET /v2/tag/category → project_type=modpack
        [CommunityResourceType.ModPack] =
        [
            ("adventure", "冒险"), ("kitchen-sink", "大杂烩"), ("technology", "科技"),
            ("magic", "魔法"), ("quests", "任务"), ("multiplayer", "多人"),
            ("combat", "战斗"), ("challenging", "高难度"), ("lightweight", "轻量"),
            ("optimization", "性能优化"),
        ],
    };

    // ── 游戏版本 ────────────────────────────────────────────
    private IReadOnlyList<RemoteVersion> _remote = Array.Empty<RemoteVersion>();
    private string _versionKeyword = "";

    // ── 社区资源（模组 / 资源包 / 数据包 / 光影共用）─────────
    private readonly List<ModHitRow> _hits = new();
    private ResourceProject? _selectedMod;
    private IReadOnlyList<ResourceVersion> _modVersions = Array.Empty<ResourceVersion>();
    private ResourceVersion? _chosenVersion;
    private InstalledVersion? _modTarget;
    private bool _modUiReady;
    private int _offset;
    private int _total = -1;

    /// <summary>当前资源分区在找什么（模组 / 资源包 / 数据包 / 光影）。</summary>
    private Section _catalogSection = Section.Mod;

    // ── 整合包 ──────────────────────────────────────────────
    private List<PackRow> _packs = new();
    private ResourceProject? _selectedPack;

    // ── 分区切换 ────────────────────────────────────────────
    private Section _section = Section.Minecraft;
    private Section _displayed = Section.Minecraft;
    private bool _sectionUiReady = true;
    private bool _syncingSection;
    private Storyboard? _sectionStoryboard;
    private int _sectionAnimGeneration;

    private bool _ready;

    public DownloadPage()
    {
        InitializeComponent();
        InitializeCombos();
        _ready = true;

        // 默认停在「游戏版本」分区。这一步也把导航的第一项点亮——
        // 少了它，左侧一整列都是未选中状态，用户不知道该点哪儿
        SectionList.SelectedIndex = 0;
        _displayed = Section.Minecraft;

        Loaded += (_, _) => EnsureSectionVisual(animate: false);
    }

    private void InitializeCombos()
    {
        // 清单还没拉到时先把官方那五种类型铺上，用户一眼能看见全部选项；
        // 清单到了再按「实际有哪些」收一遍
        RefreshTypeFilters(assumeAllKnown: true);
        TypeCombo.SelectedIndex = 0;

        ModLoaderCombo.ItemsSource = _modLoaders;
        ModLoaderCombo.SelectedIndex = 0;

        RefreshTagFilters();

        PackLoaderCombo.ItemsSource = _packLoaders;
        PackLoaderCombo.SelectedIndex = 0;

        PackVersionCombo.ItemsSource = new List<string> { AnyVersion };
        PackVersionCombo.SelectedIndex = 0;
    }

    public override void OnNavigatedTo()
    {
        if (_remote.Count == 0) _ = ReloadRemoteAsync();
    }

    // ── 分区切换 ────────────────────────────────────────────

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSection || !_sectionUiReady) return;

        if (SectionList.SelectedItem is not ListBoxItem { Tag: string tag }) return;

        // 整合包单独占一个 ListBox，选上面这组就得把它让出来
        ClearOther(ModpackSectionList);

        _section = tag switch
        {
            "mod" => Section.Mod,
            "resourcepack" => Section.ResourcePack,
            "datapack" => Section.DataPack,
            "shaderpack" => Section.ShaderPack,
            _ => Section.Minecraft
        };

        ApplySection(_section);
    }

    private void ModpackSectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSection || !_sectionUiReady) return;
        if (ModpackSectionList.SelectedItem is not ListBoxItem) return;

        ClearOther(SectionList);
        _section = Section.Modpack;
        ApplySection(_section);
    }

    /// <summary>把另一个导航列表的选中清掉。清之前先立旗，别让它的事件又绕回来。</summary>
    private void ClearOther(ListBox other)
    {
        if (other.SelectedIndex < 0) return;

        _syncingSection = true;
        try { other.SelectedIndex = -1; }
        finally { _syncingSection = false; }
    }

    private void ApplySection(Section section)
    {
        _sectionUiReady = false;
        try
        {
            SyncSectionSelection();
        }
        finally
        {
            _sectionUiReady = true;
        }

        if (section is Section.Modpack)
        {
            EnsureModpackReady();
            RefreshPackTagFilters();
        }
        else if (section is not Section.Minecraft)
        {
            EnsureCatalogReady(section);
        }

        EnsureSectionVisual(animate: true);
    }

    /// <summary>导航选中项跟着当前分区走。</summary>
    private void SyncSectionSelection()
    {
        _syncingSection = true;
        try
        {
            if (_section == Section.Modpack)
            {
                SectionList.SelectedIndex = -1;
                if (ModpackSectionList.SelectedIndex != 0) ModpackSectionList.SelectedIndex = 0;
                return;
            }

            if (ModpackSectionList.SelectedIndex >= 0) ModpackSectionList.SelectedIndex = -1;

            var index = _section switch
            {
                Section.Mod => 1,
                Section.ResourcePack => 2,
                Section.DataPack => 3,
                Section.ShaderPack => 4,
                _ => 0
            };
            if (SectionList.SelectedIndex != index) SectionList.SelectedIndex = index;
        }
        finally
        {
            _syncingSection = false;
        }
    }

    private static bool IsCatalog(Section section) =>
        section is Section.Mod or Section.ResourcePack or Section.DataPack or Section.ShaderPack;

    private FrameworkElement PaneFor(Section section) => section switch
    {
        Section.Modpack => PaneModpack,
        Section.Minecraft => PaneMinecraft,
        _ => PaneCatalog
    };

    private TranslateTransform OffsetFor(Section section) => section switch
    {
        Section.Modpack => PaneModpackOffset,
        Section.Minecraft => PaneMinecraftOffset,
        _ => PaneCatalogOffset
    };

    private void EnsureSectionVisual(bool animate)
    {
        var target = _section;

        if (_displayed == target) return;

        if (!animate)
        {
            StopSectionAnimation();
            ApplySectionInstant(target);
            _displayed = target;
            return;
        }

        AnimateSectionSwitch(_displayed, target);
        _displayed = target;
    }

    private void ApplySectionInstant(Section section)
    {
        ApplyPane(PaneMinecraft, PaneMinecraftOffset, section == Section.Minecraft);
        ApplyPane(PaneCatalog, PaneCatalogOffset, IsCatalog(section));
        ApplyPane(PaneModpack, PaneModpackOffset, section == Section.Modpack);
    }

    private static void ApplyPane(UIElement pane, TranslateTransform offset, bool show)
    {
        pane.Opacity = show ? 1 : 0;
        pane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        pane.IsHitTestVisible = show;
        offset.X = 0;
    }

    private void AnimateSectionSwitch(Section from, Section to)
    {
        StopSectionAnimation();

        var incoming = PaneFor(to);
        var outgoing = PaneFor(from);

        // 同组内切换（比如资源包 → 光影）面板不搬家，但也不能「啪」地一下换掉——
        // 那样看着像界面卡了一下。这里只做一次很快的淡入，够表达「内容换了」。
        if (Group(from) == Group(to))
        {
            ApplySectionInstant(to);
            FadeIn(incoming);
            return;
        }

        var incomingOffset = OffsetFor(to);
        var outgoingOffset = OffsetFor(from);
        const double slideInFrom = 22;
        const double slideOutTo = -14;

        outgoing.IsHitTestVisible = false;
        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        incoming.IsHitTestVisible = false;
        incomingOffset.X = slideInFrom;
        outgoingOffset.X = 0;

        // 位置和透明度用不同的缓动：位移要「快起慢收」才显得跟手，
        // 透明度线性收尾更稳。两边都挂 CubicEase 会让淡出拖到最后才动，
        // 观感上就是「先愣一下再切」。
        var slideEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(SectionAnimDuration);

        var board = new Storyboard();
        board.Children.Add(CreateAnimation(outgoing, "Opacity", outgoing.Opacity, 0, duration, fadeEase));
        board.Children.Add(CreateAnimation(outgoingOffset, "X", 0, slideOutTo, duration, slideEase));
        board.Children.Add(CreateAnimation(incoming, "Opacity", 0, 1, duration, slideEase));
        board.Children.Add(CreateAnimation(incomingOffset, "X", slideInFrom, 0, duration, slideEase));

        var generation = ++_sectionAnimGeneration;
        board.Completed += (_, _) =>
        {
            if (generation != _sectionAnimGeneration) return;
            ApplySectionInstant(to);
            _sectionStoryboard = null;
        };

        _sectionStoryboard = board;
        board.Begin();
    }

    /// <summary>同组内换内容用的一次快速淡入。只动透明度，不动位移，最省事也最不晃眼。</summary>
    private static void FadeIn(UIElement pane)
    {
        pane.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>「游戏版本」和「社区资源」是两拨人，整合包单独一拨——滑动画是按拨来的。</summary>
    private static int Group(Section section) =>
        section == Section.Modpack ? 2 : section == Section.Minecraft ? 0 : 1;

    private void StopSectionAnimation()
    {
        _sectionAnimGeneration++;
        if (_sectionStoryboard is null) return;
        try { _sectionStoryboard.Stop(); } catch { /* 停不掉也无所谓，下一轮会盖掉 */ }
        _sectionStoryboard = null;
    }

    private static DoubleAnimation CreateAnimation(
        DependencyObject target, string path, double from, double to, Duration duration, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(path));
        return animation;
    }

    // ── 分区一：游戏版本（原版与加载器）──────────────────────

    private async Task ReloadRemoteAsync()
    {
        VersionMarquee.Visibility = Visibility.Visible;
        GameList.Opacity = 0;

        await RunAsync(async ct =>
        {
            _remote = await S.Install.GetRemoteVersionsAsync(ct);
            RefreshTypeFilters();
            ApplyTypeFilter();
            FillPackVersions();
            FillModGameVersions();
            ShellStatus.Clear();
            S.Log.Info($"游戏版本清单已更新，共 {_remote.Count} 个版本");
        }, "读取游戏版本清单");

        VersionMarquee.Visibility = Visibility.Collapsed;
        GameList.Opacity = 1;
    }

    private void FillPackVersions()
    {
        var versions = new List<string> { AnyVersion };
        versions.AddRange(_remote.Where(v => v.Type == "release").Take(40).Select(v => v.Name));

        var previous = PackVersionCombo.SelectedItem as string;
        PackVersionCombo.ItemsSource = versions;
        PackVersionCombo.SelectedItem = versions.Contains(previous ?? "") ? previous : AnyVersion;
    }

    private string CurrentType =>
        (_typeFilters[TypeCombo.SelectedIndex < 0 ? 0 : TypeCombo.SelectedIndex]).Id;

    /// <summary>
    /// 按清单里实际出现过的版本类型重建「版本类型」下拉。
    /// 已知的五种按官方顺序排，认不出的新类型按原名排在后面——宁可露出一个没翻译的类型，
    /// 也不要让某一批版本整个筛不到。
    /// </summary>
    /// <summary>
    /// Mojang 清单里会出现一些「不是用户能装的版本」的类型，
    /// 比如 <c>local</c>（本机导入的实验版本）。把它们列进下拉只会添乱，
    /// 而且那一类一个都搜不到 —— 直接在这里挡掉，别让下拉长出奇怪的一行。
    /// </summary>
    private static readonly HashSet<string> HiddenVersionTypes = new(StringComparer.Ordinal)
    {
        "local",
    };

    private void RefreshTypeFilters(bool assumeAllKnown = false)
    {
        var present = _remote.Select(v => v.Type)
            .Where(t => !string.IsNullOrEmpty(t) && !HiddenVersionTypes.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

        var next = new List<FilterChoice> { new("", "全部") };

        foreach (var (id, label) in KnownTypes)
        {
            if (assumeAllKnown || present.Remove(id))
                next.Add(new(id, label));
        }

        foreach (var extra in present.OrderBy(x => x, StringComparer.Ordinal))
            next.Add(new(extra, extra));

        var previous = (TypeCombo.SelectedItem as FilterChoice)?.Id ?? "";

        _typeFilters.Clear();
        _typeFilters.AddRange(next);
        TypeCombo.ItemsSource = next.ToList();

        // 尽量保住当前选中的那一项；它要是从清单里消失了（理论上不会）就回「全部」
        TypeCombo.SelectedItem = next.FirstOrDefault(f => f.Id == previous) ?? next[0];
    }

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyTypeFilter();
    }

    private void VersionFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _versionKeyword = VersionFilter.Text?.Trim() ?? "";
        ApplyTypeFilter();
    }

    private void ApplyTypeFilter()
    {
        var type = CurrentType;
        var keyword = _versionKeyword;

        var list = _remote
            .Where(v => string.IsNullOrEmpty(type) || v.Type == type)
            .Where(v => keyword.Length == 0
                        || v.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        GameList.ItemsSource = list;

        if (list.Count > 0)
        {
            GameList.SelectedIndex = 0;
            VersionEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            VersionEmpty.Visibility = Visibility.Visible;
            VersionEmpty.Text = _remote.Count == 0
                ? "还没有读取到版本清单。点右边的「刷新清单」再试一次。"
                : "没有符合条件的版本。换个筛选条件或清空搜索框试试。";
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadRemoteAsync();

    /// <summary>点一条版本：直接弹出安装对话框，选加载器。</summary>
    private async void GameList_Click(object sender, MouseButtonEventArgs e)
    {
        if (GameList.SelectedItem is not RemoteVersion version) return;
        await ShowInstallDialogAsync(version);
    }

    private async Task ShowInstallDialogAsync(RemoteVersion version)
    {
        var dialog = new InstallVersionDialog(version, S)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true) return;
        if (dialog.Request is not { } request) return;

        var settings = S.Config.Current;

        await RunAsync(async ct =>
        {
            var reporter = Reporter();

            if (request.Kind == LoaderKind.Vanilla)
            {
                await S.Install.InstallAsync(request.GameVersion, reporter, ct);
                Ui.Info($"原版 {request.GameVersion} 装好了，回主页就能启动。");
            }
            else
            {
                var label = LoaderLabels.Of(request.Kind);
                var installed = await S.Install.InstallLoaderAsync(
                    request.Kind, request.GameVersion, request.LoaderVersion, reporter, ct);

                settings.LastVersion = installed;
                S.Config.Save();
                Ui.Info($"{label} 装好了，版本名 {installed}。\n\n可以回「主页」直接启动了。");
            }

            ShellStatus.Clear();
        }, "安装版本");
    }

    // ── 分区二：社区资源（模组 / 资源包 / 数据包 / 光影）──────

    /// <summary>
    /// 切到某个资源分区时把准备工作做好，并**立刻搜一屏**。
    ///
    /// 这里必须每次切换都重搜：模组 / 资源包 / 数据包 / 光影 共用同一套列表控件，
    /// 换了分区却不重新请求，屏幕上留着的还是上一个分区搜出来的东西 ——
    /// 用户看到的就是「点了没反应，得手动按一下重置才刷新」。
    /// </summary>
    private void EnsureCatalogReady(Section section)
    {
        var switched = _catalogSection != section;
        _catalogSection = section;

        // 分类下拉是按类型重建的，每次换分区都要重来一遍——
        // 模组的「科技」在资源包那边根本不存在
        if (switched || !_catalogInitDone)
        {
            RefreshTagFilters();

            // 换分区等于换了搜索对象，上一批结果和分页位置一起作废
            _hits.Clear();
            ModList.ItemsSource = _hits.ToList();
            _offset = 0;
            _total = -1;
            ModPager.Visibility = Visibility.Collapsed;
        }

        if (!_catalogInitDone)
        {
            _catalogInitDone = true;
            FillModTargets();
            FillModGameVersions();
        }
        else if (_modTarget is not null)
        {
            // 回到这个分区时，目标实例里已装的文件可能变了（比如刚在别处删过）
            _installedFiles = ReadInstalledFiles(_modTarget, CatalogKind);
        }

        // 只有真的换了分区才自动搜——首次进入也要搜，所以统一放在这里。
        // 没选目标实例时不搜，交给 ApplyModTarget 的提示语去引导。
        if (switched || _hits.Count == 0) _ = RunSearchAsync(reset: true);
    }

    private bool _catalogInitDone;

    private CommunityResourceType CatalogType => _catalogSection switch
    {
        Section.ResourcePack => CommunityResourceType.ResourcePack,
        Section.DataPack => CommunityResourceType.DataPack,
        Section.ShaderPack => CommunityResourceType.Shader,
        _ => CommunityResourceType.Mod
    };

    private ResourceKind CatalogKind => _catalogSection switch
    {
        Section.ResourcePack => ResourceKind.ResourcePacks,
        Section.DataPack => ResourceKind.DataPacks,
        Section.ShaderPack => ResourceKind.ShaderPacks,
        _ => ResourceKind.Mods
    };

    /// <summary>
    /// 这个分区的内容是不是与游戏版本无关。
    /// 光影包是纯粹的着色器程序，一份就够所有版本用，Modrinth 上的版本标记也不全，
    /// 拿「目标实例的版本」去筛等于自断大半货源 —— 所以光影不按版本筛，
    /// 也不要求先选实例，装的时候再问用户放哪个版本目录。
    /// </summary>
    private bool IsVersionAgnostic => _catalogSection == Section.ShaderPack;

    /// <summary>
    /// 刷新「安装到」下拉：列出本机已安装的版本。
    /// 每次切到资源分区都会调——在「游戏版本」分区装完新版本回来，这里要立刻能看到。
    /// 已经选中过一个版本时就保住它（只要它还没被删），不然每切一次分区用户都被弹回默认。
    /// </summary>
    private void FillModTargets()
    {
        var previous = _modTarget?.Name ?? S.Config.Current.LastVersion;

        _modUiReady = false;
        var entries = Installed().Select(v => new ModTargetEntry(v)).ToList();
        ModTargetCombo.ItemsSource = entries;

        var target = entries.FirstOrDefault(e => e.Version.Name == previous)
                     ?? entries.FirstOrDefault();
        ModTargetCombo.SelectedItem = target;
        _modUiReady = true;

        if (target is null)
        {
            _modTarget = null;
            ShowCatalogMessage("还没有安装任何版本。\n先去「游戏版本」分区装一个，再回来装东西。");
            return;
        }

        ShowCatalogMessage("在上面的搜索框输入名字，或者直接敲回车按热度列出。");

        // 选中的版本没变就什么都不做：重新读一遍目录、重载详情都是白工
        if (_modTarget?.Name == target.Version.Name) return;
        ApplyModTarget(target.Version);
    }

    /// <summary>游戏版本筛选用远程清单填充；目标版本的基础版本也要在列表里，它可能是快照。</summary>
    private void FillModGameVersions()
    {
        var versions = new List<string> { AnyVersion };
        versions.AddRange(_remote.Where(v => v.Type == "release").Take(40).Select(v => v.Name));

        if (_modTarget is { BaseVersion: { Length: > 0 } baseVersion } && !versions.Contains(baseVersion))
            versions.Add(baseVersion);

        var previous = ModGameVersionCombo.SelectedItem as string;
        _modUiReady = false;
        ModGameVersionCombo.ItemsSource = versions;
        ModGameVersionCombo.SelectedItem = versions.Contains(previous ?? "") ? previous : AnyVersion;
        _modUiReady = true;
    }

    private void ModTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_modUiReady) return;
        if (ModTargetCombo.SelectedItem is not ModTargetEntry entry) return;
        ApplyModTarget(entry.Version);
    }

    /// <summary>切换目标版本：重设筛选、刷新已装文件清单。</summary>
    private void ApplyModTarget(InstalledVersion version)
    {
        _modTarget = version;

        FillModGameVersions();
        _hits.Clear();
        ModList.ItemsSource = _hits.ToList();
        ModPager.Visibility = Visibility.Collapsed;

        // 记下目标实例里已有的文件，界面上好标「重新安装」。不靠文件名猜项目，
        // 只做同名判断，够用也不会误报。目录读不出来不能炸：空集合照样能用
        _installedFiles = ReadInstalledFiles(version, CatalogKind);

        ShowCatalogMessage(version.Loader == LoaderKind.Vanilla
            ? "原版版本不支持模组。\n先去「游戏版本」分区装一个 Forge / NeoForge / Fabric / Quilt 版本。"
            : "在上面的搜索框输入名字，或者直接敲回车按热度列出。");
    }

    private void ModFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_modUiReady) return;
        if (_hits.Count > 0) _ = RunSearchAsync(reset: true);
    }

    /// <summary>
    /// 按当前资源类型重建「分类」下拉项。切换分区时必须调——
    /// 模组的分类（科技 / 魔法）在资源包那边一个都没有，留着会搜出空结果。
    /// 数据包在 Modrinth 上压根没有分类，这时整个下拉收起来，免得给个永远选不出东西的控件。
    /// </summary>
    private void RefreshTagFilters()
    {
        var previous = (ModTagCombo.SelectedItem as TagFilter)?.Id;
        var options = new List<TagFilter> { new(null, "全部") };

        if (TagsByType.TryGetValue(CatalogType, out var tags))
            options.AddRange(tags.Select(t => new TagFilter(t.Id, t.Label)));

        // 重建过程中 SelectedIndex 的变动不该触发重搜——会让分区切换白白多搜一次
        var ready = _modUiReady;
        _modUiReady = false;

        ModTagCombo.ItemsSource = options;
        ModTagCombo.SelectedItem = options.FirstOrDefault(o => o.Id == previous) ?? options[0];

        // 只有「全部」一项 = 这个类型没有分类可筛，整块（标签 + 下拉）一起收
        var hasTags = options.Count > 1;
        ModTagCombo.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
        ModTagLabel.Visibility = ModTagCombo.Visibility;
        ModTagHost.Visibility = ModTagCombo.Visibility;

        // 光影是全版本通用的，不需要先选实例、也不按版本 / 加载器筛，
        // 这三栏一起收起来，界面更干净（装的时候才问放哪个版本）
        var showTarget = !IsVersionAgnostic;
        ModTargetHost.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
        ModVersionHost.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
        ModLoaderHost.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;

        _modUiReady = ready;
    }

    private string? CurrentTagFilter =>
        (ModTagCombo.SelectedItem as TagFilter)?.Id;

    /// <summary>
    /// 整合包那格有自己的「分类」下拉（它不在 Catalog 面板里），
    /// 所以单独铺一次——项目固定是 ModPack 那一套。
    /// </summary>
    private void RefreshPackTagFilters()
    {
        var options = new List<TagFilter> { new(null, "全部") };
        if (TagsByType.TryGetValue(CommunityResourceType.ModPack, out var tags))
            options.AddRange(tags.Select(t => new TagFilter(t.Id, t.Label)));

        var ready = _modUiReady;
        _modUiReady = false;

        PackTagCombo.ItemsSource = options;
        PackTagCombo.SelectedIndex = 0;

        _modUiReady = ready;
    }

    private string? CurrentPackTagFilter =>
        (PackTagCombo.SelectedItem as TagFilter)?.Id;

    /// <summary>目标版本的加载器——模组分加载器发布，装错加载器游戏根本起不来。</summary>
    private string TargetLoader => _modTarget is { } target ? LoaderApiName(target.Loader) : "";

    private static string LoaderApiName(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        _ => ""
    };

    private string? CurrentGameVersion
    {
        get
        {
            var value = ModGameVersionCombo.SelectedItem as string;
            return value == AnyVersion ? null : value;
        }
    }

    private string? CurrentLoaderFilter =>
        (ModLoaderCombo.SelectedItem as LoaderFilter)?.Id;

    private HashSet<string> _installedFiles = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> ReadInstalledFiles(InstalledVersion version, ResourceKind kind)
    {
        try
        {
            var dir = S.Resources.DirectoryFor(version.Name, version.Isolated, kind);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*")
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            S.Log.Warn($"读取 {version.Name} 的资源目录失败：{ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ModQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = RunSearchAsync(reset: true);
    }

    private void ModSearchButton_Click(object sender, RoutedEventArgs e) =>
        _ = RunSearchAsync(reset: true);

    private void ModResetButton_Click(object sender, RoutedEventArgs e)
    {
        ModQuery.Text = "";
        _modUiReady = false;
        ModLoaderCombo.SelectedIndex = 0;
        ModGameVersionCombo.SelectedItem = AnyVersion;
        if (ModTagCombo.Items.Count > 0) ModTagCombo.SelectedIndex = 0;
        _modUiReady = true;
        _ = RunSearchAsync(reset: true);
    }

    /// <summary>
    /// 搜一屏。<paramref name="reset"/> 为 true 时从第一页开始，否则接着下一页。
    /// 有目标版本才搜——没有实例的话搜出来也没地方装。
    /// </summary>
    private async Task RunSearchAsync(bool reset)
    {
        // 光影不依赖某个实例，没有目标版本也能搜
        if (_modTarget is not { } target && !IsVersionAgnostic)
        {
            ShowCatalogMessage("先在左上角选一个要装进去的实例。");
            return;
        }

        if (CatalogType == CommunityResourceType.Mod
            && _modTarget is { Loader: LoaderKind.Vanilla })
        {
            ShowCatalogMessage("原版版本不支持模组。\n先去「游戏版本」分区装一个带加载器的版本。");
            return;
        }

        if (reset) _offset = 0;

        // 模组默认按目标实例的加载器筛，免得列出一堆装不上去的版本；
        // 用户手动选了加载器就以用户为准
        var loader = CurrentLoaderFilter ?? TargetLoader;

        // 光影 / 资源包 / 数据包都不分加载器。这里必须显式清掉，
        // 否则从「模组」分区切过来时会把上一个分区的加载器（比如 forge）带过去，
        // 光影那边一个结果都搜不到 —— 看起来就像「这个分区坏了」
        if (IsVersionAgnostic) loader = null;

        ModMarquee.Visibility = Visibility.Visible;
        ModList.Opacity = 0;

        await RunAsync(async ct =>
        {
            var request = new ResourceSearchRequest
            {
                Type = CatalogType,
                SearchText = string.IsNullOrWhiteSpace(ModQuery.Text) ? null : ModQuery.Text.Trim(),
                // 光影与游戏版本无关，一份光影包对所有版本都能用，
                // 所以这一栏不参与筛选——按版本筛只会把大量可用的光影挡在外面
                GameVersion = IsVersionAgnostic ? null : CurrentGameVersion,
                Loader = string.IsNullOrEmpty(loader) ? null : loader,
                Tag = CurrentTagFilter,
                PageSize = PageSize,
                Offset = _offset,
            };

            var result = new ResourceSearchResult();
            await S.Community.SearchAsync(request, result, ct);

            _total = result.Total;

            _hits.Clear();
            _hits.AddRange(result.Results.Select(h => new ModHitRow(h)));
            ModList.ItemsSource = _hits.ToList();
            Animator.AaItems(ModList);

            if (_hits.Count == 0)
            {
                // 一条都没有：列表区显示引导文案，条数那行收掉
                ShowCatalogMessage("这个条件下什么都没搜到。换个关键字，或者把筛选放宽一点。");
                ModCountText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 有结果：列表区清空引导，条数单独放到翻页下面
                ShowCatalogMessage(null);
                ModCountText.Text = $"共 {_hits.Count} 条";
                ModCountText.Visibility = Visibility.Visible;
            }

            UpdatePager();
            ShellStatus.Clear();
        }, "搜索资源");

        ModMarquee.Visibility = Visibility.Collapsed;
        ModList.Opacity = 1;

        _ = LoadIconsAsync();
    }

    private void UpdatePager()
    {
        var canPrev = _offset > 0;
        var canNext = _total < 0 || _offset + PageSize < _total;

        ModPager.Visibility = canPrev || canNext ? Visibility.Visible : Visibility.Collapsed;
        ModPageLabel.Text = _total >= 0
            ? $"第 {_offset / PageSize + 1} 页 / 共 {Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize))} 页"
            : $"第 {_offset / PageSize + 1} 页";

        foreach (var button in ModPager.Children.OfType<Button>())
            button.IsEnabled = button.Content as string == "上一页" ? canPrev : canNext;
    }

    private void ModPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_offset == 0) return;
        _offset = Math.Max(0, _offset - PageSize);
        _ = RunSearchAsync(reset: false);
    }

    private void ModNextPage_Click(object sender, RoutedEventArgs e)
    {
        _offset += PageSize;
        _ = RunSearchAsync(reset: false);
    }

    /// <summary>
    /// 在列表区放一句引导 / 提示。
    /// 传 <c>null</c> 表示「有结果了，把引导收起来」，别让这句话压在结果上。
    /// </summary>
    private void ShowCatalogMessage(string? text)
    {
        ModEmpty.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        ModEmpty.Text = text ?? "";
        ModStatusInline.Text = "";
    }

    private async Task LoadIconsAsync()
    {
        var rows = _hits.Where(r => r.Icon is null && !string.IsNullOrWhiteSpace(r.IconUrl)).ToList();
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            var icon = await IconLoader.LoadAsync(row.IconUrl);
            if (icon is not null) row.Icon = icon;
        }
    }

    // ── 社区资源：详情 ──────────────────────────────────────

    private void ModList_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModList.SelectedItem is not ModHitRow row) return;
        _ = OpenModDetailAsync(row.Project);
    }

    private async Task OpenModDetailAsync(ResourceProject project)
    {
        _selectedMod = project;
        _chosenVersion = null;

        ShowCatalogBrowse(false);

        ModTitle.Text = project.Title;
        ModAuthor.Text = project.AuthorText;
        ModMeta.Text = project.DownloadsText
            + (project.LatestGameVersion.Length > 0 ? $"　·　支持到 {project.LatestGameVersion}" : "");
        ModDescription.Text = string.IsNullOrWhiteSpace(project.Description)
            ? "这个没有写简介。"
            : project.Description;
        ModVersionList.ItemsSource = null;
        ModVersionHint.Text = "正在读取可用版本…";
        ModChangelog.Text = "选中一个版本后，这里显示它的更新日志。";
        BtnInstallMod.IsEnabled = false;
        ModNote.Text = "";

        // 光影与版本无关：不按目标实例的版本 / 加载器筛，把所有版本都列出来。
        // 按实例筛会把绝大多数光影直接筛没（详情页一句「没有支持的版本」、安装键灰着），
        // 而实际上一份光影对所有版本都能用。
        var query = VersionQueryFor();

        await RunAsync(async ct =>
        {
            _modVersions = await S.Community.GetVersionsAsync(
                project.Id, query.GameVersion, query.Loader, ct);

            ModVersionList.ItemsSource = _modVersions.Select(v => new ModVersionEntry(v)).ToList();

            if (_modVersions.Count == 0)
            {
                ModVersionHint.Text = IsVersionAgnostic
                    ? $"{project.Title} 没有可下载的文件。"
                    : $"{project.Title} 没有支持 {query.GameVersion} / {query.LoaderLabel} 的版本。";
                return;
            }

            ModVersionList.SelectedIndex = 0;
            ModVersionHint.Text = IsVersionAgnostic
                ? $"共 {_modVersions.Count} 个版本。光影与游戏版本无关，装的时候再选放进哪个版本。"
                : $"共 {_modVersions.Count} 个可用版本，默认选最新。";
        }, "读取版本列表");
    }

    /// <summary>
    /// 详情页读版本列表时用哪套筛选条件。
    /// 光影这类与版本无关的资源返回全空（= 不筛），其余按目标实例的版本 + 加载器。
    /// 抽出来是为了让自检能直接断言这里，而不是去猜详情页的行为。
    /// </summary>
    private (string? GameVersion, string? Loader, string LoaderLabel) VersionQueryFor()
    {
        if (IsVersionAgnostic) return (null, null, "");

        if (_modTarget is not { } target) return (null, null, "");
        return (target.BaseVersion, TargetLoader,
                target.Loader == LoaderKind.Vanilla ? "原版" : target.LoaderLabel);
    }

    /// <summary>列表 ↔ 详情。两个面板一直在树上，只换可见性，切换才不会有白屏。</summary>
    private void ShowCatalogBrowse(bool browse)
    {
        CatalogBrowse.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        CatalogBrowse.IsHitTestVisible = browse;

        CatalogDetail.Opacity = browse ? 0 : 1;
        CatalogDetail.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        CatalogDetail.IsHitTestVisible = !browse;
    }

    private void ModDetailBack_Click(object sender, RoutedEventArgs e) => ShowCatalogBrowse(true);

    private void ModVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        _chosenVersion = (ModVersionList.SelectedItem as ModVersionEntry)?.Version;

        if (_chosenVersion is not { } version)
        {
            ModChangelog.Text = "选中一个版本后，这里显示它的更新日志。";
            BtnInstallMod.IsEnabled = false;
            return;
        }

        ModChangelog.Text = string.IsNullOrWhiteSpace(version.Changelog)
            ? "这个版本没有写更新日志。"
            : TrimChangelog(version.Changelog);

        // 光影没有「目标实例」这个概念，装到哪要等点了「安装」才问，
        // 这里就别拿某个实例的目录去判断「已经装过了」
        if (IsVersionAgnostic)
        {
            BtnInstallMod.Content = "安装";
            BtnInstallMod.IsEnabled = true;
            ModNote.Text = $"文件名 {version.FileName}，约 {version.Size / 1024.0:0.#} KB。"
                         + "点「安装」后再选放进哪个版本。";
            return;
        }

        var installed = _installedFiles.Contains(version.FileName);
        BtnInstallMod.Content = installed ? "重新安装" : "安装";
        BtnInstallMod.IsEnabled = true;
        ModNote.Text = installed
            ? $"目标实例里已经有 {version.FileName} 了，再装一遍会覆盖它。"
            : $"文件名 {version.FileName}，约 {version.Size / 1024.0:0.#} KB。";
    }

    /// <summary>更新日志有人能写好几千字，缩略一下，全文去 Modrinth 页面看。</summary>
    private static string TrimChangelog(string text)
    {
        var flat = text.Replace("\r\n", "\n").Trim();
        return flat.Length <= 800 ? flat : flat[..800] + "\n……（太长，截断了）";
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMod is not { } hit) return;
        if (_chosenVersion is not { } version) return;

        // 光影这类与版本无关的资源：不要求先选实例，装的时候再问用户放哪个版本
        string directory;
        string friendlyTarget;

        if (IsVersionAgnostic)
        {
            var dialog = new PickTargetDialog(
                S, CatalogKind, version.FileName, version.Size, hit.Title)
            { Owner = Window.GetWindow(this) };

            if (dialog.ShowDialog() != true || dialog.TargetDirectory is not { } picked) return;

            directory = picked;
            friendlyTarget = dialog.TargetVersionName ?? Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar));
        }
        else
        {
            if (_modTarget is not { } target) return;
            directory = S.Resources.EnsureDirectory(target.Name, target.Isolated, CatalogKind);
            friendlyTarget = target.Name;
        }

        await RunAsync(async ct =>
        {
            ModNote.Text = $"正在下载 {version.FileName}…";
            var path = await S.Community.InstallAsync(
                directory, version, new Progress<double>(ShellStatus.SetRatio), ct);

            ModNote.Text = $"装好了：{Path.GetFileName(path)}";
            ShellStatus.Clear();
            Ui.Info($"「{hit.Title} {version.VersionNumber}」已安装到 {friendlyTarget}。\n\n回主页就能用。");
        }, "安装");
    }

    private void OpenModPage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMod is not { } hit) return;
        Ui.OpenUrl(hit.WebsiteUrl);
    }

    // ── 模组更新检查 ────────────────────────────────────────

    /// <summary>最近一次检查出来的可更新项。一键更新时直接照着这个跑。</summary>
    private List<ResourceUpdate> _updates = new();

    /// <summary>
    /// 问 Modrinth「我装的这些模组有没有新版本」。
    /// 走文件哈希比对，不靠文件名猜项目——改了名的 jar 也能认出来。
    /// 只对模组分区开放：资源包 / 光影的更新语义不一样，先不混进来。
    /// </summary>
    private async void CheckModUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_modTarget is not { } target)
        {
            ShowCatalogMessage("先在左上角选一个要检查的实例。");
            return;
        }

        await RunAsync(async ct =>
        {
            var dir = S.Resources.DirectoryFor(target.Name, target.Isolated, ResourceKind.Mods);
            if (!Directory.Exists(dir))
            {
                HideModUpdates();
                ShowCatalogMessage($"{target.Name} 还没有 mods 目录，装几个模组再回来看。");
                return;
            }

            var files = Directory.GetFiles(dir, "*.jar");
            if (files.Length == 0)
            {
                HideModUpdates();
                ShowCatalogMessage($"{target.Name} 的 mods 目录里没有 jar 文件。");
                return;
            }

            ModMarquee.Visibility = Visibility.Visible;
            ShellStatus.SetText($"正在比对 {files.Length} 个模组…");

            _updates = (await S.Community.CheckUpdatesAsync(
                files, target.BaseVersion, TargetLoader, ct)).ToList();

            ModMarquee.Visibility = Visibility.Collapsed;
            ShellStatus.Clear();

            ShowModUpdates(target);
        }, "检查模组更新");
    }

    private void ShowModUpdates(InstalledVersion target)
    {
        if (_updates.Count == 0)
        {
            HideModUpdates();
            ShowCatalogMessage($"检查完了：{target.Name} 里 {Directory.GetFiles(
                S.Resources.DirectoryFor(target.Name, target.Isolated, ResourceKind.Mods), "*.jar").Length
                } 个模组都是最新的。");
            return;
        }

        ModUpdateTitle.Text = $"发现 {_updates.Count} 个模组有新版本：";
        ModUpdateList.ItemsSource = _updates
            .Select(u => new ModUpdateRow(u.InstalledName, u.CurrentVersion, u.NewVersion.VersionNumber))
            .ToList();

        ModUpdatePanel.Visibility = Visibility.Visible;
        BtnUpdateAllMods.IsEnabled = true;
        ModEmpty.Visibility = Visibility.Collapsed;
    }

    private void HideModUpdates()
    {
        ModUpdatePanel.Visibility = Visibility.Collapsed;
        ModUpdateList.ItemsSource = null;
        _updates = new List<ResourceUpdate>();
    }

    private void CloseModUpdates_Click(object sender, RoutedEventArgs e) => HideModUpdates();

    /// <summary>
    /// 把检查出来的新版本逐个下下来，覆盖掉旧的那个文件。
    /// 下载完就删旧的——Modrinth 那边换了版本往往连文件名都变了，
    /// 不删的话 mods 目录里会同时躺着新旧两份，游戏直接起不来。
    /// </summary>
    private async void UpdateAllMods_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Count == 0 || _modTarget is not { } target) return;

        var pending = _updates.ToList();
        BtnUpdateAllMods.IsEnabled = false;

        await RunAsync(async ct =>
        {
            var dir = S.Resources.EnsureDirectory(target.Name, target.Isolated, ResourceKind.Mods);
            var done = 0;
            var failed = new List<string>();

            foreach (var update in pending)
            {
                ct.ThrowIfCancellationRequested();

                ModUpdateTitle.Text = $"正在更新 {done + 1}/{pending.Count}：{update.InstalledName}";

                try
                {
                    var path = await S.Community.InstallAsync(
                        dir, update.NewVersion,
                        new Progress<double>(r => ShellStatus.SetRatio((done + r) / pending.Count)), ct);

                    // 新旧文件名不一样时才会多出一个文件，同名就是原地覆盖
                    if (!string.Equals(path, update.InstalledPath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(update.InstalledPath))
                    {
                        File.Delete(update.InstalledPath);
                    }

                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add($"{update.InstalledName}：{ex.Message}");
                    S.Log.Warn($"更新 {update.InstalledName} 失败：{ex.Message}");
                }
            }

            ShellStatus.Clear();
            HideModUpdates();

            if (failed.Count == 0)
            {
                Ui.Info($"已更新 {done} 个模组。\n\n回主页启动试试。");
            }
            else
            {
                Ui.Warn($"更新完成：成功 {done} 个，失败 {failed.Count} 个。\n\n"
                    + string.Join("\n", failed.Take(6)));
            }
        }, "更新模组");
    }

    // ── 分区三：整合包 ──────────────────────────────────────

    private void EnsureModpackReady()
    {
        if (_modpackInitDone) return;
        _modpackInitDone = true;

        ShowPackMessage("点右边的「搜索」或者直接敲回车开始搜整合包。");
    }

    private bool _modpackInitDone;

    private void PackQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = SearchPacksAsync();
    }

    private void PackFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_modUiReady) return;
        if (_packs.Count > 0) _ = SearchPacksAsync();
    }

    private async Task SearchPacksAsync()
    {
        await RunAsync(async ct =>
        {
            var query = PackQuery.Text.Trim();
            var version = PackVersionCombo.SelectedItem as string;
            if (version == AnyVersion) version = null;
            var loader = (PackLoaderCombo.SelectedItem as LoaderFilter)?.Id;

            var request = new ResourceSearchRequest
            {
                Type = CommunityResourceType.ModPack,
                SearchText = query,
                GameVersion = version,
                Loader = loader,
                Tag = CurrentPackTagFilter,
                PageSize = PageSize,
            };

            var result = new ResourceSearchResult();
            await S.Community.SearchAsync(request, result, ct);

            _packs = result.Results.Select(h => new PackRow(h)).ToList();
            PackList.ItemsSource = _packs;
            Animator.AaItems(PackList);

            ShowPackMessage(_packs.Count == 0
                ? (string.IsNullOrEmpty(query)
                    ? "这个条件下没搜到整合包，换个筛选试试。"
                    : $"没搜到跟「{query}」相关的整合包。")
                : $"共 {_packs.Count} 个整合包。");

            ShellStatus.Clear();
        }, "搜索整合包");
    }

    private void ShowPackMessage(string text)
    {
        PackEmpty.Visibility = Visibility.Visible;
        PackEmpty.Text = text;
    }

    private void Pack_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PackList.SelectedItem is not PackRow row)
        {
            _selectedPack = null;
            BtnInstallPack.IsEnabled = false;
            return;
        }

        _selectedPack = row.Project;

        PackTitle.Text = row.Project.Title;
        PackAuthor.Text = row.Project.AuthorText;
        PackMeta.Text = row.Project.DownloadsText
            + (row.Project.LatestGameVersion.Length > 0
                ? $"　·　支持到 {row.Project.LatestGameVersion}"
                : "");
        PackDescription.Text = string.IsNullOrWhiteSpace(row.Project.Description)
            ? "这个整合包没有写简介。"
            : row.Project.Description;

        BtnInstallPack.IsEnabled = true;
    }

    private async void InstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPack is not { } pack) return;

        await RunAsync(async ct =>
        {
            var reporter = Reporter();
            reporter.Status($"正在下载整合包「{pack.Title}」…");

            var url = await S.ModPacks.GetLatestDownloadAsync(pack.Id, ct)
                      ?? throw new InvalidOperationException("这个整合包没有可用的下载文件");

            var file = await S.ModPacks.DownloadAsync(url, null, ct);
            try
            {
                await InstallModPackAsync(file, ct);
            }
            finally
            {
                // 缓存文件用完就删，留着只会占地方
                try { File.Delete(file); } catch { /* 删不掉也无所谓 */ }
            }

            PackNote.Text = $"「{pack.Title}」安装完成，已经建好同名版本。";
            ShellStatus.Clear();
            Ui.Info($"整合包「{pack.Title}」安装完成。\n\n可以在「主页」或「版本与资源」里直接启动。");
        }, "安装整合包", BtnInstallPack);
    }

    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickFiles("Modrinth 整合包 (*.mrpack)|*.mrpack");
        if (files.Length == 0) return;

        await RunAsync(async ct =>
        {
            foreach (var file in files)
                await InstallModPackAsync(file, ct);

            ShowPackMessage("本地整合包导入完成。");
            ShellStatus.Clear();
            Ui.Info("本地整合包导入完成。");
        }, "导入整合包");
    }

    private void OpenPackPage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPack is not { } pack)
        {
            Ui.Warn("先选一个整合包。");
            return;
        }

        Ui.OpenUrl(pack.WebsiteUrl);
    }

    // ── 自检探针 ────────────────────────────────────────────
    //
    // 「切到资源分区」这条路和账号窗口的页签当年一样，是靠 SelectionChanged 驱动的。
    // 构造没抛异常不等于切过去有内容——结果面板全收着也照样不抛。
    // 所以这里把「切分区 + 汇报可见性」做成探针，让 SelfCheck 断言。

    /// <summary>切到资源分区，返回关键元素的可见性与文本。给 SelfCheck 用。</summary>
    internal string CatalogProbe()
    {
        _sectionUiReady = true;
        SectionList.SelectedItem = SectionList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(i => i.Tag as string == "mod");

        return $"{nameof(PaneCatalog)}={PaneCatalog.Visibility} "
             + $"结果列表={ModList.Visibility} 空提示={ModEmpty.Visibility} "
             + $"详情标题='{ModTitle.Text}' 目标实例={_modTarget?.Name ?? "(无)"}";
    }

    /// <summary>
    /// 把六个分区逐个点一遍，回报每个分区有没有真的把面板切出来。
    /// 分区切换靠 SelectionChanged 驱动，历史上出过「点了没反应」——
    /// 就是 _sectionUiReady 一直没被置上、事件全被前置守卫挡掉。给 SelfCheck 断言用。
    /// </summary>
    internal string SectionNavProbe()
    {
        var parts = new List<string>();

        var items = new (ListBox List, string Tag)[]
        {
            (SectionList, "minecraft"),
            (SectionList, "mod"),
            (SectionList, "resourcepack"),
            (SectionList, "datapack"),
            (SectionList, "shaderpack"),
            (ModpackSectionList, "modpack"),
        };

        foreach (var (list, tag) in items)
        {
            _sectionUiReady = true;
            list.SelectedItem = list.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Tag as string == tag);

            var expected = tag switch
            {
                "mod" => Section.Mod,
                "resourcepack" => Section.ResourcePack,
                "datapack" => Section.DataPack,
                "shaderpack" => Section.ShaderPack,
                "modpack" => Section.Modpack,
                _ => Section.Minecraft
            };

            // 断言的是「点下去之后状态真的变了」。切换动画靠调度器播放，
            // 探针是同步的，所以顺手把面板推到终态——既不会把「动画还没播完」
            // 误判成没反应，也不会把页面留在半透明的中间态
            var switched = _section == expected && _displayed == expected;
            parts.Add($"{tag}={(switched ? "OK" : "BAD")}");

            StopSectionAnimation();
            ApplySectionInstant(_section);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 检查「分类」下拉是不是真的按当前资源类型铺上了选项，以及更新面板能不能亮起来。
    ///
    /// 为什么要探：TagCombo 的选项是在分区切换时重建的，这中间要临时压低 _modUiReady
    /// 免得触发重搜——写错一步就会变成「下拉永远是空的」或者「一换分区就狂搜」，
    /// 而这两种情况光靠构造不抛异常是看不出来的。
    /// 更新面板同理：它是 Collapsed 起步的，得确认真能给推出来。
    /// </summary>
    internal string FilterProbe()
    {
        var parts = new List<string>();

        // 逐个分区切过去，看分类下拉有没有跟着换一套选项。
        // 数据包那格期望的是「整块收起来」——Modrinth 没给它定义分类，
        // 留着个只有「全部」的下拉等于骗用户去点
        var sections = new (ListBox List, string Tag, string Expect, bool HasTags)[]
        {
            (SectionList, "mod", "性能优化", true),
            (SectionList, "resourcepack", "写实风", true),
            (SectionList, "datapack", "", false),
            (SectionList, "shaderpack", "半写实", true),
        };

        foreach (var (list, tag, expect, hasTags) in sections)
        {
            _sectionUiReady = true;
            list.SelectedItem = list.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Tag as string == tag);

            StopSectionAnimation();
            ApplySectionInstant(_section);

            var labels = ModTagCombo.Items.OfType<TagFilter>().Select(t => t.Label).ToList();
            var defaulted = ModTagCombo.SelectedIndex == 0;

            var ok = hasTags
                ? labels.Count > 1 && labels.Contains(expect) && defaulted
                    && ModTagCombo.Visibility == Visibility.Visible
                : labels.Count == 1 && ModTagCombo.Visibility == Visibility.Collapsed;

            parts.Add($"{tag}={(ok ? "OK" : "BAD")}({labels.Count}项,{ModTagCombo.Visibility}"
                + $"{(defaulted ? "" : ",未复位")}{(hasTags && !labels.Contains(expect) ? $",缺{expect}" : "")})");
        }

        // 光影那一格：与版本无关，所以「安装到 / 游戏版本 / 加载器」三栏都该收起来
        {
            _sectionUiReady = true;
            SectionList.SelectedItem = SectionList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Tag as string == "shaderpack");

            StopSectionAnimation();
            ApplySectionInstant(_section);

            var collapsed =
                ModTargetHost.Visibility == Visibility.Collapsed
                && ModVersionHost.Visibility == Visibility.Collapsed
                && ModLoaderHost.Visibility == Visibility.Collapsed;
            var agnostic = IsVersionAgnostic;

            parts.Add($"shader无版本栏={(collapsed && agnostic ? "OK" : "BAD")}"
                + $"({ModTargetHost.Visibility},{ModVersionHost.Visibility},{ModLoaderHost.Visibility})");

            // 详情页也必须走「不按版本筛」那条路。曾经这里漏了，
            // 结果光影详情页写着「没有支持 xx / Forge 的版本」、安装键是灰的 ——
            // 列表搜得出来、点进去却装不了，光看列表是发现不了的。
            // 断言方式：光影分区的版本查询不应该带任何版本 / 加载器条件。
            var query = VersionQueryFor();
            var clean = string.IsNullOrEmpty(query.GameVersion) && string.IsNullOrEmpty(query.Loader);
            parts.Add($"shader查询={clean}({query.GameVersion ?? "-"}/{query.Loader ?? "-"})");
        }

        // 整合包那格用的是自己的下拉（不在 Catalog 面板里），单独验一遍
        {
            _sectionUiReady = true;
            ModpackSectionList.SelectedItem = ModpackSectionList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Tag as string == "modpack");

            StopSectionAnimation();
            ApplySectionInstant(_section);

            var labels = PackTagCombo.Items.OfType<TagFilter>().Select(t => t.Label).ToList();
            var ok = labels.Count > 1 && labels.Contains("大杂烩") && PackTagCombo.SelectedIndex == 0;
            parts.Add($"modpack={(ok ? "OK" : "BAD")}({labels.Count}项)");
        }

        // 更新面板：默认收着，推出来之后要能看见、且按钮可点
        var hiddenByDefault = ModUpdatePanel.Visibility == Visibility.Collapsed;
        ModUpdateTitle.Text = "探针";
        ModUpdateList.ItemsSource = new List<ModUpdateRow> { new("示例模组", "1.0", "2.0") };
        ModUpdatePanel.Visibility = Visibility.Visible;
        BtnUpdateAllMods.IsEnabled = true;

        var shown = ModUpdatePanel.Visibility == Visibility.Visible && BtnUpdateAllMods.IsEnabled;
        ModUpdateTitle.Text = "示例模组";

        parts.Add($"更新面板={(hiddenByDefault && shown ? "OK" : "BAD")}");

        return string.Join(" ", parts);
    }

    // ── 内部小类型 ──────────────────────────────────────────

    // ComboBox 收起时显示的是 ToString()——DisplayMemberPath 只作用于展开的列表，
    // 所以这几个包装 record 都得自己给出显示文本。
    private sealed record FilterChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record LoaderFilter(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TagFilter(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>「检查更新」结果里的一行：模组名 + 「旧版 → 新版」。</summary>
    private sealed class ModUpdateRow
    {
        public ModUpdateRow(string name, string current, string next)
        {
            Name = name;
            VersionText = string.Equals(current, next, StringComparison.OrdinalIgnoreCase)
                ? "已是最新"
                : $"{current} → {next}";
        }

        public string Name { get; }

        public string VersionText { get; }
    }

    /// <summary>「安装到」下拉的一行。ComboBox 收起时显示的是 ToString()。</summary>
    private sealed record ModTargetEntry(InstalledVersion Version)
    {
        public override string ToString() =>
            $"{Version.Name}（{(Version.Loader == LoaderKind.Vanilla ? "原版" : $"{Version.LoaderLabel} {Version.BaseVersion}")}）";
    }

    /// <summary>版本下拉的一行。Name 是作者起的版本名（mc1.20.1-0.5.13-fabric 这种），直接给人看。</summary>
    private sealed record ModVersionEntry(ResourceVersion Version)
    {
        public override string ToString() => Version.Name;
    }

    /// <summary>
    /// 搜索结果的一行。图标是异步补的，所以实现一下属性通知——
    /// 列表已经画出来了，图标下完再单独刷新那一格。
    /// </summary>
    private sealed class ModHitRow : INotifyPropertyChanged
    {
        private BitmapImage? _icon;

        public ModHitRow(ResourceProject project) => Project = project;

        public ResourceProject Project { get; }

        public string Title => Project.Title;

        public string Description => string.IsNullOrWhiteSpace(Project.Description)
            ? "没有简介。"
            : Project.Description;

        public string VersionsText => Project.GameVersions.Count > 0
            ? $"支持 {Project.GameVersions[0]} ~ {Project.LatestGameVersion}"
            : "";

        public string LoadersText => Project.Loaders.Count > 0
            ? string.Join(" / ", Project.Loaders)
            : "";

        public string SourceText => Project.Platform == ResourcePlatform.CurseForge ? "CurseForge" : "Modrinth";

        public string DownloadsText => Project.DownloadsText;

        public string IconUrl => Project.IconUrl;

        public BitmapImage? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>整合包列表的一行。record 里塞不下格式化逻辑，所以包一层。</summary>
    private sealed class PackRow
    {
        public PackRow(ResourceProject project) => Project = project;

        public ResourceProject Project { get; }

        public string Title => Project.Title;

        public string SubText => $"{Project.AuthorText}　·　{Project.DownloadsText}";
    }
}
