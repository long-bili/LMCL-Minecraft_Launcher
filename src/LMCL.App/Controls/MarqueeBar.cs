using System.Windows;
using System.Windows.Controls;

namespace LMCL.App.Controls;

/// <summary>
/// 不确定进度时用的跑马灯。样式定义在 Themes/Controls.xaml。
/// </summary>
public sealed class MarqueeBar : Control
{
    static MarqueeBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MarqueeBar), new FrameworkPropertyMetadata(typeof(MarqueeBar)));
    }
}
