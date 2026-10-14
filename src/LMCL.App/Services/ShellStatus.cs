using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LMCL.App.Services;

/// <summary>
/// 底部状态条的唯一入口。任何页面都可以往这里报进度，
/// 不用关心自己是哪个页面——装完东西用户切页了也还能看到进度。
/// </summary>
public static class ShellStatus
{
    private static Dispatcher? _dispatcher;

    public static event Action<string>? TextChanged;

    /// <summary>ratio &lt; 0 表示进度不确定。</summary>
    public static event Action<double>? RatioChanged;

    internal static void Attach(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static void SetText(string text) => Post(() => TextChanged?.Invoke(text));

    public static void SetRatio(double ratio) => Post(() => RatioChanged?.Invoke(ratio));

    /// <summary>回到「就绪」状态。</summary>
    public static void Clear()
    {
        SetText("就绪");
        SetRatio(0);
    }

    public static void Error(string text)
    {
        SetText(text);
        SetRatio(0);
    }

    private static void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
