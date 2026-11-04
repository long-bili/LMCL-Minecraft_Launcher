using System.Diagnostics;
using System.Net.Sockets;

namespace LMCL.Core.Online;

/// <summary>一个内置公共中转节点。界面展示地区与提供者，不展示地址。</summary>
public sealed record SharedNode(string Id, string Url, string Provider, string Region, bool Limited = false);

/// <summary>
/// 内置公共中转节点与延迟测量。清单照 Campfire（GPL-3.0）公开源码的
/// PublicNodes 抄录 —— 这些是社区成员无偿提供的 EasyTier 共享节点，
/// LMCL 以 AGPL-3.0 开源，与其许可证兼容；联机双方须至少启用同一个节点。
/// </summary>
public static class SharedNodes
{
    public static IReadOnlyList<SharedNode> All { get; } =
    [
        new("sh-ct", "tcp://225284.xyz:11010", "海波", "上海 · 电信"),
        new("xm-ct", "tcp://easytier.weiai.org.cn:11010", "为爱，唯爱", "厦门 · 电信"),
        new("cq-cm", "tcp://183.230.36.171:11010", "Just the Two of Us", "重庆 · 移动"),
        new("zz-bgp", "udp://et.basd1.de:11010", "宇智波.希", "枣庄 · BGP"),
        new("sh-bgp", "tcp://et.sumnight.top:11010", "夏夜", "上海 · BGP", Limited: true),
        new("cn-eo", "wss://et.chinokou.cn", "ChinoKou", "国内 · EdgeOne"),
        new("yn-cu", "https://et-public.lctn.top", "凉城听暖", "云南 · 联通"),
        new("intl-cf", "wss://et.xn--6kr850c.com", "林然", "海外 · Cloudflare"),
        new("intl-wk", "https://etnode.zkitefly.eu.org/node2", "zkitefly", "海外 · Cloudflare"),
    ];
}

/// <summary>
/// 各内置节点的延迟测量：对节点地址做一次 TCP 连接计时（udp 节点同样连它的端口，
/// 只判「通不通 + 多快」，不追求与协议语义完全一致）。结果在内存里缓存，
/// 超过时效（10 分钟）再次展示时界面会提示重新测。
/// </summary>
public static class SharedNodeTester
{
    /// <summary>结果多久算新鲜；超过后界面展示时应重新测。</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);

    /// <summary>往返延迟高于这个值就算「慢」（照 Campfire 的 HighLatencyMs）。</summary>
    public const double HighLatencyMs = 150;

    private const int ConnectTimeoutMs = 3000;

    private static readonly object Sync = new();

    /// <summary>node.Id → 延迟毫秒；null = 测过但连不上。</summary>
    private static readonly Dictionary<string, double?> Latencies = new();

    /// <summary>上一轮完整测试的完成时间；还没测过是 null。</summary>
    public static DateTime? TestedAt { get; private set; }

    public static bool IsTesting { get; private set; }

    /// <summary>某个节点的测试状态有变化（或整体开始 / 结束测试）时触发；线程不定，界面侧自行调度。</summary>
    public static event EventHandler? Changed;

    /// <summary>测过才有结果；没测过返回 false（界面显示「未测试」）。</summary>
    public static bool TryGetLatency(SharedNode node, out double? latency)
    {
        lock (Sync)
        {
            var found = Latencies.TryGetValue(node.Id, out var value);
            latency = value;
            return found;
        }
    }

    /// <summary>并发测一轮所有节点；重复调用会等上一轮结束（直接返回）。</summary>
    public static Task TestAsync(IReadOnlyList<SharedNode> nodes)
    {
        lock (Sync)
        {
            if (IsTesting) return Task.CompletedTask;
            IsTesting = true;
        }
        Changed?.Invoke(null, EventArgs.Empty);

        return Task.Run(async () =>
        {
            try
            {
                using var gate = new SemaphoreSlim(4);
                var tasks = nodes.Select(async node =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var latency = await MeasureAsync(node).ConfigureAwait(false);
                        lock (Sync) Latencies[node.Id] = latency;
                        Changed?.Invoke(null, EventArgs.Empty);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
                TestedAt = DateTime.Now;
            }
            finally
            {
                lock (Sync) IsTesting = false;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        });
    }

    /// <summary>一次 TCP 连接的往返时间；连不上 / 超时返回 null。</summary>
    private static async Task<double?> MeasureAsync(SharedNode node)
    {
        if (!Uri.TryCreate(node.Url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;

        var port = uri.IsDefaultPort ? DefaultPort(uri.Scheme) : uri.Port;

        using var client = new TcpClient();
        var watch = Stopwatch.StartNew();
        try
        {
            var connect = client.ConnectAsync(uri.Host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(ConnectTimeoutMs)).ConfigureAwait(false);
            if (finished != connect || !client.Connected) return null;
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static int DefaultPort(string scheme) => scheme switch
    {
        "wss" or "https" => 443,
        "ws" or "http" => 80,
        _ => 11010, // tcp / udp 的 EasyTier 惯例端口
    };
}
