using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Game;

/// <summary>
/// 在交给并行安装器之前，按「目标路径」去重。
///
/// 原版的资产索引里同一个物理对象会被多个名字引用——1.20.1 就有 23 组，
/// 于是文件清单里出现两条 GameFile 指向同一个 assets/objects/xx/hash。
/// 并行安装器把它们分给不同线程，结果一个在写（File.Create 独占打开）
/// 另一个在校验（File.OpenRead），必然撞车抛 IOException；而这个异常会冒泡成
/// AggregateException 把整次安装打断，排在资产后面的客户端 jar 就永远下不到——
/// 表现就是「版本看起来装好了，一启动却说找不到主类」。
///
/// 去重之后每个路径只剩一个任务，问题从根上消失，顺带少做一次多余的 SHA1。
/// 库里自带 <c>GameFilePathComparer</c> 说明上游也清楚这个情况，只是没在安装器里用。
/// </summary>
internal sealed class DedupedGameInstaller : IGameInstaller
{
    private readonly IGameInstaller _inner;
    private readonly ILogSink? _log;

    public DedupedGameInstaller(IGameInstaller inner, ILogSink? log = null)
    {
        _inner = inner;
        _log = log;
    }

    public ValueTask Install(
        IEnumerable<GameFile> gameFiles,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<GameFile>();
        var skipped = 0;

        foreach (var file in gameFiles)
        {
            // 没有目标路径的条目不该出现在这里，但真出现了也别自作主张丢掉
            if (string.IsNullOrEmpty(file.Path))
            {
                unique.Add(file);
                continue;
            }

            if (seen.Add(file.Path!)) unique.Add(file);
            else skipped++;
        }

        if (skipped > 0)
            _log?.Debug($"安装前去重：合并了 {skipped} 个指向同一路径的条目（{unique.Count} 个文件待处理）");

        return _inner.Install(unique, fileProgress, byteProgress, cancellationToken);
    }
}
