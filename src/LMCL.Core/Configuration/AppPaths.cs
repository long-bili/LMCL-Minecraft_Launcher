namespace LMCL.Core.Configuration;

/// <summary>启动器自身的目录布局。游戏目录由配置决定，不在这里。</summary>
public static class AppPaths
{
    public static string LauncherRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LMCL");

    public static string ConfigFile => Path.Combine(LauncherRoot, "config.json");

    public static string AccountsFile => Path.Combine(LauncherRoot, "accounts.json");

    public static string CacheDir => Path.Combine(LauncherRoot, "cache");

    public static string LogDir => Path.Combine(LauncherRoot, "logs");

    public static string UpdateDir => Path.Combine(LauncherRoot, "update");

    /// <summary>本地皮肤库：Steve / Alex 两个内置位 + 一个自定义位，都是 PNG。</summary>
    public static string SkinsDir => Path.Combine(LauncherRoot, "skins");

    /// <summary>外置登录用的 authlib-injector.jar，单独放一层免得被「清缓存」误删。</summary>
    public static string AuthlibInjectorPath => Path.Combine(LauncherRoot, "authlib-injector.jar");

    /// <summary>
    /// 本地皮肤中继的 RSA 签名私钥。持久化下来复用：
    /// 每次换密钥客户端都要重新认一遍，没必要每次启动都生成新的。
    /// </summary>
    public static string SkinRelayKeyFile => Path.Combine(LauncherRoot, "skin-relay.rsa.pkcs8");

    /// <summary>标准 Minecraft 目录，和官方启动器一致。</summary>
    public static string DefaultGameDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(LauncherRoot);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
    }

    public static string Combine(params string[] parts) => Path.Combine(parts);
}
