using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LMCL.Core.Configuration;

namespace LMCL.Core.Accounts;

/// <summary>本地皮肤库的分组。离线账号和微软账号各一套，互不串用。</summary>
public enum SkinLibraryKind
{
    Offline = 0,
    Microsoft = 1,
}

/// <summary>Steve 宽手臂 / Alex 细手臂。上传皮肤时必须选一个。</summary>
public enum SkinArmModel
{
    Classic = 0,
    Slim = 1,
}

/// <summary>
/// 本地皮肤库里的一个「位」。
///
/// 两类：
///   · 内置位 —— Steve / Alex，每个库各一份，不允许删；
///   · 自定义位 —— 用户导入的 PNG，**可以有很多张**，每张一个 id（custom-&lt;kind&gt;-&lt;guid&gt;）。
///
/// 离线账号没有服务端档案，皮肤只是「本地想用哪张」——所以这里管的是文件名和元信息，
/// 真正的材质还是存在磁盘上的那张 PNG。
/// </summary>
public sealed class SkinEntry
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public SkinLibraryKind Library { get; set; } = SkinLibraryKind.Offline;

    public SkinArmModel ArmModel { get; set; } = SkinArmModel.Classic;

    /// <summary>PNG 在皮肤库目录下的文件名。</summary>
    public string FileName { get; set; } = "";

    /// <summary>启动器自带的 Steve / Alex，不允许删。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// 老版本留下的「唯一自定义槽」。现在自定义皮肤是一列表了，
    /// 这个标记只用来识别并迁移老数据（见 <see cref="SkinLibrary.EnsureSeeded"/>）。
    /// </summary>
    public bool IsCustomSlot { get; set; }

    /// <summary>用户自己导入的那一类（可多张、可删）。</summary>
    public bool IsUserSkin => !IsBuiltIn;

    /// <summary>空的自定义槽还没导入过 PNG，此时没有可看的内容。</summary>
    public bool IsConfigured { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayName
    {
        get
        {
            if (IsBuiltIn)
            {
                if (Id.Contains("steve", StringComparison.OrdinalIgnoreCase)) return "Steve（默认）";
                if (Id.Contains("alex", StringComparison.OrdinalIgnoreCase)) return "Alex（默认）";
                return "内置皮肤";
            }

            // 用户导入的：优先用文件名，没有就给个通用名
            return string.IsNullOrWhiteSpace(Name) ? "自定义皮肤" : Name;
        }
    }

    public string ArmModelLabel => ArmModel == SkinArmModel.Slim ? "纤细（Alex）" : "经典（Steve）";
}

/// <summary>
/// 本地皮肤库。索引存在 <c>%APPDATA%\LMCL\skins\skins.json</c>，PNG 就在同目录。
///
/// 为什么先从客户端 jar 里挖：真实的 Steve / Alex 跟着游戏版本走，挖出来的那张
/// 和游戏里看到的完全一致；jar 里没有（比如一个版本都没装过）再用随启动器
/// 附带的那两份兜底。
/// </summary>
public sealed class SkinLibrary
{
    private const string SteveEntry = "assets/minecraft/textures/entity/player/wide/steve.png";
    private const string AlexEntry = "assets/minecraft/textures/entity/player/slim/alex.png";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _indexFile;
    private List<SkinEntry> _entries = new();
    private readonly object _gate = new();

    public SkinLibrary()
    {
        Directory.CreateDirectory(AppPaths.SkinsDir);
        _indexFile = Path.Combine(AppPaths.SkinsDir, "skins.json");
        Load();
    }

    public event EventHandler? Changed;

    public static string SteveId(SkinLibraryKind kind) => $"official-steve-{kind.ToString().ToLowerInvariant()}";

    public static string AlexId(SkinLibraryKind kind) => $"official-alex-{kind.ToString().ToLowerInvariant()}";

    /// <summary>
    /// 老版本那个「唯一自定义槽」的 id。现在自定义皮肤是一列表了，
    /// 这个只用于识别并迁移旧数据。
    /// </summary>
    public static string LegacyCustomId(SkinLibraryKind kind) => $"custom-{kind.ToString().ToLowerInvariant()}";

    private static string NewCustomId(SkinLibraryKind kind) =>
        $"custom-{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";

    public IReadOnlyList<SkinEntry> List(SkinLibraryKind kind)
    {
        lock (_gate)
            return _entries.Where(e => e.Library == kind).ToList();
    }

    /// <summary>只有用户自己导入的那些（内置的 Steve / Alex 不算），新的在前。</summary>
    public IReadOnlyList<SkinEntry> ListUserSkins(SkinLibraryKind kind)
    {
        lock (_gate)
            return _entries
                .Where(e => e.Library == kind && e.IsUserSkin)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
    }

    /// <summary>内置的那两张（Steve / Alex）。</summary>
    public IReadOnlyList<SkinEntry> ListBuiltIn(SkinLibraryKind kind)
    {
        lock (_gate)
            return _entries.Where(e => e.Library == kind && e.IsBuiltIn).ToList();
    }

    public SkinEntry? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
            return _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public string PathOf(SkinEntry entry) => Path.Combine(AppPaths.SkinsDir, entry.FileName);

    /// <summary>这一位上有没有真正可看的 PNG。</summary>
    public bool IsReady(SkinEntry entry)
    {
        if (!entry.IsConfigured) return false;
        var path = PathOf(entry);
        return File.Exists(path) && new FileInfo(path).Length > 64;
    }

    /// <summary>
    /// 把 Steve / Alex / 自定义三个槽补全。已经存在且文件完好的不动，
    /// 只在「没种子过」或者「文件缺失 / 损坏」时补。
    ///
    /// 注意这里**每次都要检查文件在不在**，不能只在「新加了槽」时才写：
    /// 老版本在别的电脑上可能已经把槽建好了、但 PNG 没落地，
    /// 那种情况下槽是存在的、图却是坏的，只补槽不补文件就永远修不好。
    /// </summary>
    public void EnsureSeeded(string gameDir)
    {
        var changed = false;

        // 只有 Steve / Alex 两个内置位是「必须有」的。
        // 自定义皮肤现在是用户自己添加的一列表，不再预先建空槽。
        foreach (var kind in new[] { SkinLibraryKind.Offline, SkinLibraryKind.Microsoft })
        {
            changed |= EnsureSlot(SteveId(kind), "Steve", SkinArmModel.Classic, true, SteveEntry);
            changed |= EnsureSlot(AlexId(kind), "Alex", SkinArmModel.Slim, true, AlexEntry);
        }

        // 老版本留下的那个「唯一自定义槽」迁移成普通的一张自定义皮肤
        changed |= MigrateLegacyCustomSlot();

        // 内置两份 PNG 需要从客户端 jar 里挖，这一步可能慢；
        // WriteBuiltIn 自己是幂等的（文件好用就直接返回）
        var refreshed = false;
        foreach (var kind in new[] { SkinLibraryKind.Offline, SkinLibraryKind.Microsoft })
        {
            refreshed |= WriteBuiltIn(gameDir, SteveId(kind), SteveEntry, "steve.png", default);
            refreshed |= WriteBuiltIn(gameDir, AlexId(kind), AlexEntry, "alex.png", default);
        }

        if (!changed && !refreshed)
        {
            Raise();
            return;
        }

        Save();
        Raise();
    }

    /// <summary>在后台补种子。挖客户端 jar 有点慢，别堵住启动线程。</summary>
    public Task EnsureSeededAsync(string gameDir, CancellationToken ct = default) =>
        Task.Run(() => EnsureSeeded(gameDir), ct);

    private bool EnsureSlot(string id, string name, SkinArmModel armModel, bool builtIn, string? jarEntry)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Id == id)) return false;

            _entries.Add(new SkinEntry
            {
                Id = id,
                Name = name,
                Library = id.EndsWith("offline", StringComparison.OrdinalIgnoreCase)
                    ? SkinLibraryKind.Offline
                    : SkinLibraryKind.Microsoft,
                ArmModel = armModel,
                FileName = $"{id}.png",
                IsBuiltIn = builtIn,
                IsCustomSlot = !builtIn,
                IsConfigured = builtIn,
            });
            return true;
        }
    }

    /// <summary>把内置皮肤写到磁盘上。返回 true 表示这次真的写了（或改了配置状态）。</summary>
    private bool WriteBuiltIn(
        string gameDir, string id, string jarEntry, string bundledName, CancellationToken ct)
    {
        var entry = Find(id);
        if (entry is null) return false;
        if (ct.IsCancellationRequested) return false;

        var target = PathOf(entry);

        // 文件已经在就当它好使。
        // 但有个历史遗留要修：老版本在「换过电脑」之后可能留下一个 0 字节或半截的
        // PNG（种子那一步没跑成），这种文件大小 < 64，下面的 IsReady 会判成不可用，
        // 这里必须当成「没有」，先清掉再重新写一遍。
        if (File.Exists(target) && new FileInfo(target).Length > 64) return false;
        TryDeleteBroken(target);

        // ① 优先从本机客户端 jar 里挖：那份材质和游戏里看到的一模一样
        if (TryExtractFromJar(gameDir, jarEntry, target))
        {
            if (!entry.IsConfigured) { entry.IsConfigured = true; return true; }
            return true;
        }

        // ② 程序集里内嵌的那一份 —— 单文件 exe 上唯一可靠的兜底
        var embedded = ReadEmbeddedSkin(bundledName);
        if (embedded is not null)
        {
            WriteAtomic(target, embedded);
            entry.IsConfigured = true;
            return true;
        }

        // ③ 开发机上 bin 目录旁边可能还留着松散文件，顺手用上
        var bundled = FindBundledSkin(bundledName);
        if (bundled is not null)
        {
            File.Copy(bundled, target, overwrite: true);
            entry.IsConfigured = true;
            return true;
        }

        // 什么都没有：标记成未配置，界面上显示「本地没有内置皮肤」而不是一个裂图
        if (entry.IsConfigured)
        {
            entry.IsConfigured = false;
            return true;
        }

        return false;
    }

    /// <summary>先写临时文件再改名，避免半截文件被当成可用皮肤。</summary>
    private static void WriteAtomic(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>删掉一个已知不可用的残留文件（0 字节 / 半截），别让它挡住后续重建。</summary>
    private static void TryDeleteBroken(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就算了，后面 WriteAtomic 会覆盖它
        }
    }

    /// <summary>
    /// 导入一张本地 PNG，**新增**一个自定义皮肤（不改动已有的）。
    /// 返回新建的那一项；调用方通常接着把它绑到账号上。
    /// </summary>
    public async Task<SkinEntry> AddAsync(
        SkinLibraryKind kind, string pngPath, SkinArmModel armModel, CancellationToken ct = default)
    {
        if (!File.Exists(pngPath))
            throw new FileNotFoundException("找不到选中的皮肤文件。", pngPath);

        var bytes = await File.ReadAllBytesAsync(pngPath, ct).ConfigureAwait(false);
        ValidatePng(bytes);

        var id = NewCustomId(kind);
        var entry = new SkinEntry
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(pngPath),
            Library = kind,
            ArmModel = armModel,
            FileName = $"{id}.png",
            IsCustomSlot = false,
            IsConfigured = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        WriteAtomic(PathOf(entry), bytes);

        lock (_gate) _entries.Add(entry);
        Save();
        Raise();
        return entry;
    }

    /// <summary>改一个自定义皮肤的名字（内置的不给改）。</summary>
    public void Rename(SkinEntry entry, string name)
    {
        if (entry.IsBuiltIn) return;

        entry.Name = string.IsNullOrWhiteSpace(name) ? "自定义皮肤" : name.Trim();
        Save();
        Raise();
    }

    /// <summary>
    /// 删掉一个自定义皮肤（连同 PNG 文件）。内置的不给删。
    /// 如果这个账号正好在用被删的那张，返回它原本绑的 id，让调用方去改绑；
    /// 没被用到就返回 null。
    /// </summary>
    public void Delete(SkinEntry entry)
    {
        if (entry.IsBuiltIn) return;

        try
        {
            var path = PathOf(entry);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件删不掉也要把索引里的记录去掉，界面上就不该再出现它
        }

        lock (_gate) _entries.RemoveAll(e => e.Id == entry.Id);
        Save();
        Raise();
    }

    /// <summary>
    /// 把老版本那个「唯一自定义槽」迁移成普通的一张自定义皮肤。
    ///
    /// 老数据形如 id=custom-offline、IsCustomSlot=true、可能还没导入过图。
    /// 有新 id 的条目一律不动；老条目里没导过图的（空槽）直接丢掉。
    /// </summary>
    private bool MigrateLegacyCustomSlot()
    {
        var changed = false;

        foreach (var kind in new[] { SkinLibraryKind.Offline, SkinLibraryKind.Microsoft })
        {
            var legacy = _entries.FirstOrDefault(
                e => string.Equals(e.Id, LegacyCustomId(kind), StringComparison.OrdinalIgnoreCase));

            if (legacy is null) continue;

            // 空槽（没导入过图）直接删掉，界面上本来也不显示内容
            if (!legacy.IsConfigured || !IsReady(legacy))
            {
                _entries.Remove(legacy);
                changed = true;
                continue;
            }

            // 有图：改成一张正常的自定义皮肤，id 保持原样（账号可能正绑着它）
            legacy.IsCustomSlot = false;
            legacy.Name = string.IsNullOrWhiteSpace(legacy.Name) ? "自定义皮肤" : legacy.Name;
            changed = true;
        }

        return changed;
    }

    private static void ValidatePng(byte[] bytes)
    {
        if (ReadPngSize(bytes) is not { } size)
            throw new InvalidOperationException("这不是一张有效的 PNG 图片。");

        if (size is not ((64, 64) or (64, 32)))
            throw new InvalidOperationException(
                $"皮肤尺寸不对：当前是 {size.Width}×{size.Height}，必须是 64×64（或老格式 64×32）。");
    }

    // ── 内部 ────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexFile)) return;
            var list = JsonSerializer.Deserialize<List<SkinEntry>>(File.ReadAllText(_indexFile), Json);
            if (list is not null) _entries = list;
        }
        catch
        {
            // 索引坏了就当从没有过，下一次 EnsureSeededAsync 会重建
            _entries = new List<SkinEntry>();
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            var temp = _indexFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
            File.Move(temp, _indexFile, overwrite: true);
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>从本机客户端 jar 里挖一张皮肤出来。挖不到返回 false。</summary>
    private static bool TryExtractFromJar(string gameDir, string entryName, string destPath)
    {
        try
        {
            var versionsRoot = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsRoot)) return false;

            // 优先挖和目录同名的 jar——那种是干净的原版客户端
            foreach (var dir in Directory.EnumerateDirectories(versionsRoot)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var id = Path.GetFileName(dir);
                var preferred = Path.Combine(dir, id + ".jar");
                if (File.Exists(preferred) && TryExtractEntry(preferred, entryName, destPath))
                    return true;

                foreach (var jar in Directory.EnumerateFiles(dir, "*.jar"))
                    if (TryExtractEntry(jar, entryName, destPath))
                        return true;
            }
        }
        catch
        {
            // 挖不动就往下走 bundled 兜底
        }

        return false;
    }

    private static bool TryExtractEntry(string jarPath, string entryName, string destPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry(entryName);
            if (entry is null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
            return File.Exists(destPath) && new FileInfo(destPath).Length > 64;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindBundledSkin(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Skins", fileName),
            Path.Combine(AppContext.BaseDirectory, "Skins", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Skins", fileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 从程序集里读内置皮肤（csproj 里按 EmbeddedResource 打进 LMCL.App）。
    ///
    /// 为什么必须有这条路：Release 是单文件发布，随包附带的松散 PNG 不会进 exe，
    /// 换一台电脑 <see cref="FindBundledSkin"/> 就什么都找不到。
    /// 这里按逻辑名在所有已加载程序集里找，不直接引用 LMCL.App ——
    /// Core 不该反向依赖界面程序集。
    /// </summary>
    private static byte[]? ReadEmbeddedSkin(string fileName)
    {
        var logicalName = $"LMCL.Skins.{fileName}";
        var name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                // 先按 LogicalName 精确找，找不到再按后缀模糊匹配
                var stream = assembly.GetManifestResourceStream(logicalName)
                             ?? assembly.GetManifestResourceNames()
                                 .Where(n => n.EndsWith($".{name}.png", StringComparison.OrdinalIgnoreCase))
                                 .Select(assembly.GetManifestResourceStream)
                                 .FirstOrDefault(s => s is not null);

                if (stream is null) continue;

                using (stream)
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    if (buffer.Length > 64) return buffer.ToArray();
                }
            }
            catch
            {
                // 某个程序集读不了不影响继续找下一个
            }
        }

        return null;
    }

    /// <summary>从 PNG 字节里直接读宽高（IHDR 在固定偏移上），不引入任何图像库。</summary>
    private static (int Width, int Height)? ReadPngSize(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24) return null;
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;
        if (bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R')
            return null;

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));

        return width > 0 && height > 0 ? (width, height) : null;
    }
}
