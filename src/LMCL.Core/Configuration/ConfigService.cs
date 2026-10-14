using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMCL.Core.Http;

namespace LMCL.Core.Configuration;

/// <summary>启动器全局设置。</summary>
public sealed class LauncherConfig
{
    /// <summary>游戏目录（.minecraft）。空表示用默认值。</summary>
    public string GameDir { get; set; } = "";

    public DownloadSource Source { get; set; } = DownloadSource.Bmclapi;

    /// <summary>同时下载的文件数。BMCLAPI 并发过高会返回 429，8 是实测安全的档位。</summary>
    public int DownloadThreads { get; set; } = 8;

    public string JavaPath { get; set; } = "";

    public bool AutoJava { get; set; } = true;

    public int MaxMemoryMb { get; set; } = 4096;

    public int MinMemoryMb { get; set; } = 512;

    /// <summary>light / dark / system</summary>
    public string Theme { get; set; } = "system";

    /// <summary>强调色名：Blue / Green / Purple / Orange / Rose / Teal；"Custom" 表示用下面的 RGB。</summary>
    public string Accent { get; set; } = "Blue";

    /// <summary>自定义强调色的 RGB（各 0~255）。仅在 <see cref="Accent"/> 为 "Custom" 时生效。</summary>
    public int AccentR { get; set; } = 0;

    public int AccentG { get; set; } = 95;

    public int AccentB { get; set; } = 184;

    /// <summary>背景效果：mica（云母）/ acrylic（亚克力模糊）/ solid（纯色）。</summary>
    public string Backdrop { get; set; } = "mica";

    // ── 自定义背景图与观感调节 ──────────────────────────
    //
    // 这几个值只在「背景效果 = 自定义图片」时才有意义；
    // 但通透度 / 模糊度 / 亮度也作用于云母 / 亚克力，
    // 所以它们不放在自定义背景那一支里，而是独立存在。

    /// <summary>自定义背景图的绝对路径。空 = 没用自定义背景。</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>背景图铺法：fill（拉伸铺满）/ uniform（完整显示留黑边）/ tile（平铺）。</summary>
    public string BackgroundStretch { get; set; } = "fill";

    /// <summary>
    /// 内容面板的通透度（0~1）。
    /// 1 = 完全不透明（最清晰），越小越能透出背景。
    /// 卡片 / 侧栏 / 状态条的不透明底都按这个值缩放。
    /// </summary>
    public double Transparency { get; set; } = 0.7;

    /// <summary>背景的模糊程度（0~40，对应 WPF BlurEffect 的 Radius）。0 = 不模糊。</summary>
    public double BlurRadius { get; set; } = 0;

    /// <summary>背景的明暗调节（0.2~2.0，1 = 原样，小于 1 变暗、大于 1 变亮）。</summary>
    public double Brightness { get; set; } = 1.0;

    public bool HideLauncherOnLaunch { get; set; } = true;

    public string ExtraJvmArgs { get; set; } = "";

    public string ExtraGameArgs { get; set; } = "";

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool FullScreen { get; set; }

    public int LaunchCount { get; set; }

    /// <summary>整包更新清单地址，留空则不检查更新。</summary>
    public string UpdateUrl { get; set; } = "";

    /// <summary>新建版本时默认开启版本隔离（mods / 存档放进版本自己的目录，互不干扰）。</summary>
    public bool DefaultIsolate { get; set; } = true;

    /// <summary>
    /// 登记过的游戏目录（.minecraft），供设置页快速切换。
    /// 当前目录不重复登记在这里——<see cref="GameDir"/> 就是它，空表示默认位置。
    /// </summary>
    public List<string> GameDirs { get; set; } = new();

    public string LastVersion { get; set; } = "";

    public LauncherConfig Clone() => (LauncherConfig)MemberwiseClone();
}

/// <summary>config.json 的读写。</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public LauncherConfig Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var text = File.ReadAllText(AppPaths.ConfigFile);
                Current = JsonSerializer.Deserialize<LauncherConfig>(text, Json) ?? new LauncherConfig();
            }
        }
        catch (Exception)
        {
            // 配置坏了不该让启动器起不来，退回默认值
            Current = new LauncherConfig();
        }
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var temp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
        File.Move(temp, AppPaths.ConfigFile, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>实际的游戏目录，空配置时回退到标准位置。</summary>
    public string GameDir =>
        string.IsNullOrWhiteSpace(Current.GameDir) ? AppPaths.DefaultGameDir : Current.GameDir;

    public bool UseMirror => Current.Source == DownloadSource.Bmclapi;

    public static string DescribeSource(DownloadSource source) =>
        source == DownloadSource.Bmclapi ? "BMCLAPI 镜像" : "Mojang 官方源";
}
