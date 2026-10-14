using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LMCL.App.Animations;
using LMCL.App.Services;
using LMCL.App.Views.Pages;
using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Views;

/// <summary>页面统一接口：进入时刷新数据。</summary>
public interface IPage
{
    void OnNavigatedTo();
}

public partial class ShellWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private int _navGeneration;
    private bool _micaApplied;
    private bool _allowClose;
    private bool _closeInvoked;

    public ShellWindow()
    {
        InitializeComponent();

        TitleVersion.Text = $"v{AppServices.Version}";
        ShellStatus.Attach(Dispatcher);
        ShellStatus.TextChanged += text => StatusText.Text = text;
        ShellStatus.RatioChanged += UpdateProgress;

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;

        AccountMenu.ItemsSource = _choices;
        AccountPopup.Closed += AccountPopup_Closed;
        // 展开时播一个「轻轻弹一下」：比 PopupAnimation="Fade" 有质感，
        // 系统关掉动画效果时 Animator 会直接到位，不会留个半透明影子
        AccountPopup.Opened += (_, _) =>
        {
            if (AccountPopup.Child is FrameworkElement surface)
                Animator.AaPopupOpen(surface);
        };

        App.Services.Accounts.Changed += (_, _) => Dispatcher.Invoke(RefreshAccount);
        ThemeManager.Changed += OnThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 启动动画：Loaded 先于窗口可见，在这里把透明度按下去再淡入，
        // 用户看不到「先闪一下再播动画」的中间态
        PlayOpenAnimation();

        RefreshAccount();
        RefreshSideInfo();

        Nav.SelectedIndex = 0;
        ApplyBackdrop();
    }

    /// <summary>窗口出现：淡入 + 轻微放大，150ms 一带而过。</summary>
    private void PlayOpenAnimation()
    {
        if (!Animator.Enabled) return;

        Opacity = 0;
        Animator.AaOpacity(this, 1, 150);
        Animator.AaScale(this, 1, 180, 0, AniEase.OutFluent());
    }

    /// <summary>窗口关闭：淡出 + 轻微缩小，播完才真正关。</summary>
    private void PlayCloseAnimation()
    {
        if (!Animator.Enabled)
        {
            DoClose();
            return;
        }

        var finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            DoClose();
        }

        Animator.AaOpacity(this, 0, 140, 0, AniEase.InFluent());
        Animator.AaScale(this, 0.97, 140, 0, AniEase.InFluent());
        Animator.AaCode(Finish, 160);

        // 兜底：动画时钟万一不前进（窗口被提前藏起来之类），
        // 也不能让「点了关闭却关不掉」发生
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        guard.Tick += (s, e) => { guard.Stop(); Finish(); };
        guard.Start();
    }

    private void DoClose()
    {
        _allowClose = true;

        // 关键修复：不能在 OnClosing / InternalClose 的调用栈里同步再调 Close()。
        // 此时窗口内部 _isClosing 已被置位，嵌套的 Close() 会立刻抛
        // InvalidOperationException（"Cannot ... call ... Close ... while a Window is closing"）。
        // 改用低优先级 BeginInvoke，等本次 OnClosing 完全出栈、WPF 把 _isClosing 复位后，
        // 再在下一轮消息循环里真正关闭。_closeInvoked 防止 Finish 与兜底定时器重复触发。
        if (_closeInvoked) return;
        _closeInvoked = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Close());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 第一次进这里先取消关闭、播淡出；动画回调里置了 _allowClose 再进来才真关。
        // 少了这个守卫会无限递归：Close() → OnClosing → Close() …
        if (!_allowClose)
        {
            e.Cancel = true;
            PlayCloseAnimation();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 刷新左下角的「Java / 下载源」摘要。设置页改了这两项之后要跟着更新，
    /// 所以抽出来给它复用。
    /// </summary>
    public void RefreshSideInfo()
    {
        SideSource.Text = $"下载源：{Core.Configuration.ConfigService.DescribeSource(App.Services.Config.Current.Source)}";

        // Java 扫盘比较慢，放后台跑完再回填
        _ = Task.Run(async () =>
        {
            var javas = await App.Services.Java.DetectAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                SideJava.Text = javas.Count == 0
                    ? "Java：未检测到"
                    : $"Java：{string.Join(" / ", javas.Select(j => j.Major).Distinct().OrderBy(m => m))}";
            });
        });
    }

    // ── 窗口外观 ──────────────────────────────────────────

    /// <summary>
    /// 云母要在窗口句柄存在之后才能设置，所以放在 Loaded 而不是构造函数里。
    /// 设置结果同时决定窗口背景用透明（透出云母）还是纯色兜底。
    /// </summary>
    private void ApplyBackdrop()
    {
        var config = App.Services.Config.Current;
        var mode = config.Backdrop;

        // 自定义背景图：整层铺一张图，不启用系统材质（材质在上面反而会打架）
        if (mode == "image")
        {
            _micaApplied = false;
            WindowEffects.ApplySolid(this, ThemeManager.IsDark);

            // 窗口背景必须不透明，否则图下面会透出桌面
            ThemeManager.SetTranslucentBackdrop(false);

            ApplyBackgroundImage(config);
            return;
        }

        CustomBackground.Source = null;
        CustomBackground.Visibility = Visibility.Collapsed;

        // 背景效果三档：云母 / 亚克力模糊 / 纯色。前两档生效时窗口背景必须全透明，
        // 否则材质直接被糊住；系统不支持就退到纯色
        _micaApplied = mode switch
        {
            "acrylic" => WindowEffects.TryApplyAcrylic(this, ThemeManager.IsDark),
            "solid" => false,
            _ => WindowEffects.TryApplyMica(this, ThemeManager.IsDark),
        };

        if (!_micaApplied) WindowEffects.ApplySolid(this, ThemeManager.IsDark);

        // 换肤会重建颜色字典，所以这两步必须成对执行
        ThemeManager.SetTranslucentBackdrop(_micaApplied);

        ApplyBackgroundTint(config);

        var material = mode switch
        {
            "acrylic" => "亚克力模糊",
            "solid" => "纯色",
            _ => "云母",
        };

        // 材质是不是真的开起来了，只有系统说得准；截图看不出区别时全靠这条日志
        App.Services.Log.Info(_micaApplied
            ? $"窗口材质：{material}已启用"
            : $"窗口材质：{material}不可用（内部版本 {Environment.OSVersion.Version.Build} 或不支持），已退回纯色背景");
    }

    /// <summary>
    /// 铺自定义背景图。
    ///
    /// 模糊和明暗都在 ThemeManager 里按像素算好（不用 XAML BlurEffect，
    /// 那个在没硬件加速的环境下会把元素整个画不出来）。
    /// 「模糊」刻意放在 ReprepareBackground 而不是这里 —— 拖模糊滑杆时
    /// 只重算模糊、不重新解码图片。
    /// </summary>
    private void ApplyBackgroundImage(LauncherConfig config)
    {
        var path = config.BackgroundImage;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            CustomBackground.Source = null;
            CustomBackground.Visibility = Visibility.Collapsed;
            BackgroundTint.Visibility = Visibility.Collapsed;
            _backgroundSource = null;

            if (!string.IsNullOrWhiteSpace(path))
                App.Services.Log.Warn($"自定义背景图不存在：{path}");

            return;
        }

        try
        {
            // 解码只做一次，之后拖滑杆都复用这一份原图
            if (_backgroundSource is null || !string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                _backgroundSource = image;
                _backgroundPath = path;
            }

            CustomBackground.Source = ThemeManager.PrepareBackground(
                _backgroundSource, config.BlurRadius, config.Brightness);

            CustomBackground.Stretch = config.BackgroundStretch switch
            {
                "uniform" => Stretch.Uniform,
                "fill" => Stretch.Fill,
                _ => Stretch.UniformToFill,
            };
            CustomBackground.Visibility = Visibility.Visible;

            // 图片模式下不用遮罩：亮度已经烘进图里了
            BackgroundTint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CustomBackground.Source = null;
            CustomBackground.Visibility = Visibility.Collapsed;
            App.Services.Log.Warn($"自定义背景图加载失败：{ex.Message}");
        }
    }

    /// <summary>已解码的背景原图（拖滑杆时复用，不重复解码）。</summary>
    private BitmapSource? _backgroundSource;
    private string _backgroundPath = "";

    /// <summary>
    /// 重算背景图。拖模糊 / 明暗滑杆时走这条 —— 图片已经解码过就直接复用，
    /// 不重新读盘，所以滑块跟手。
    /// </summary>
    public void ReprepareBackground() => ApplyBackgroundImage(App.Services.Config.Current);

    /// <summary>
    /// 云母 / 亚克力模式下没法直接改系统材质的亮度，就在上面盖一层黑或白的半透明膜。
    /// 亮度 1.0 时把遮罩收掉，一帧都不用画。
    /// </summary>
    private void ApplyBackgroundTint(LauncherConfig config)
    {
        var brightness = config.Brightness;

        if (Math.Abs(brightness - 1.0) < 0.02)
        {
            BackgroundTint.Visibility = Visibility.Collapsed;
            return;
        }

        // 变暗：黑色 + (1 - brightness) 的不透明度；变亮：白色 + (brightness - 1) 的一半
        var (color, alpha) = brightness < 1.0
            ? (Colors.Black, (1.0 - brightness) * 0.85)
            : (Colors.White, Math.Min((brightness - 1.0) * 0.45, 0.8));

        BackgroundTint.Fill = new SolidColorBrush(color)
        {
            Opacity = Math.Clamp(alpha, 0, 0.9),
        };
        BackgroundTint.Visibility = Visibility.Visible;
    }

    /// <summary>重新铺一次窗口材质。设置页换了背景效果（云母/亚克力/纯色/图片）后调。</summary>
    public void ReapplyBackdrop() => ApplyBackdrop();

    /// <summary>
    /// 全局刷新：游戏目录切换这种影响所有页面的事之后调。
    /// 不重建页面（缓存留着，状态不丢），只让当前页重新读一遍数据——
    /// 各页的 OnNavigatedTo 自己知道要刷什么。
    /// </summary>
    public void RefreshAll()
    {
        RefreshSideInfo();
        if (Host.Content is IPage page) page.OnNavigatedTo();
    }

    private void OnThemeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            WindowEffects.ApplyImmersiveDarkMode(this, ThemeManager.IsDark);
            ApplyBackdrop();
        });
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 最大化时窗口不该被圆角裁掉
        WindowEffects.ApplyImmersiveDarkMode(this, ThemeManager.IsDark);
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ── 窗口按钮 ──────────────────────────────────────────
    // 拖拽、双击最大化、Aero Snap、右键系统菜单都由 WindowChrome 的 CaptionHeight 交给系统了，
    // 这里只需要实现三个按钮的动作。

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        WindowEffects.ApplyImmersiveDarkMode(this, ThemeManager.IsDark);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (message == WmGetMinMaxInfo)
        {
            FixMaximizedBounds(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// WindowStyle=None + WindowChrome 的经典毛病：最大化会盖住任务栏。
    /// 回应 WM_GETMINMAXINFO，把最大化尺寸限制到显示器的工作区。
    /// </summary>
    private void FixMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        mmi.MaxPosition.X = info.WorkArea.Left - info.MonitorArea.Left;
        mmi.MaxPosition.Y = info.WorkArea.Top - info.MonitorArea.Top;
        mmi.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        mmi.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        mmi.MinTrackSize.X = (int)(MinWidth * scale);
        mmi.MinTrackSize.Y = (int)(MinHeight * scale);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // ── 导航 ──────────────────────────────────────────────

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        Navigate(item.Tag as string ?? "home");
    }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            if (page is null) return;
            _pages[key] = page;
        }

        // 点的就是当前页：不作声，省得自己退出自己
        if (ReferenceEquals(Host.Content, page)) return;

        // 每次导航发一个代号；旧页退出动画播完时若代号变了，说明中间又切过，这次作废
        var generation = ++_navGeneration;
        var previous = Host.Content as FrameworkElement;

        void Swap()
        {
            if (generation != _navGeneration) return;

            Host.Content = page;
            if (page is IPage loadable) loadable.OnNavigatedTo();

            // 两层动画：整页淡入 + 上移 + 放大回弹（AaPageEnter），
            // 内部各块再依次浮入（PlayEnterAnimation）。后者只对顶层页面播——
            // 嵌在宿主里的子面板由宿主的页签切换驱动，不然会叠成两层
            if (page is FrameworkElement shell) Animator.AaPageEnter(shell);
            if (page is PageBase pageBase) pageBase.PlayEnterAnimation();
        }

        // 旧页先快速退场，退完再换新页——两页之间不再「硬切」
        if (previous is not null)
            Animator.AaPageExit(previous, Swap);
        else
            Swap();
    }

    /// <summary>
    /// 按 key 跳页，并让左侧导航的选中项跟着走。
    /// 直接改 SelectedIndex 会触发 SelectionChanged，页面切换仍然只有一条路径。
    /// </summary>
    public void GoTo(string key)
    {
        var index = -1;
        for (var i = 0; i < Nav.Items.Count; i++)
        {
            if (Nav.Items[i] is ListBoxItem item && (string?)item.Tag == key) { index = i; break; }
        }

        if (index >= 0) Nav.SelectedIndex = index;
        else Navigate(key);
    }

    /// <summary>跳到「版本与资源」并选中某个版本。启动失败时点「去看看」就落到这儿。</summary>
    public void GoToVersion(string version)
    {
        GoTo("versions");
        if (_pages.TryGetValue("versions", out var page) && page is VersionsPage versions)
            versions.Select(version);
    }

    /// <summary>跳到「版本与资源」并直接打开某个版本的设置页。主页「版本设置」按钮用。</summary>
    public void GoToVersionSettings(string version)
    {
        GoToVersion(version);
        if (_pages.TryGetValue("versions", out var page) && page is VersionsPage versions)
            versions.SelectAndShowSettings(version);
    }

    /// <summary>打开设置页里的某个分栏（比如「关于与帮助」）。</summary>
    public void GoToSettingsTab(string tab)
    {
        GoTo("settings");
        if (_pages.TryGetValue("settings", out var page) && page is SettingsPage settings)
            settings.ShowTab(tab);
    }

    private static FrameworkElement? CreatePage(string key) => key switch
    {
        "home" => new HomePage(),
        "download" => new DownloadPage(),
        "versions" => new VersionsPage(),
        "log" => new LogPage(),
        "settings" => new SettingsPage(),
        _ => null,
    };

    // ── 账号胶囊 ──────────────────────────────────────────

    /// <summary>
    /// 账号菜单里的行。和账号管理窗口里的 AccountRow 长得像但不是一回事：
    /// 这里只需要「名字 + 类型 + 是不是当前」，不需要详情。
    /// </summary>
    public sealed class AccountChoice
    {
        public AccountChoice(Account model, bool isCurrent)
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
                var text = Model.TypeLabel;
                if (Model.IsExpired) text += "　·　凭证已过期";
                return text;
            }
        }
    }

    private readonly ObservableCollection<AccountChoice> _choices = new();

    /// <summary>重建菜单会顺带清掉 SelectedItem，那次 SelectionChanged 不算用户点的。</summary>
    private bool _menuBusy;

    /// <summary>菜单被「点外面」关掉的时间点，用来把「同一击又把它点开」挡回去。</summary>
    private DateTime _popupClosedAt;

    private void RefreshAccount()
    {
        var account = App.Services.Accounts.Selected;
        if (account is null)
        {
            AccountInitial.Text = "?";
            AccountName.Text = "未登录";
            AccountType.Text = "点击添加账号";
        }
        else
        {
            AccountInitial.Text = string.IsNullOrEmpty(account.Name)
                ? "?" : account.Name[..1].ToUpperInvariant();
            AccountName.Text = account.Name;
            AccountType.Text = account.TypeLabel;
        }

        BuildAccountMenu();
    }

    private void BuildAccountMenu()
    {
        _menuBusy = true;
        _choices.Clear();

        foreach (var item in App.Services.Accounts.Accounts.OrderByDescending(a => a.LastUsed))
            _choices.Add(new AccountChoice(item, item.Id == App.Services.Accounts.SelectedId));

        AccountMenu.SelectedItem = null;
        _menuBusy = false;

        var empty = _choices.Count == 0;
        MenuEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MenuCaption.Text = empty ? "还没有账号" : "切换账号";
    }

    /// <summary>
    /// 点账号条：开 / 关菜单。
    ///
    /// 这里有个坑：菜单是 StaysOpen=false，点账号条本身也算「点外面」，
    /// 于是它先关掉、紧接着这次点击又把它打开，看上去就是永远关不掉。
    /// 用关闭时间戳把这一击吃掉。
    /// </summary>
    private void AccountChip_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup.IsOpen)
        {
            AccountPopup.IsOpen = false;
            return;
        }

        if ((DateTime.Now - _popupClosedAt).TotalMilliseconds < 250) return;

        // 位置不用算：Placement=Bottom 会按账号条的右边缘把菜单右对齐过去
        AccountPopup.IsOpen = true;
    }

    private void AccountPopup_Closed(object? sender, EventArgs e) => _popupClosedAt = DateTime.Now;

    /// <summary>在菜单里选了另一个账号——这一步就直接换过去，不再弹管理窗口。</summary>
    private void AccountMenu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_menuBusy) return;

        // 重建列表清空选中时 AddedItems 是空的，只有真点了才有
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not AccountChoice choice) return;

        AccountPopup.IsOpen = false;

        if (choice.IsCurrent) return;

        App.Services.Accounts.Select(choice.Model.Id);
        App.Services.Log.Info($"已切换账号：{choice.Model.Name}");

        RefreshAccount();

        if (Host.Content is IPage page) page.OnNavigatedTo();
    }

    private void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        OpenAccountWindow();
    }

    /// <summary>打开账号管理。首页的上手引导卡片也走这里，免得两处各写一遍。</summary>
    public void OpenAccountWindow()
    {
        var window = new AccountWindow { Owner = this };
        window.ShowDialog();
        RefreshAccount();

        // 账号变了，当前页面的账号信息要跟着刷新
        if (Host.Content is IPage page) page.OnNavigatedTo();
    }

    private void UpdateProgress(double ratio)
    {
        if (ratio < 0)
        {
            StatusProgress.Visibility = Visibility.Collapsed;
            StatusMarquee.Visibility = Visibility.Visible;
            StatusPercent.Text = "";
            return;
        }

        StatusMarquee.Visibility = Visibility.Collapsed;

        if (ratio <= 0)
        {
            StatusProgress.Visibility = Visibility.Collapsed;
            StatusPercent.Text = "";
            return;
        }

        StatusProgress.Visibility = Visibility.Visible;
        StatusProgress.Value = ratio;
        StatusPercent.Text = $"{ratio * 100:0}%";
    }

    // ── Win32 互操作 ──────────────────────────────────────

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public int Flags;
    }
}
