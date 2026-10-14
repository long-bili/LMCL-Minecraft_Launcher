using System.Net;
using System.Net.Http;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Http;

/// <summary>统一构造 HttpClient，保证整个程序只有一套超时/重试/镜像策略。</summary>
public static class HttpClients
{
    public const string UserAgent = "LMCL/1.0 (MinecraftLauncher)";

    /// <summary>
    /// 注意：HttpClient.Timeout 覆盖的是「整个 SendAsync」，所以它同时给内部的重试
    /// 划了总时长上限——单个文件最多 5 分钟，超了就失败重来（已下好的文件会被跳过）。
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static HttpClient Create(bool useMirror, ILogSink? log = null)
    {
        var sockets = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
            AllowAutoRedirect = true,
            // 二进制下载不做自动解压：避免任何影响 SHA1 校验的意外
            AutomaticDecompression = DecompressionMethods.None,
        };

        var handler = new MirrorHttpHandler(sockets)
        {
            UseMirror = useMirror,
            Log = message => log?.Write(new LogEntry(DateTime.Now, LogLevel.Debug, message)),
        };

        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
