using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LMCL.App.Services;

/// <summary>
/// Windows 11 的云母（Mica）材质、窗口圆角、深色模式。
///
/// 全部通过 dwmapi 直接设置，任何一步失败都静默降级——在 Win10 上这些属性根本不存在，
/// 不能让程序崩掉，也不能让窗口变成一块黑。
///
/// 云母要真正透出来有个前提：窗口背景不能是不透明色。
/// 配合 <c>WindowChrome.GlassFrameThickness="-1"</c>（把 DWM 框体铺满整个窗口），
/// 内容区透明的地方才会露出系统背景材质。所以 ShellWindow 里窗口背景是 Transparent，
/// 只有这里返回 false（系统不支持）时才换成纯色兜底。
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>Win11 21H2（内部版本 22000~22620）还没有 SYSTEMBACKDROP_TYPE，用它顶。</summary>
    private const int DwmwaMicaEffect = 1029;

    private const int CornerRound = 2;
    private const int BackdropNone = 1;
    private const int BackdropMica = 2;
    private const int BackdropAcrylic = 3;

    private const int BuildWin11 = 22000;
    private const int BuildWin11_22H2 = 22621;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 开云母。返回是否真的生效——调用方要靠这个决定窗口背景用 Transparent 还是兜底纯色。
    /// </summary>
    public static bool TryApplyMica(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        var build = Environment.OSVersion.Version.Build;
        if (build < BuildWin11) return false;   // Win10 及更早没有云母

        try
        {
            ApplyCornerAndDarkMode(handle, dark);

            if (build >= BuildWin11_22H2)
            {
                var backdrop = BackdropMica;
                if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                    return true;
            }

            // 21H2 的老写法。传 -1 表示"启用云母"
            var legacy = -1;
            return DwmSetWindowAttribute(handle, DwmwaMicaEffect, ref legacy, sizeof(int)) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// 开亚克力模糊背景（Win11 22H2+ 的 SYSTEMBACKDROP_TYPE=3）。
    /// 和云母的区别：亚克力是「模糊+噪点」，内容下面透出的是糊过的桌面；
    /// 云母只吃桌面色调。返回是否真的生效。
    /// </summary>
    public static bool TryApplyAcrylic(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        // 22H2 之前没有这个 backdrop 类型，老硬件的 DWM 合成也吃不消
        if (Environment.OSVersion.Version.Build < BuildWin11_22H2) return false;

        try
        {
            ApplyCornerAndDarkMode(handle, dark);

            var backdrop = BackdropAcrylic;
            return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>系统不支持云母时的兜底：不透明窗口 + 圆角 + 深色标题栏。</summary>
    public static void ApplySolid(Window window, bool dark)    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            ApplyCornerAndDarkMode(handle, dark);

            if (Environment.OSVersion.Version.Build >= BuildWin11)
            {
                var backdrop = BackdropNone;
                _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // 老系统上没有这些属性，忽略
        }
    }

    /// <summary>
    /// 给对话框铺底。返回云母是否真的生效。
    ///
    /// 对话框最容易踩的坑：主窗口为了透出云母，把全局的 WindowBrush 改成了 alpha=0。
    /// 对话框要是照抄「Background = WindowBrush」，自己又没开云母，窗口就是全透明，
    /// DWM 合成出来一片白——暗色主题下白底白字，看着像整个界面渲染崩了。
    /// 所以这里统一处理：开了云母才透明，没开就用不透明的 DialogBrush。
    /// </summary>
    public static bool ApplyDialogBackdrop(Window window)
    {
        var dark = ThemeManager.IsDark;
        var mica = TryApplyMica(window, dark);
        if (!mica) ApplySolid(window, dark);

        window.Background = mica
            ? Brushes.Transparent
            : window.TryFindResource("DialogBrush") as Brush ?? SystemColors.WindowBrush;

        return mica;
    }

    /// <summary>只让标题栏跟随暗色——给对话框这类不支持云母的窗口用。</summary>
    public static void ApplyImmersiveDarkMode(Window window, bool dark)    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));

            if (Environment.OSVersion.Version.Build >= BuildWin11)
            {
                var corner = CornerRound;
                _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            }
        }
        catch { /* 老系统忽略 */ }
    }

    /// <summary>窗口圆角 + 深色模式。</summary>
    private static void ApplyCornerAndDarkMode(IntPtr handle, bool dark)
    {
        if (Environment.OSVersion.Version.Build >= BuildWin11)
        {
            var corner = CornerRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }

        var darkMode = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
    }
}
