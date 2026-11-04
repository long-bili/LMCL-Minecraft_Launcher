using System.Collections.Concurrent;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Services;

/// <summary>
/// 日志总线：Core 层只管往这里丢，UI 层订阅后自己决定显示在哪。
/// 同时保留一份环形缓冲，方便「导出日志」和启动时回填。
/// </summary>
public sealed class LogBus : ILogSink
{
    /// <summary>UI 侧的日志面板撑不住太多行，只留最近这么多条。</summary>
    public const int Capacity = 600;

    private readonly ConcurrentQueue<LogEntry> _recent = new();

    /// <summary>注意：会在后台线程触发，订阅方需自行切回 UI 线程。</summary>
    public event Action<LogEntry>? EntryWritten;

    public event Action? Cleared;

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public void Write(LogEntry entry)
    {
        _recent.Enqueue(entry);
        while (_recent.Count > Capacity && _recent.TryDequeue(out _)) { }
        EntryWritten?.Invoke(entry);
    }

    public void Clear()
    {
        while (_recent.TryDequeue(out _)) { }
        Cleared?.Invoke();
    }

    /// <summary>把日志写到磁盘，供导出与崩溃排查使用。</summary>
    public string DumpToFile(string? path = null)
    {
        Core.Configuration.AppPaths.EnsureCreated();
        path ??= Path.Combine(Core.Configuration.AppPaths.LogDir,
            $"lmcl-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        File.WriteAllLines(path, _recent.Select(e => e.ToString()));
        return path;
    }
}
