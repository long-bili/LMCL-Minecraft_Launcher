using System.Text.Encodings.Web;
using System.Text.Json;
using CmlLib.Core;
using LMCL.Core.Configuration;

namespace LMCL.Core.Game;

/// <summary>单个版本的独立设置（版本隔离等）。存在 <c>versions/&lt;版本&gt;/lmcl.json</c>。</summary>
public sealed class InstanceSettings
{
    /// <summary>版本隔离：mods / saves / config 等放在 <c>versions/&lt;版本&gt;/</c> 里，互不干扰。</summary>
    public bool Isolated { get; set; } = true;

    public int? MaxMemoryMb { get; set; }

    public int? MinMemoryMb { get; set; }

    public string? JavaPath { get; set; }

    public bool? AutoJava { get; set; }

    public string? ExtraJvmArgs { get; set; }

    public string? ExtraGameArgs { get; set; }

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool FullScreen { get; set; }
}

/// <summary>游戏目录的路径换算。所有路径都显式指定，不依赖库的默认推导。</summary>
public static class GamePaths
{
    public const string InstanceFileName = "lmcl.json";

    public static readonly string[] ResourceKinds = { "mods", "resourcepacks", "shaderpacks", "saves" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>共享的目录布局：版本、依赖库、资源、运行时都是全局一份。</summary>
    public static MinecraftPath Shared(string gameDir)
    {
        var root = Path.GetFullPath(gameDir);
        return new MinecraftPath(root)
        {
            BasePath = root,
            Versions = Path.Combine(root, "versions"),
            Library = Path.Combine(root, "libraries"),
            Assets = Path.Combine(root, "assets"),
            Resource = Path.Combine(root, "resources"),
            Runtime = Path.Combine(root, "runtime"),
        };
    }

    /// <summary>启动用的目录布局。开了版本隔离时把游戏目录指到版本自己身上。</summary>
    public static MinecraftPath ForLaunch(string gameDir, string version, bool isolated)
    {
        var path = Shared(gameDir);
        if (isolated) path.BasePath = VersionDir(gameDir, version);
        return path;
    }

    public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");

    public static string VersionDir(string gameDir, string version) =>
        Path.Combine(gameDir, "versions", version);

    public static string VersionJson(string gameDir, string version) =>
        Path.Combine(VersionDir(gameDir, version), version + ".json");

    /// <summary>某个版本实际使用的游戏目录（隔离时为版本目录本身）。</summary>
    public static string InstanceRoot(string gameDir, string version, bool isolated) =>
        isolated ? VersionDir(gameDir, version) : gameDir;

    public static string ResourceDir(string gameDir, string version, bool isolated, string kind) =>
        Path.Combine(InstanceRoot(gameDir, version, isolated), kind);

    public static InstanceSettings LoadInstance(string gameDir, string version)
    {
        try
        {
            var file = Path.Combine(VersionDir(gameDir, version), InstanceFileName);
            if (File.Exists(file))
                return JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(file)) ?? new InstanceSettings();
        }
        catch
        {
            // 配置坏了就用默认值，不影响启动
        }
        return new InstanceSettings();
    }

    public static void SaveInstance(string gameDir, string version, InstanceSettings settings)
    {
        var dir = VersionDir(gameDir, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, InstanceFileName), JsonSerializer.Serialize(settings, Json));
    }

    public static void EnsureVersionDirs(string gameDir, string version, bool isolated)
    {
        if (!isolated) return;
        foreach (var kind in ResourceKinds)
            Directory.CreateDirectory(ResourceDir(gameDir, version, true, kind));
    }
}
