using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LMCL.App.Services;

namespace LMCL.App.Views;

/// <summary>消息框的语气。决定图标、图标颜色和按钮组合。</summary>
public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>询问：会多出一个「取消」，返回 bool。</summary>
    Question,
}

/// <summary>
/// 统一风格的消息框（替掉 Win32 的 MessageBox）。
///
/// 沿用了项目里其它对话框的骨架：WindowChrome 自绘标题栏 + 内容 + 底部按钮条，
/// 所以云母、圆角、深色模式、强调色都跟主界面一致 ——
/// 这一点正是 MessageBox 做不到的（它画的是系统那套，弹出来很突兀）。
///
/// 用法（一般不直接用，走 <c>Ui.Info/Warn/Error/Confirm</c>）：
/// <c>MessageDialog.Ask("确定删除？", "确认", MessageKind.Question, danger: true)</c>
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>点了「确定 / 是」为 true；关闭或取消为 false。</summary>
    public bool Confirmed { get; private set; }

    public MessageDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 和别的对话框一样：主窗口把 WindowBrush 改成全透明了，
        // 这里不重新铺一次背景就会渲染成一片窟窿
        WindowEffects.ApplyDialogBackdrop(this);
    }

    /// <summary>
    /// 显示一条消息（只有「确定」）。
    /// 没有可用主窗口时（比如命令行自检）回退到 MessageBox —— 那种场景下
    /// 弹一个没有宿主的窗口既没必要、也容易卡住流程。
    /// </summary>
    public static void Show(string message, string title, MessageKind kind, string okText = "确定")
    {
        var dialog = new MessageDialog();
        dialog.Setup(message, title, kind, okText, cancelText: null, danger: false);
        dialog.ShowModal();
    }

    /// <summary>显示一个询问（「是 / 否」），返回用户是不是点了「是」。</summary>
    public static bool Ask(string message, string title, MessageKind kind,
        string okText = "是", string cancelText = "否", bool danger = false)
    {
        var dialog = new MessageDialog();
        dialog.Setup(message, title, kind, okText, cancelText, danger);
        dialog.ShowModal();
        return dialog.Confirmed;
    }

    private void Setup(string message, string title, MessageKind kind,
        string okText, string? cancelText, bool danger)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        AskMode = cancelText is not null;
        BtnCancel.Visibility = AskMode ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Content = cancelText ?? "取消";
        BtnOk.Content = okText;

        Apply(kind, danger);
    }

    /// <summary>是不是询问模式（有取消按钮）。关闭时按这个决定返回值。</summary>
    private bool AskMode { get; set; }

    private void Apply(MessageKind kind, bool danger)
    {
        var (glyph, brushKey) = kind switch
        {
            MessageKind.Success => ("\uE73E", "SuccessBrush"),
            MessageKind.Warning => ("\uE7BA", "WarningBrush"),
            MessageKind.Error => ("\uE783", "ErrorBrush"),
            MessageKind.Question => ("\uE897", "AccentBrush"),
            _ => ("\uE946", "AccentBrush"),
        };

        var brush = TryFindResource(brushKey) as SolidColorBrush
                    ?? new SolidColorBrush(Colors.Gray);
        var color = brush.Color;

        // 标题栏那个小图标 + 正文左边的大图标用同一个字形
        KindIcon.Text = glyph;
        KindIcon.Foreground = brush;
        BigIcon.Text = glyph;

        // 底板做成同色的很淡一片，图标本身保持纯色 —— 深色/浅色主题下都看得清
        IconPlate.Color = Color.FromArgb(0x22, color.R, color.G, color.B);
        IconInk.Color = color;

        if (danger)
        {
            // 危险操作：按钮不铺红底（暗色主题下红底配白字反而看不清），
            // 改成红字，主题怎么换都清楚
            BtnOk.Style = (Style)FindResource("SubtleButton");
            BtnOk.Foreground = brush;
            BtnOk.FontWeight = FontWeights.SemiBold;
        }
    }

    private void ShowModal()
    {
        // 有可见的主窗口就挂上去（居中于它、跟着最小化）；没有就退回系统弹窗
        var owner = Application.Current?.MainWindow;

        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // 命令行自检等没有界面宿主的情况：不能真的弹窗口，用系统框兜底，
            // 保证信息不丢、流程不卡
            System.Windows.MessageBox.Show(
                MessageText.Text, Title,
                AskMode ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                MessageBoxImage.None);
            Confirmed = false;
            return;
        }

        ShowDialog();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Esc = 取消/关闭；Enter = 确定。键盘操作和 Win32 MessageBox 的手感保持一致
        if (e.Key == Key.Escape)
        {
            Confirmed = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirmed = true;
            Close();
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    /// <summary>标题栏的 × ：等同于取消。</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    /// <summary>
    /// 自检用：五种语气各套一遍，确认图标、图标颜色、按钮组合真的跟着变了。
    ///
    /// 为什么值得断言：这些全靠 `FindResource` 取动态资源键（SuccessBrush 等）
    /// 和给命名元素（IconPlate / IconInk / BtnCancel）赋值。
    /// 任何一个键名写错、或者元素名对不上，都不会抛异常，只会静默退化成
    /// 灰色问号 + 永远显示取消按钮 —— 界面上看得出来，但构造阶段完全无声。
    /// </summary>
    internal bool KindsRendered()
    {
        try
        {
            var seenGlyphs = new HashSet<string>();
            var seenColors = new HashSet<Color>();
            var ok = true;

            foreach (var kind in new[]
                     {
                         MessageKind.Info, MessageKind.Success, MessageKind.Warning,
                         MessageKind.Error, MessageKind.Question,
                     })
            {
                Apply(kind, danger: false);

                seenGlyphs.Add(BigIcon.Text);
                seenColors.Add(IconInk.Color);

                // 图标不能退化成空，颜色不能是没取到资源的默认灰
                if (string.IsNullOrEmpty(BigIcon.Text)) ok = false;
                if (IconInk.Color == Colors.Gray) ok = false;
            }

            // 五种语气应当给出四种以上不同的图标（Info 和 Question 各一个字形，
            // 这里只要求确实有区分度，不苛求五个全不同）
            if (seenGlyphs.Count < 4) ok = false;
            if (seenColors.Count < 3) ok = false;

            // 询问模式：取消按钮要出现；普通提示：不该出现
            Setup("自检", "自检", MessageKind.Question, "是", "否", danger: false);
            var askHasCancel = BtnCancel.Visibility == Visibility.Visible;

            Setup("自检", "自检", MessageKind.Info, "确定", null, danger: false);
            var infoHasCancel = BtnCancel.Visibility == Visibility.Visible;

            return ok && askHasCancel && !infoHasCancel;
        }
        catch
        {
            return false;
        }
    }
}
