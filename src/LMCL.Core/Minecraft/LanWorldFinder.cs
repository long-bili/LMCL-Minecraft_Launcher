using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LMCL.Core.Minecraft;

/// <summary>
/// 等待本机的「对局域网开放」：读系统的 TCP 监听表，挑出 Java 进程持有的端口，
/// 逐个用传统服务器列表 ping（0xFE）探测 —— 新旧所有 Java 版服务器都会回 0xFF。
/// 独立实现照 Campfire（GPL-3.0）公开源码的 LocalWorldFinder / MinecraftProbe，
/// LMCL 以 AGPL-3.0 开源，许可证兼容。
/// </summary>
public static class LanWorldFinder
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 在限定时间内找一个本机正在运行的 Minecraft 世界 / 服务器的端口。
    /// 找到返回端口号；超时返回 null。
    /// </summary>
    public static async Task<int?> WaitForWorldAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var rejected = new Dictionary<int, DateTime>();

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var port in GetJavaListeningPorts())
            {
                // 20 秒内探测失败过的端口先跳过，别一直敲同一扇没人的门
                if (rejected.TryGetValue(port, out var since) && DateTime.UtcNow - since < TimeSpan.FromSeconds(20))
                    continue;
                if (await IsServerAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    return port;
                rejected[port] = DateTime.UtcNow;
            }

            var remain = deadline - DateTime.UtcNow;
            if (remain <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1500, remain.TotalMilliseconds)), ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>这个端口上是不是有一个 Minecraft 服务器（含局域网世界）在应答。</summary>
    public static async Task<bool> IsServerAsync(int port, TimeSpan timeout)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            var exchange = ExchangeAsync(client, port);
            if (await Task.WhenAny(exchange, Task.Delay(timeout)).ConfigureAwait(false) != exchange)
                return false;
            return await exchange.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            return false;
        }
    }

    private static async Task<bool> ExchangeAsync(TcpClient client, int port)
    {
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0xFE }, 0, 1).ConfigureAwait(false);
        var reply = new byte[1];
        return await stream.ReadAsync(reply, 0, 1).ConfigureAwait(false) == 1 && reply[0] == 0xFF;
    }

    /// <summary>本机所有「Java 进程正在监听」的 TCP 端口（自检也用它做离线断言）。</summary>
    public static IReadOnlyList<int> GetJavaListeningPorts()
    {
        var ports = new SortedSet<int>();
        var names = new Dictionary<int, bool>();

        foreach (var (port, pid) in ReadListeners(AfInet, TcpRowListener).Concat(ReadListeners(AfInet6, TcpRowListener6)))
        {
            if (!names.TryGetValue(pid, out var isJava))
            {
                isJava = IsJava(pid);
                names[pid] = isJava;
            }
            if (isJava)
                ports.Add(port);
        }

        return ports.ToList();
    }

    private static bool IsJava(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>读 IP Helper 的监听表，拿 (本地端口, 所属进程) 对。</summary>
    private static IEnumerable<(int Port, int Pid)> ReadListeners(int family, int rowSize)
    {
        // 行内布局：IPv4 行本地端口在 8 字节处、PID 在 20 字节处；IPv6 行分别是 20 / 52。
        var (portOffset, pidOffset) = family == AfInet ? (8, 20) : (20, 52);

        var result = new List<(int, int)>();
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);

        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var table = Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != 0) break;

                var count = Marshal.ReadInt32(table);
                for (var i = 0; i < count; i++)
                {
                    var row = table + 4 + i * rowSize;
                    var rawPort = Marshal.ReadInt32(row, portOffset);
                    // 端口低 16 位按网络字节序存
                    var port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                    result.Add((port, Marshal.ReadInt32(row, pidOffset)));
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        return result;
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const int TcpRowListener = 24;   // IPv4 监听行大小
    private const int TcpRowListener6 = 56;  // IPv6 监听行大小

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
