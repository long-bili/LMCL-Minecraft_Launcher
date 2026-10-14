using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LMCL.App.Services;

/// <summary>
/// 主题切换。WPF 没有 WinUI 的 ThemeDictionaries，所以做法是：
/// 第 0 个合并字典装颜色，换主题就是把它整本换掉；
/// 其余样式全部用 DynamicResource 引用颜色键，会自动跟着变。
/// </summary>
public static class ThemeManager
{
    /// <summary>
    /// 颜色字典的地址要写成「绝对 pack URI + 自身程序集名」的形式。
    ///
    /// 用相对路径 "Themes/Fluent.Light.xaml" 会拿入口程序集去解析——在启动器自己跑的时候
    /// 入口就是 LMCL，恰好没问题；但只要换任何别的宿主来加载这套界面（测试工具、离屏渲染），
    /// 就会跑去找那个宿主里的 Themes 目录，然后报「找不到资源」。
    /// </summary>
    private static readonly string AssemblyName =
        typeof(ThemeManager).Assembly.GetName().Name ?? "LMCL";

    private static Uri ThemeUri(string file) =>
        new($"pack://application:,,,/{AssemblyName};component/Themes/{file}", UriKind.Absolute);

    public static bool IsDark { get; private set; }

    /// <summary>主题变化后触发，用来让 Mica 之类跟着换色。</summary>
    public static event Action? Changed;

    /// <summary>把 "system" 解析成实际要用的亮/暗。</summary>
    public static bool ResolveDark(string theme) => theme switch
    {
        "dark" => true,
        "light" => false,
        _ => IsSystemDark(),
    };

    public static void Apply(string theme)
    {
        IsDark = ResolveDark(theme);

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = ThemeUri(IsDark ? "Fluent.Dark.xaml" : "Fluent.Light.xaml");

        // 第 0 本固定是颜色表
        if (dictionaries.Count == 0) dictionaries.Add(new ResourceDictionary { Source = source });
        else dictionaries[0] = new ResourceDictionary { Source = source };

        // 颜色字典整本换掉了，之前记下的 alpha 基准已经失效：
        // 不清掉的话，切到暗色主题后通透度还是按亮色那套基准算的
        ForgetAlphaBaseline();
        ApplyTransparency(Transparency);

        Changed?.Invoke();
    }

    /// <summary>读注册表判断系统是不是暗色。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) return value == 0;
        }
        catch
        {
            // 读不到就当作亮色
        }
        return false;
    }

    /// <summary>
    /// 云母生效时窗口背景必须**完全透明**——只要留一点不透明底色，云母就被糊住了，
    /// 界面立刻退回"灰盒子"。系统不支持云母时用颜色表里的纯色兜底。
    ///
    /// 做法是直接替换颜色字典里的 WindowBrush，ShellWindow 那边一句
    /// Background="{DynamicResource WindowBrush}" 就自动跟上，不用写任何分支。
    /// 注意主题切换会重建颜色字典，所以每次换肤后都要重新调一次。
    /// </summary>
    public static void SetTranslucentBackdrop(bool translucency)
    {
        var resources = Application.Current.Resources;
        if (!resources.Contains("WindowBrush")) return;

        var color = IsDark
            ? Color.FromArgb(translucency ? (byte)0x00 : (byte)0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(translucency ? (byte)0x00 : (byte)0xFF, 0xF3, 0xF3, 0xF3);

        resources["WindowBrush"] = new SolidColorBrush(color);
    }

    // ── 内容面板的通透度 ──────────────────────────────────
    //
    // 主题色表里那几张面板底色（LayerBrush / CardBrush / SideBarBrush …）本身带 alpha，
    // 通透度就是把这些 alpha 按比例缩放：
    //   Transparency = 1   → 保持原样（最清晰）
    //   Transparency = 0.3 → alpha 打到 30%，背景透得更明显
    // 只动 alpha、不动 RGB，所以主题的明暗关系不会被打乱。

    /// <summary>内容面板的通透度（0~1）。1 = 原样，越小越透。</summary>
    public static double Transparency { get; private set; } = 0.7;

    /// <summary>需要按通透度缩放 alpha 的那几支面板底色。</summary>
    private static readonly string[] TranslucentKeys =
    [
        "LayerBrush", "CardBrush", "CardHoverBrush", "SideBarBrush",
        "SubtleHoverBrush", "NavSelectedBrush", "DialogBrush",
    ];

    /// <summary>原始 alpha 备份：每次重算都从「主题原本的值」出发，避免反复乘下去越来越透。</summary>
    private static readonly Dictionary<string, byte> OriginalAlpha = new();

    public static void ApplyTransparency(double transparency)
    {
        Transparency = Math.Clamp(transparency, 0.0, 1.0);

        var resources = Application.Current.Resources;

        foreach (var key in TranslucentKeys)
        {
            if (resources[key] is not SolidColorBrush brush) continue;

            // 第一次见到这支画刷时记下它原本的 alpha 基准
            if (!OriginalAlpha.ContainsKey(key)) OriginalAlpha[key] = brush.Color.A;

            var original = OriginalAlpha[key];

            // 原来就完全透明的（比如云母模式下的 WindowBrush）不动
            if (original == 0) continue;

            var scaled = (byte)Math.Round(original * Transparency);

            var color = Color.FromArgb(scaled, brush.Color.R, brush.Color.G, brush.Color.B);
            resources[key] = new SolidColorBrush(color);
        }

        Changed?.Invoke();
    }

    /// <summary>换主题 / 换强调色之后底色会被重建，得把通透度重新贴一遍。</summary>
    public static void ReapplyTransparency() => ApplyTransparency(Transparency);

    /// <summary>主题被整本换掉时忘掉旧基准，下次重新采样。</summary>
    private static void ForgetAlphaBaseline() => OriginalAlpha.Clear();

    // ── 背景明暗调节 ──────────────────────────────────────
    //
    // 用 ColorMatrix 做整体缩放：小于 1 变暗、大于 1 变亮。
    // 对「自定义背景图」和「云母透进来的桌面」都有效——
    // 后者靠给窗口盖一层带 alpha 的黑 / 白遮罩实现（见 ShellWindow.ApplyBackgroundTint）。

    /// <summary>背景明暗（0.2~2.0，1 = 原样）。</summary>
    public static double Brightness { get; private set; } = 1.0;

    public static void ApplyBrightness(double brightness)
    {
        Brightness = Math.Clamp(brightness, 0.2, 2.0);
        Changed?.Invoke();
    }

    /// <summary>
    /// 给背景图做「模糊 + 明暗」预处理，返回处理后的位图。
    ///
    /// **为什么不用 XAML 的 BlurEffect**：那个走的是像素着色器（pixel shader），
    /// 在没有硬件加速的环境里（远程桌面、虚拟机、某些老显卡）会**整个元素画不出来** ——
    /// 表现是「一开模糊背景就变成一片纯色」，实测过。
    /// 所以改成自己按像素算：先降采样再模糊，最后交给 WPF 放大回去。
    /// 好处是任何环境都能出图，而且因为模糊本来就丢细节，降采样几乎看不出差别。
    ///
    /// 顺序：先模糊再调明暗 —— 反过来会把噪声也一起放大。
    /// </summary>
    public static BitmapSource PrepareBackground(
        BitmapSource source, double blurRadius, double brightness)
    {
        var result = source;

        if (blurRadius > 0.5)
            result = Blur(result, blurRadius);

        if (Math.Abs(brightness - 1.0) > 0.001)
            result = ApplyBrightnessTo(result, brightness);

        return result;
    }

    /// <summary>
    /// 快速模糊：降采样 → 可分离盒式模糊（横向 + 纵向各一遍）→ 返回小图。
    /// 小图会被 WPF 平滑放大，放大那一步本身又相当于一次模糊，所以观感很接近高斯。
    /// </summary>
    private static BitmapSource Blur(BitmapSource source, double radius)
    {
        // 降采样倍率：半径越大、缩得越狠。缩太多会糊成一团色块，这里留了个上限
        var divisor = Math.Clamp((int)Math.Round(radius / 3.0), 1, 8);
        var small = Downscale(source, divisor);

        var width = small.PixelWidth;
        var height = small.PixelHeight;
        var stride = width * 4;

        var pixels = new byte[stride * height];
        small.CopyPixels(pixels, stride, 0);

        // 盒式模糊的半径也按倍率缩，保证视觉半径和用户拖出来的数值一致
        var boxRadius = Math.Max(1, (int)Math.Round(radius / divisor));

        BoxBlurHorizontal(pixels, width, height, boxRadius);
        BoxBlurVertical(pixels, width, height, boxRadius);

        var blurred = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        blurred.Freeze();
        return blurred;
    }

    /// <summary>把图缩到 1/divisor，用 WPF 自己的高质量缩放，省得手写重采样。</summary>
    private static BitmapSource Downscale(BitmapSource source, int divisor)
    {
        if (divisor <= 1) return source;

        var scaled = new TransformedBitmap(source, new ScaleTransform(
            1.0 / divisor, 1.0 / divisor));

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = scaled;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    /// <summary>横向盒式模糊（就地改 pixels）。用滑动和，复杂度 O(宽×高)，与半径无关。</summary>
    private static void BoxBlurHorizontal(byte[] pixels, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        var line = new byte[width * 4];

        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width * 4, line, 0, width * 4);

            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += line[Math.Clamp(i, 0, width - 1) * 4 + channel];

                for (var x = 0; x < width; x++)
                {
                    pixels[y * width * 4 + x * 4 + channel] = (byte)(sum / window);

                    var add = Math.Clamp(x + radius + 1, 0, width - 1) * 4 + channel;
                    var drop = Math.Clamp(x - radius, 0, width - 1) * 4 + channel;
                    sum += line[add] - line[drop];
                }
            }
        }
    }

    /// <summary>纵向盒式模糊（就地改 pixels）。</summary>
    private static void BoxBlurVertical(byte[] pixels, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        var column = new byte[height * 4];

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, (y * width + x) * 4, column, y * 4, 4);

            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += column[Math.Clamp(i, 0, height - 1) * 4 + channel];

                for (var y = 0; y < height; y++)
                {
                    pixels[(y * width + x) * 4 + channel] = (byte)(sum / window);

                    var add = Math.Clamp(y + radius + 1, 0, height - 1) * 4 + channel;
                    var drop = Math.Clamp(y - radius, 0, height - 1) * 4 + channel;
                    sum += column[add] - column[drop];
                }
            }
        }
    }

    /// <summary>
    /// 把明暗作用到一张位图上（用于自定义背景图）。返回处理后的位图。
    ///
    /// 不用 ColorMatrixEffect —— 那是 WPF 里没有的（它在 System.Drawing 那边）。
    /// 这里直接按像素乘系数：读 Bgra32 的字节、每个颜色通道乘 brightness 再夹回 0~255。
    /// 背景图也就一张，这点开销看不出来。
    /// </summary>
    public static BitmapSource ApplyBrightnessTo(BitmapSource source, double brightness)
    {
        if (Math.Abs(brightness - 1.0) < 0.001) return source;

        // 统一成 Bgra32：源图可能是索引色 / 灰度，直接按 4 字节读会错位
        var converted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var format = new FormatConvertedBitmap();
            format.BeginInit();
            format.Source = source;
            format.DestinationFormat = PixelFormats.Bgra32;
            format.EndInit();
            converted = format;
        }

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;

        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Bgra32 的内存顺序是 B, G, R, A
            pixels[i] = Scale(pixels[i], brightness);
            pixels[i + 1] = Scale(pixels[i + 1], brightness);
            pixels[i + 2] = Scale(pixels[i + 2], brightness);
            // alpha 不动
        }

        var result = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;

        static byte Scale(byte channel, double factor) =>
            (byte)Math.Clamp(channel * factor, 0, 255);
    }

    // ── 强调色 ────────────────────────────────────────────

    private static ResourceDictionary? _accent;

    /// <summary>
    /// 换强调色。做法是把选中那套色板插到 MergedDictionaries 末尾——
    /// WPF 查合并字典是**逆序**的，后进来的同键字典盖住先前的，
    /// 所以 Fluent.Light/Dark 里的默认蓝不用动，插一本小的就行。
    ///
    /// <paramref name="r/g/b"/> 只有 <paramref name="name"/> 为 "Custom" 时才用得上：
    /// 那种情况下不再查色板表，而是按用户给的 RGB 现算 5 支画刷。
    /// </summary>
    public static void ApplyAccent(string name, int r = 0, int g = 95, int b = 184)
    {
        var palette = name == "Custom"
            ? BuildCustomPalette(r, g, b)
            : LoadPalette(name);

        if (palette is null) return;

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (_accent is not null)
        {
            var index = dictionaries.IndexOf(_accent);
            if (index >= 0) dictionaries[index] = palette;
            else dictionaries.Add(palette);
        }
        else
        {
            dictionaries.Add(palette);
        }

        _accent = palette;
        Changed?.Invoke();
    }

    private static ResourceDictionary? LoadPalette(string name)
    {
        var palettes = new ResourceDictionary { Source = ThemeUri("AccentPalettes.xaml") };
        return (palettes[name] as ResourceDictionary) ?? (palettes["Blue"] as ResourceDictionary);
    }

    /// <summary>
    /// 按用户给的 RGB 现造一套强调色。
    ///
    /// 除了主色，还要派生 hover（亮一点）/ pressed（暗一点）/ soft（带 alpha 的淡底），
    /// 否则按钮的悬停和按下会没有反馈 —— 色板表里那五支就是干这个的。
    /// </summary>
    private static ResourceDictionary BuildCustomPalette(int r, int g, int b)
    {
        var baseColor = Color.FromRgb(Clamp(r), Clamp(g), Clamp(b));

        var palette = new ResourceDictionary
        {
            ["AccentBrush"] = new SolidColorBrush(baseColor),
            ["AccentHoverBrush"] = new SolidColorBrush(Shift(baseColor, IsDark ? 0.18 : -0.10)),
            ["AccentPressedBrush"] = new SolidColorBrush(Shift(baseColor, IsDark ? -0.12 : -0.22)),
            // soft = 15% 不透明度的同色，做浅底强调块
            ["AccentSoftBrush"] = new SolidColorBrush(
                Color.FromArgb(0x26, baseColor.R, baseColor.G, baseColor.B)),
            ["NavIndicatorBrush"] = new SolidColorBrush(baseColor),
        };

        return palette;

        static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

        // amount > 0 往白靠，< 0 往黑靠
        static Color Shift(Color color, double amount)
        {
            double Mix(byte channel) => amount >= 0
                ? channel + (255 - channel) * amount
                : channel * (1 + amount);

            return Color.FromRgb(
                (byte)Math.Clamp(Mix(color.R), 0, 255),
                (byte)Math.Clamp(Mix(color.G), 0, 255),
                (byte)Math.Clamp(Mix(color.B), 0, 255));
        }
    }
}
