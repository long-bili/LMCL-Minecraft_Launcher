using CmlLib.Core;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Game;

/// <summary>
/// 给「补齐游戏文件」这一步加一层重试。
///
/// 并行安装器一旦在某个文件上出错，会把整批任务一起废掉并抛 AggregateException——
/// 哪怕 4043 个文件都好好的。而这类错误多半是瞬时的：杀软正在扫刚下下来的 jar、
/// 用户同时开了两个启动器、单个文件超时。已下好的文件下次会按大小与 SHA1 跳过，
/// 所以重试的代价很小，却能把「装到一半全崩」变成「自己缓过来」。
/// </summary>
internal static class ResilientInstaller
{
    public static async Task InstallAsync(
        MinecraftLauncher launcher, string version, ProgressReporter reporter,
        ILogSink? log, CancellationToken ct = default, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await launcher.InstallAsync(version, reporter, reporter, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                log?.Warn($"安装 {version} 第 {attempt} 次中断：{Root(ex).Message}");
                reporter.Status($"下载中断，正在重试（第 {attempt + 1}/{attempts} 次）…");

                // 给占用文件的进程一点时间松手，别立刻撞上去
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>文件被占、权限被抢、网络抖动、单文件超时——这些重试有意义；其余直接放出去。</summary>
    private static bool IsTransient(Exception? ex, int depth = 0)
    {
        if (ex is null || depth > 6) return false;

        switch (ex)
        {
            case IOException:
            case UnauthorizedAccessException:
            case HttpRequestException:
            case TaskCanceledException:      // 调用方真取消的情况上面已经挡掉了
                return true;
            case AggregateException aggregate:
                return aggregate.InnerExceptions.Any(e => IsTransient(e, depth + 1));
            default:
                return IsTransient(ex.InnerException, depth + 1);
        }
    }

    private static Exception Root(Exception ex)
    {
        var current = ex;
        while (current is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            current = aggregate.InnerExceptions[0];
        while (current.InnerException is not null) current = current.InnerException;
        return current;
    }
}
