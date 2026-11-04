using System.Diagnostics;
using System.Text.RegularExpressions;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Java;

public sealed record JavaInfo(string Path, string Home, int Major, string Version, string Source)
{
    public string Display => $"{Major}";

    public string Detail => string.IsNullOrWhiteSpace(Version) ? $"Java {Major}" : $"Java {Major} · {Version}";

    public override string ToString() => $"{Detail}  ({Path})";
}

/// <summary>
/// Java 识别。三条来源：启动器配置 → Mojang 内置运行时 → 本机安装的 JDK。
///
/// 版本号优先读 JDK 目录里的 <c>release</c> 文件（零进程开销），读不到才退回
/// 跑一次 <c>java -version</c>。
/// </summary>
public sealed class JavaService
{
    private static readonly Regex ReleaseRegex = new(@"JAVA_VERSION\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex VersionLineRegex = new(@"version\s+""([^""]+)""", RegexOptions.Compiled);

    private static readonly string[] CommonRoots =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Eclipse Foundation",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\IBM\Semeru",
        @"C:\Program Files\JetBrains",
        @"C:\Program Files\Android\Android Studio\jbr",
        @"D:\Program Files\Java",
        @"D:\Program Files\Eclipse Adoptium",
        @"D:\Java",
        @"D:\java",
    };

    private readonly ConfigService _config;
    private readonly ILogSink? _log;
    private readonly object _gate = new();
    private List<JavaInfo> _cache = new();

    public JavaService(ConfigService config, ILogSink? log = null)
    {
        _config = config;
        _log = log;
    }

    public IReadOnlyList<JavaInfo> Cached
    {
        get { lock (_gate) return _cache.ToArray(); }
    }

    /// <summary>扫盘找 Java。会启动少量子进程，请勿在 UI 线程直接调用。</summary>
    public async Task<IReadOnlyList<JavaInfo>> DetectAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!force && _cache.Count > 0) return _cache.ToArray();
        }

        var found = await Task.Run(() => Scan(ct), ct).ConfigureAwait(false);

        lock (_gate) _cache = found.ToList();

        _log?.Info(found.Count == 0
            ? "未检测到任何 Java"
            : $"检测到 {found.Count} 个 Java：{string.Join("、", found.Select(j => $"Java {j.Major}"))}");

        return found;
    }

    private List<JavaInfo> Scan(CancellationToken ct)
    {
        var results = new List<JavaInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? exe, string source)
        {
            if (string.IsNullOrWhiteSpace(exe) || ct.IsCancellationRequested) return;
            string full;
            try { full = Path.GetFullPath(exe); } catch { return; }
            if (!File.Exists(full) || !seen.Add(full)) return;

            var home = Directory.GetParent(Path.GetDirectoryName(full)!)?.FullName ?? "";
            var info = Probe(full, home, source);
            if (info is not null) results.Add(info);
        }

        // 1) 配置里手工指定的
        if (!string.IsNullOrWhiteSpace(_config.Current.JavaPath))
            Add(_config.Current.JavaPath, "手动指定");

        // 2) 启动器游戏目录下由 Mojang 运行时下载的 JRE
        var runtimeDir = Path.Combine(_config.GameDir, "runtime");
        foreach (var exe in EnumerateJavaExes(runtimeDir, maxDepth: 5, limit: 400))
            Add(exe, "Mojang 运行时");

        // 3) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            Add(Path.Combine(javaHome!, "bin", "java.exe"), "JAVA_HOME");

        // 4) 常见安装目录
        foreach (var exe in EnumerateJavaExes(CommonRoots, maxDepth: 3, limit: 800))
            Add(exe, "本机安装");

        // 5) PATH
        foreach (var exe in EnumerateFromPath())
            Add(exe, "PATH");

        return results
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var dir in path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "java.exe");
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateJavaExes(string root, int maxDepth, int limit)
        => EnumerateJavaExes(new[] { root }, maxDepth, limit);

    private static IEnumerable<string> EnumerateJavaExes(string[] roots, int maxDepth, int limit)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (var root in roots)
            if (Directory.Exists(root)) queue.Enqueue((root, 0));

        var visited = 0;
        while (queue.Count > 0 && visited < limit)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;

            var candidate = Path.Combine(dir, "bin", "java.exe");
            if (File.Exists(candidate)) yield return candidate;

            if (depth >= maxDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var child in children) queue.Enqueue((child, depth + 1));
        }
    }

    private JavaInfo? Probe(string exe, string home, string source)
    {
        var version = ReadReleaseVersion(home) ?? RunJavaVersion(exe) ?? "";
        var major = ParseMajor(version);
        if (major <= 0) return null;

        return new JavaInfo(exe, home, major, version, source);
    }

    /// <summary>读 JDK 目录里的 release 文件，比启动进程快得多。</summary>
    public static string? ReadReleaseVersion(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        var file = Path.Combine(home, "release");
        if (!File.Exists(file)) return null;
        try
        {
            var match = ReleaseRegex.Match(File.ReadAllText(file));
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    private string? RunJavaVersion(string exe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            var error = process.StandardError.ReadToEnd();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000)) { try { process.Kill(true); } catch { } }

            var match = VersionLineRegex.Match(error + "\n" + output);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>把 "1.8.0_401" / "17.0.2" / "21" 归一成主版本号。</summary>
    public static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var text = version.Trim().Split('-', '+')[0];
        var parts = text.Split('.');
        if (parts.Length == 0) return 0;

        if (!int.TryParse(parts[0], out var first)) return 0;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second)) return second;
        return first;
    }

    /// <summary>挑最合适的 Java：优先「刚好满足要求」的最小版本，没有才退而求其次。</summary>
    public JavaInfo? Best(int? requiredMajor)
    {
        var list = Cached;
        if (list.Count == 0) return null;

        if (requiredMajor is int need && need > 0)
        {
            var satisfying = list.Where(j => j.Major >= need)
                                 .OrderBy(j => j.Major - need)
                                 .ThenByDescending(j => j.Major)
                                 .ToArray();
            if (satisfying.Length > 0) return satisfying[0];

            _log?.Warn($"没有满足要求的 Java {need}，改用已有的 Java {list[0].Major}");
            return list[0];
        }

        return list.OrderByDescending(j => j.Major).First();
    }
}
