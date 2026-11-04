using LMCL.Core.Diagnostics;

namespace LMCL.Core.Resources;

public enum ResourceKind
{
    Mods,
    ResourcePacks,
    ShaderPacks,
    DataPacks,
    Saves,
}

public static class ResourceLabels
{
    public static string Of(ResourceKind kind) => kind switch
    {
        ResourceKind.Mods => "模组",
        ResourceKind.ResourcePacks => "资源包",
        ResourceKind.ShaderPacks => "光影包",
        ResourceKind.DataPacks => "数据包",
        ResourceKind.Saves => "存档",
        _ => "资源",
    };

    public static string FolderOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Mods => "mods",
        ResourceKind.ResourcePacks => "resourcepacks",
        ResourceKind.ShaderPacks => "shaderpacks",
        // 数据包放实例目录下的 datapacks：单机世界的 datapacks 也是这个名字，
        // 放进存档也能被识别，所以这里不需要额外区分
        ResourceKind.DataPacks => "datapacks",
        ResourceKind.Saves => "saves",
        _ => "mods",
    };
}

public sealed class ResourceFile
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary>没有 .disabled 后缀就是启用状态。</summary>
    public bool Enabled { get; init; } = true;

    public long Size { get; init; }

    public DateTime Modified { get; init; }

    public bool IsFolder { get; init; }

    public string SizeText => IsFolder ? "文件夹" : Game.InstalledVersion.FormatSize(Size);

    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>模组 / 资源包 / 光影 / 存档的列举与管理。</summary>
public sealed class ResourceService
{
    private const string DisabledSuffix = ".disabled";

    private readonly Configuration.ConfigService _config;
    private readonly ILogSink? _log;

    public ResourceService(Configuration.ConfigService config, ILogSink? log = null)
    {
        _config = config;
        _log = log;
    }

    public string DirectoryFor(string version, bool isolated, ResourceKind kind) =>
        Game.GamePaths.ResourceDir(_config.GameDir, version, isolated, ResourceLabels.FolderOf(kind));

    public IReadOnlyList<ResourceFile> List(string version, bool isolated, ResourceKind kind)
    {
        var directory = DirectoryFor(version, isolated, kind);
        var result = new List<ResourceFile>();
        if (!System.IO.Directory.Exists(directory)) return result;

        if (kind == ResourceKind.Saves)
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(directory))
            {
                result.Add(new ResourceFile
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    IsFolder = true,
                    Enabled = true,
                    Size = Game.VersionService.DirectorySize(dir),
                    Modified = System.IO.Directory.GetLastWriteTime(dir),
                });
            }
            return result.OrderByDescending(r => r.Modified).ToList();
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            var enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var display = enabled ? name : name[..^DisabledSuffix.Length];

            result.Add(new ResourceFile
            {
                Name = display,
                Path = file,
                Enabled = enabled,
                Size = new FileInfo(file).Length,
                Modified = System.IO.File.GetLastWriteTime(file),
            });
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>启用 / 禁用。用改后缀的方式实现，和 PCL 的做法一致。</summary>
    public void SetEnabled(ResourceFile file, bool enabled)
    {
        var target = enabled
            ? file.Path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? file.Path[..^DisabledSuffix.Length]
                : file.Path
            : file.Path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? file.Path
                : file.Path + DisabledSuffix;

        if (string.Equals(target, file.Path, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.Move(file.Path, target, overwrite: false);
            _log?.Info($"{(enabled ? "已启用" : "已禁用")} {file.Name}");
        }
        catch (Exception ex)
        {
            throw new IOException($"无法{(enabled ? "启用" : "禁用")} {file.Name}：{ex.Message}", ex);
        }
    }

    public void Delete(ResourceFile file)
    {
        if (file.IsFolder) System.IO.Directory.Delete(file.Path, recursive: true);
        else File.Delete(file.Path);
        _log?.Info($"已删除 {file.Name}");
    }

    /// <summary>把外部文件复制进来，重名自动加序号。</summary>
    public int Import(string version, bool isolated, ResourceKind kind, IEnumerable<string> sources)
    {
        var directory = DirectoryFor(version, isolated, kind);
        System.IO.Directory.CreateDirectory(directory);

        var count = 0;
        foreach (var source in sources)
        {
            if (kind == ResourceKind.Saves && System.IO.Directory.Exists(source))
            {
                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                var target = UniqueName(directory, name, isFolder: true);
                CopyDirectory(source, target);
                count++;
            }
            else if (File.Exists(source))
            {
                var target = UniqueName(directory, Path.GetFileName(source), isFolder: false);
                File.Copy(source, target, overwrite: false);
                count++;
            }
        }

        if (count > 0) _log?.Success($"已导入 {count} 个{ResourceLabels.Of(kind)}");
        return count;
    }

    private static string UniqueName(string directory, string name, bool isFolder)
    {
        var target = Path.Combine(directory, name);
        if (!File.Exists(target) && !System.IO.Directory.Exists(target)) return target;

        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? "" : Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !System.IO.Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>把一个版本的资源目录整体指过去（打开所在目录用）。</summary>
    public string EnsureDirectory(string version, bool isolated, ResourceKind kind)
    {
        var directory = DirectoryFor(version, isolated, kind);
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>各资源目录的占用统计。</summary>
    public (int Mods, int Packs, int Shaders, int Saves) Counts(string version, bool isolated) => (
        List(version, isolated, ResourceKind.Mods).Count,
        List(version, isolated, ResourceKind.ResourcePacks).Count,
        List(version, isolated, ResourceKind.ShaderPacks).Count,
        List(version, isolated, ResourceKind.Saves).Count);
}
