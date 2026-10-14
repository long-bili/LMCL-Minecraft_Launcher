using CmlLib.Core;
using CmlLib.Core.Installers;

namespace LMCL.Core.Diagnostics;

/// <summary>
/// 把 CmlLib 的两种进度回调（按文件数 / 按字节数）归一成 UI 能直接用的形式。
///
/// 两者会互相抢进度条，所以这里定了个优先级：只要收到过字节进度，就以字节为准
/// （字节进度才反映真实下载量——Forge 处理器那种没有字节进度的阶段才退化成文件计数）。
/// </summary>
public sealed class ProgressReporter : IProgress<InstallerProgressChangedEventArgs>, IProgress<ByteProgress>
{
    private readonly ILogSink? _log;
    private readonly object _gate = new();
    private DateTime _lastByteReport = DateTime.MinValue;
    private bool _sawByteProgress;

    public ProgressReporter(ILogSink? log = null) => _log = log;

    /// <summary>0~1；-1 表示进度不确定。</summary>
    public Action<double>? RatioChanged { get; init; }

    public Action<string>? StatusChanged { get; init; }

    /// <summary>已完成数、总数、当前文件名。</summary>
    public Action<int, int, string>? FilesChanged { get; init; }

    public void Status(string text) => StatusChanged?.Invoke(text);

    public void Info(string message) => _log.Info(message);

    public void Success(string message) => _log.Success(message);

    public void Warn(string message) => _log.Warn(message);

    public void Error(string message) => _log.Error(message);

    public void ResetRatio() => RatioChanged?.Invoke(-1);

    void IProgress<InstallerProgressChangedEventArgs>.Report(InstallerProgressChangedEventArgs value)
    {
        FilesChanged?.Invoke(value.ProgressedTasks, value.TotalTasks, value.Name ?? "");

        if (_sawByteProgress || value.TotalTasks <= 0) return;
        RatioChanged?.Invoke(Math.Clamp((double)value.ProgressedTasks / value.TotalTasks, 0, 1));
    }

    void IProgress<ByteProgress>.Report(ByteProgress value)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastByteReport).TotalMilliseconds < 80) return;   // 节流，别把 UI 刷爆
            _lastByteReport = now;
            _sawByteProgress = true;
        }

        var ratio = value.ToRatio();
        RatioChanged?.Invoke(double.IsNaN(ratio) ? -1 : Math.Clamp(ratio, 0, 1));
    }
}
