using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;

namespace LMCL.Core.EasyTier;

/// <summary>
/// EasyTier 联机的一站式管理：下载、启动、停止、读状态。
///
/// ── 为什么选它 ──────────────────────────────────────────
/// Minecraft 原生只支持「同一局域网」联机；两人不在同一个网里就得开服务器或打洞工具。
/// EasyTier 用虚拟网卡把两台异地机器拉进同一个虚拟局域网（默认 10.126.126.0/24），
/// 之后主机开「对局域网开放」、其他人直连主机的虚拟 IP 即可 —— 不需要真开服务器。
///
/// ── 怎么接 ──────────────────────────────────────────────
/// EasyTier 是 Rust 写的独立 exe，**没有启动器 SDK**。
/// 集成方式就是「下载 zip → 解压 → 起子进程 → 读它的输出 / 用 easytier-cli 查状态」，
/// 和本项目已有的 authlib-injector（下载 jar → 拼 JVM 参数）是同一类活。
///
/// 启动参数（官方 quick-networking 文档）：
///   easytier-core.exe -d --network-name &lt;名&gt; --network-secret &lt;密码&gt; -p tcp://&lt;共享节点&gt;:11010
///   -d       虚拟 IP 自动分配（DHCP）
///   --network-name / --network-secret   相同才能进同一个网（名 + 密码就是「房间号」）
///   -p       共享节点（没有公网 IP 时靠它中转 / 帮忙打洞）
///
/// ── 权限 ────────────────────────────────────────────────
/// 建虚拟网卡（TUN）需要管理员权限。这里用 runas 提权拉起，
/// UAC 弹窗由用户确认；拒绝时降级为提示，不阻断启动器本身。
/// </summary>
public sealed class EasyTierService
{
    /// <summary>EasyTier 当前稳定版。升级时改这一个字段即可。</summary>
    public const string Version = "2.6.4";

    /// <summary>默认虚拟网段。EasyTier 的 DHCP 在这段里自动分配虚拟 IP。</summary>
    public const string DefaultVirtualSubnet = "10.126.126.0/24";

    /// <summary>
    /// 管理门户的固定端口。
    ///
    /// **必须显式指定**：不指定时 rpc-portal 是「随机端口，先尝试 15888」——
    /// 一旦 15888 被别的程序占了，`easytier-cli` 就查不到任何状态
    /// （表现为「peer 列表永远是空的」），而界面会把这当成「没连上」。
    /// 固定成 15888 并让 cli 用同一个端口，状态查询才可靠。
    /// </summary>
    public const int RpcPortal = 15888;

    /// <summary>管理门户的完整地址，供 easytier-cli --rpc-portal 使用。</summary>
    public static readonly string RpcPortalAddr = "127.0.0.1:" + RpcPortal;

    /// <summary>
    /// 默认公共共享节点。没有公网 IP 时靠它们中转 / 帮忙打洞。
    ///
    /// **配多个是必须的**：单个节点挂了（或被本地网络挡住）就直接连不上，
    /// 而「异地连不上」最常见的诱因就是共享节点不可达。
    /// 官方文档明确说「为提升可用性，可同时连接多个共享节点，只需指定多个 -p」，
    /// 并且建议所有节点使用**相同**的列表 —— 否则 A 只连得上节点 1、
    /// B 只连得上节点 2，而两个节点之间没打通，两人就永远遇不上。
    ///
    /// ⚠ 这些是「默认值」：官方公共节点**会失效**（2026-10 实测
    /// public.easytier.cn 已 NXDOMAIN、11010 全部超时）。
    /// 所以列表允许被配置覆盖（<c>MpSharedNodes</c>），
    /// 官方挂了时用户可换自建 / 社区节点，不用发版。
    /// </summary>
    public static readonly string[] DefaultSharedNodes =
    [
        "tcp://public.easytier.top:11010",
        "udp://public.easytier.top:11010",
    ];

    /// <summary>
    /// 取实际要用的共享节点列表：配置里有就用配置的，否则用默认。
    /// 过滤掉空行和写错的（不含 :// 的），防手滑。
    /// </summary>
    public static IReadOnlyList<string> EffectiveSharedNodes(IReadOnlyList<string>? configured)
    {
        if (configured is { Count: > 0 })
        {
            var valid = configured
                .Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains("://", StringComparison.Ordinal))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (valid.Count > 0) return valid;
        }

        return DefaultSharedNodes;
    }

    /// <summary>
    /// 实际要用的节点全集：勾选的内置社区节点（见 SharedNodes.All）+ 自定义节点
    /// （MpSharedNodes；空则回落到官方默认节点）。去重保序。
    /// </summary>
    public IReadOnlyList<string> EffectiveNodes()
    {
        var list = new List<string>();

        var enabled = _config.Current.MpEnabledNodes;
        if (enabled is null)
            list.AddRange(Online.SharedNodes.All.Select(n => n.Url));
        else
            list.AddRange(Online.SharedNodes.All
                .Where(n => enabled.Contains(n.Id, StringComparer.Ordinal))
                .Select(n => n.Url));

        list.AddRange(EffectiveSharedNodes(_config.Current.MpSharedNodes));

        return list
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains("://", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ConfigService _config;
    private readonly ILogSink? _log;

    private Process? _process;

    public EasyTierService(ConfigService config, ILogSink? log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>easytier-core.exe 的本地安装路径。</summary>
    public static string CorePath => Path.Combine(AppPaths.EasyTierDir, "easytier-core.exe");

    /// <summary>easytier-cli.exe 的本地安装路径。</summary>
    public static string CliPath => Path.Combine(AppPaths.EasyTierDir, "easytier-cli.exe");

    /// <summary>是否已安装（两个 exe 都在）。</summary>
    public static bool IsInstalled => File.Exists(CorePath) && File.Exists(CliPath);

    /// <summary>
    /// 当前本机是不是有一张虚拟网在跑。
    ///
    /// **按实际进程判断，而不是只看自己那个句柄**：
    /// 用户可能上次没退干净（提权进程普通 Kill 杀不掉），
    /// 也可能自己另外开了一个 EasyTier。这两种情况下虚拟网是活的，
    /// 只认自己句柄的话页面会显示「还没加入」，
    /// 用户一点建房就撞端口冲突（10048），而界面完全不解释为什么。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (_process is { HasExited: false }) return true;

            // 自己那个没了（或没启动过），看看系统里还有没有
            try
            {
                return Process.GetProcessesByName("easytier-core").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    // ── 下载 ────────────────────────────────────────────────

    /// <summary>
    /// 确保 EasyTier 已就位。已装则直接返回；没有就下载官方 zip 并解压。
    /// 走 gh-proxy 镜像链（GitHub 在国内直连不稳），失败逐个回退。
    /// 返回 true 表示可用。
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            _log?.Info($"EasyTier 已就位：{CorePath}");
            return true;
        }

        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.EasyTierDir);

        // GitHub 直连在国内不稳，逐个镜像尝试（官方下载页给出的加速链）
        var file = $"easytier-windows-x86_64-v{Version}.zip";
        var urls = new[]
        {
            $"https://github.com/EasyTier/EasyTier/releases/download/v{Version}/{file}",
            $"https://ghfast.top/https://github.com/EasyTier/EasyTier/releases/download/v{Version}/{file}",
            $"https://gh-proxy.org/https://github.com/EasyTier/EasyTier/releases/download/v{Version}/{file}",
        };

        var zip = Path.Combine(AppPaths.EasyTierDir, file);
        var ok = false;

        foreach (var url in urls)
        {
            try
            {
                _log?.Info($"正在下载 EasyTier {Version}…");
                await DownloadAsync(url, zip, progress, ct).ConfigureAwait(false);

                // zip 里可能有子目录，解压后把 exe 归位到 EasyTierDir
                using var archive = ZipFile.OpenRead(zip);
                var entries = archive.Entries
                    .Where(e => e.Name is "easytier-core.exe" or "easytier-cli.exe")
                    .ToList();

                if (entries.Count == 0)
                    throw new InvalidOperationException("压缩包里没有 easytier-core.exe。");

                foreach (var entry in entries)
                {
                    var target = Path.Combine(AppPaths.EasyTierDir, entry.Name);
                    entry.ExtractToFile(target, overwrite: true);
                }

                // **驱动和依赖 DLL 必须一起抽出来**，不能只挑那两个 exe。
                // 踩过的坑：原来只解压 core/cli，漏掉 wintun.dll，
                // 结果 easytier-core 一启动就 0xC0000135（STATUS_DLL_NOT_FOUND）秒退 ——
                // 而 runas 提权拿不到进程输出，界面上只看到「已启动」然后永远等不到虚拟 IP，
                // 极难定位。这里改成把 zip 里的**其他文件全都铺平**到安装目录。
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0) continue;          // 目录项
                    if (entry.Name is "easytier-core.exe" or "easytier-cli.exe") continue; // 上面已处理

                    // web / web-embed 是自建控制台用的，桌面联机用不到，省 45 MB
                    if (entry.Name.StartsWith("easytier-web", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var target = Path.Combine(AppPaths.EasyTierDir, entry.Name);
                    entry.ExtractToFile(target, overwrite: true);
                }

                ok = true;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Warn($"从 {url} 下载 EasyTier 失败：{ex.Message}");
            }
        }

        try { File.Delete(zip); } catch { /* 删不掉就算了 */ }

        if (ok) _log?.Success($"EasyTier {Version} 已就位：{AppPaths.EasyTierDir}");
        return ok;
    }

    private static async Task DownloadAsync(
        string url, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = HttpClients.Create(useMirror: false, log: null);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var temp = target + ".downloading";

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = File.Create(temp))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }
        }

        File.Move(temp, target, overwrite: true);
    }

    // ── 启动 / 停止 ─────────────────────────────────────────

    /// <summary>
    /// 加入一个虚拟网。
    ///
    /// 建虚拟网卡需要管理员权限，所以用 runas 提权。代价是拿不到子进程的输出，
    /// 因此状态确认走 easytier-cli。
    ///
    /// ── 房主与客人必须用**不同**的地址模式 ──────────────────
    /// 这是「异地连不上」的真正原因（踩过一次大坑）：
    ///
    /// 之前**所有人**都用 <c>-i</c> 指定一个按「网络名+机器名」散列出来的固定 IP。
    /// <c>-i</c> 没有任何冲突消解 —— 两个用户散列到同一个地址时，
    /// EasyTier 认为地址已被占用，虚拟网根本建不起来，表现就是「怎么也连不上」。
    /// 而 253 个地址的哈希空间 + 大量相似的机器名（DESKTOP-XXXX），撞车远比想象的容易。
    ///
    /// EasyTier 官方给 <c>-d</c>（DHCP）的说明是「由 EasyTier 自动确定并设置 IP，
    /// **如果网络中出现 IP 冲突，IP 将自动更改**」—— 这正是多人场景要的。
    /// 但实测 DHCP 在**网内没有任何其他节点**时拿不到地址（没人应答），
    /// 而「第一个人建房」恰恰是最常见的场景。
    ///
    /// 所以分工：
    ///   · **房主**用 <c>-i</c> 钉死一个固定地址（<see cref="HostVirtualIp"/>），
    ///     它就是网内的锚点，也是 DHCP 能分配的前提；
    ///   · **客人**用 <c>-d</c> 自动分配，撞了会自动改，绝不会和房主冲突。
    ///
    /// 返回 false 表示没能启动（用户拒了 UAC / 缺文件）。
    /// </summary>
    /// <param name="isHost">房主 = true（固定地址）；客人 = false（DHCP 自动分配）。</param>
    /// <param name="playerName">
    /// 玩家名，作为 EasyTier 的 <c>--hostname</c> 传进虚拟网 ——
    /// peer 列表和魔法 DNS 里显示的都是它，这样成员列表才能认出「这是谁」。
    /// 空则省略参数，EasyTier 回落到系统主机名。
    /// </param>
    public bool Start(string networkName, string networkSecret, bool isHost = true, string playerName = "")
    {
        if (IsRunning) return true;
        if (!IsInstalled) return false;

        // EasyTier 会在 AppPaths.EasyTierDir 里写运行数据，先确保存在
        Directory.CreateDirectory(AppPaths.EasyTierDir);

        var args = new StringBuilder();

        if (isHost)
        {
            // 房主：固定地址做锚点。用 .1 而不是散列值 ——
            // 房主是唯一的一个，不需要散列；而且 .1 是 DHCP 池的起点，
            // 让客人的自动分配从 .1 之后开始，天然避开房主。
            args.Append($"-i {HostVirtualIp} ");
        }
        else
        {
            // 客人：交给 DHCP。它会在冲突时自动换地址（官方明确保证），
            // 这是多人场景唯一安全的分配方式。
            args.Append("-d ");
        }

        // 玩家名 → --hostname：peer 列表里显示「这是谁」。
        // 名字会被拼进命令行，引号和空白必须挡掉。
        var name = playerName.Trim();
        if (name.Length > 0)
            args.Append($"--hostname \"{Escape(name)}\" ");

        args.Append($"--network-name \"{Escape(networkName)}\" ");
        args.Append($"--network-secret \"{Escape(networkSecret)}\" ");

        // 共享节点：内置社区节点（勾选的）+ 自定义节点。**双方必须用同一份列表**，
        // 否则 A 只连得上节点 1、B 只连得上节点 2，两人永远遇不上。
        foreach (var node in EffectiveNodes())
            args.Append($"-p {node} ");

        // 固定管理门户：cli 查状态时要用同一个端口（见 RpcPortal 注释）。
        // 加 --file-log-dir：EasyTier 内部日志落到安装目录，联机排障时能直接翻 ——
        // runas 提权拿不到 stdout，这几乎是唯一的现场证据来源。
        args.Append($"-r {RpcPortal} ");
        args.Append($"--file-log-dir \"{Escape(AppPaths.EasyTierDir)}\" ");

        // TUN 需要管理员权限：runas 提权。输出拿不到，状态走 cli 查
        var startInfo = new ProcessStartInfo
        {
            FileName = CorePath,
            Arguments = args.ToString(),
            UseShellExecute = true,
            Verb = "runas",                                   // 触发 UAC
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppPaths.EasyTierDir,
        };

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 里点了「否」
            _log?.Warn("EasyTier 需要管理员权限来创建虚拟网卡，但你取消了授权。");
            _process = null;
            return false;
        }
        catch (Exception ex)
        {
            _log?.Warn($"EasyTier 启动失败：{ex.Message}");
            _process = null;
            return false;
        }

        if (_process is null)
        {
            _log?.Warn("EasyTier 没能启动（拿不到进程句柄）。");
            return false;
        }

        // **必须确认它没秒退**。
        // runas 提权拿不到子进程的输出，所以进程要是立刻挂了（比如缺 wintun.dll
        // 导致 0xC0000135），这里不检查的话界面会一直显示「正在启动虚拟网卡…」，
        // 用户永远等不到虚拟 IP，却看不到任何错误 —— 这个坑踩过一次。
        Thread.Sleep(1200);

        if (_process.HasExited)
        {
            var code = _process.ExitCode;
            _log?.Warn($"EasyTier 启动后立刻退出（退出码 0x{code:X8}）。");

            LastError = code switch
            {
                unchecked((int)0xC0000135) =>
                    "缺少 wintun.dll 等依赖文件。删掉安装目录重下即可。",
                unchecked((int)0xC0000005) =>
                    "启动时访问冲突，通常是虚拟网卡驱动与系统不兼容。",
                1 => "启动被拒绝。若是端口被占用，先关掉已在运行的 EasyTier 再试。",
                _ => $"退出码 0x{code:X8}。",
            };

            _process = null;
            return false;
        }

        LastError = null;
        _log?.Success($"EasyTier 已启动（网络：{networkName}）");
        return true;
    }

    /// <summary>上一次启动失败的原因，供界面显示。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 停掉本机正在跑的 EasyTier。
    ///
    /// **不能只用 Process.Kill()**：我们是用 runas 提权把它拉起来的，
    /// 提权进程的权限比启动器高，普通权限的 Kill 会直接 Access denied ——
    /// 结果是「关了启动器，EasyTier 还在后台跑、端口一直被占」，
    /// 下次再创建房间就会撞上 10048（端口占用）起不来。这个坑踩过一次。
    ///
    /// 所以顺序是：先试普通 Kill；不行再用 runas 提权 taskkill。
    /// </summary>
    public void Stop()
    {
        var denied = false;

        // 优先只动我们自己拉起来的那个；没有自己句柄时（上次没退干净的残留、
        // 或用户手动开的）才把系统里剩的都收掉 —— 因为用户点的是「退出房间」，
        // 意图就是要断开，而残留进程正是下次建房撞 10048 的元凶。
        var targets = _process is { HasExited: false } mine
            ? Process.GetProcessesByName("easytier-core")
                .Where(p => p.Id == mine.Id)
                .ToList()
            : Process.GetProcessesByName("easytier-core").ToList();

        if (targets.Count == 0)
        {
            _process = null;
            return;
        }

        foreach (var process in targets)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                denied = true;
                _log?.Warn($"直接结束 EasyTier 被拒（{ex.GetType().Name}），改用提权结束。");
            }
            finally
            {
                process.Dispose();
            }
        }

        _process = null;

        if (denied) KillElevated();
    }

    /// <summary>用管理员权限 taskkill 清掉残留的 easytier-core（UAC 会弹一次）。</summary>
    private void KillElevated()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/IM easytier-core.exe /F",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var killer = Process.Start(startInfo);
            killer?.WaitForExit(10000);

            _log?.Info("已用管理员权限结束 EasyTier。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log?.Warn("需要管理员权限才能结束 EasyTier，但你取消了授权 —— " +
                       "它可能还在后台运行，下次联机可能因端口被占用而失败。");
        }
        catch (Exception ex)
        {
            _log?.Warn($"结束 EasyTier 失败：{ex.Message}");
        }
    }

    private static string Escape(string value) =>
        value.Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// 房主的虚拟 IP。**固定值，不再按机器名散列。**
    ///
    /// 散列方案是「异地连不上」的元凶：<c>-i</c> 没有冲突消解，
    /// 两个用户散列到同一个地址时虚拟网直接建不起来。
    /// 现在房主固定 <c>.1</c>（整个房间只有一个房主，不存在相互冲突），
    /// 客人一律走 DHCP（<c>-d</c>，官方保证冲突时自动改地址），
    /// 从 .1 之后开始分配，天然避开房主。
    ///
    /// 这个地址也是客人连 Minecraft 时要填的那个。
    /// </summary>
    public const string HostVirtualIp = "10.126.126.1";

    // ── 状态查询 ────────────────────────────────────────────

    /// <summary>
    /// 本机在虚拟网里的信息（含分配到的虚拟 IP）。
    /// 通过 easytier-cli node 查询，解析它的表格输出。
    /// 没跑起来 / 还没连上时返回 null。
    /// </summary>
    public async Task<EasyTierNodeInfo?> GetNodeInfoAsync(CancellationToken ct = default)
    {
        if (!IsRunning || !File.Exists(CliPath)) return null;

        var output = await RunCliAsync("node", ct).ConfigureAwait(false);
        return EasyTierNodeInfo.Parse(output);
    }

    /// <summary>
    /// 本机的公网出口与 NAT 类型（<c>easytier-cli node</c> 里的 Public IP / UDP Stun Type）。
    ///
    /// 这两个值是判断「为什么连不上」的关键线索：
    ///   · Public IP 为空 → 本机没拿到公网映射，只能靠共享节点中转；
    ///   · NAT 类型是 Symmetric / 都拿不到 → P2P 打洞很难成功，
    ///     必须有共享节点在中间转发才能通。
    /// 用户报「异地连不上」时，先看这两个值就知道该怪网络还是怪配置。
    /// </summary>
    public async Task<EasyTierNatInfo?> GetNatInfoAsync(CancellationToken ct = default)
    {
        if (!IsRunning || !File.Exists(CliPath)) return null;

        var output = await RunCliAsync("node", ct).ConfigureAwait(false);
        return EasyTierNatInfo.Parse(output);
    }

    /// <summary>
    /// 测一下公共共享节点通不通（TCP 连接，5 秒超时）。
    ///
    /// 为什么值得单独测：共享节点不可达时，两个都在 NAT 后的用户
    /// 根本没有见面的机会，而界面上的表现只是「peer 列表一直是空的」，
    /// 用户完全无从判断。这个探测能把「网络问题」和「配置问题」分开。
    /// </summary>
    public static async Task<bool> IsSharedNodeReachableAsync(
        string host = "public.easytier.top", int port = 11010, int timeoutMs = 5000)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 虚拟网里现在有哪些节点（ peers ）。
    /// 主机可以用它确认「客人进来了没」。
    /// </summary>
    public async Task<IReadOnlyList<EasyTierPeer>> GetPeersAsync(CancellationToken ct = default)
    {
        if (!IsRunning || !File.Exists(CliPath)) return Array.Empty<EasyTierPeer>();

        var output = await RunCliAsync("peer", ct).ConfigureAwait(false);
        return EasyTierPeer.ParseAll(output);
    }

    /// <summary>跑一条 easytier-cli 子命令，返回它的标准输出。</summary>
    private static async Task<string> RunCliAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CliPath,
                // **必须带上 --rpc-portal 且指向我们自己设的固定端口**：
                // cli 默认也是「随机、先试 15888」，看起来能碰上，
                // 但 15888 被占时 portal 会漂到别的端口，cli 就会静默查空
                Arguments = $"--rpc-portal {RpcPortalAddr} {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }
}

/// <summary>本机在虚拟网里的信息。</summary>
public sealed class EasyTierNodeInfo
{
    /// <summary>虚拟 IP。其他人连 Minecraft 时就填这个。</summary>
    public string VirtualIp { get; init; } = "";

    public string Hostname { get; init; } = "";

    /// <summary>
    /// easytier-cli 的表格分列符。
    ///
    /// 它输出的是**全角制表符** `│`（U+2502），不是 ASCII 的 `|`。
    /// 两种都认：写死一种的话，换一版输出格式就整个解析不出东西，
    /// 而且失败得很安静（返回 null → 界面一直显示"启动中"）。
    /// </summary>
    internal static readonly char[] ColumnSeparators = ['│', '|'];

    /// <summary>把一行表格拆成单元格；不是表格行就返回 null。</summary>
    internal static string[]? SplitRow(string line)
    {
        if (!line.Any(ColumnSeparators.Contains)) return null;

        var cells = line.Split(ColumnSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .ToArray();

        return cells.Length >= 2 ? cells : null;
    }

    /// <summary>从 easytier-cli node 的表格输出里解析。</summary>
    public static EasyTierNodeInfo? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? ip = null, host = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var cells = SplitRow(rawLine.Trim());
            if (cells is null) continue;

            if (cells[0].StartsWith("Virtual IP", StringComparison.OrdinalIgnoreCase))
                ip = NormalizeIp(cells[1]);
            else if (cells[0].StartsWith("Hostname", StringComparison.OrdinalIgnoreCase))
                host = cells[1];
        }

        if (string.IsNullOrEmpty(ip)) return null;

        return new EasyTierNodeInfo { VirtualIp = ip, Hostname = host ?? "" };
    }

    /// <summary>
    /// easytier-cli 给出的虚拟 IP 带掩码后缀（形如 <c>10.126.126.7/24</c>）。
    /// 界面上要显示给用户照着敲进游戏，所以只留地址本身。
    /// </summary>
    internal static string NormalizeIp(string raw)
    {
        var value = raw.Trim();
        var slash = value.IndexOf('/');
        return slash > 0 ? value[..slash] : value;
    }
}

/// <summary>虚拟网里的另一个节点（同伴）。</summary>
public sealed class EasyTierPeer
{
    public string VirtualIp { get; init; } = "";
    public string Hostname { get; init; } = "";
    /// <summary>延迟（毫秒）。-1 = 未知。</summary>
    public double LatencyMs { get; init; } = -1;

    /// <summary>
    /// 界面上显示的名字。
    ///
    /// Hostname 现在就是建房间时填的玩家名（我们通过 --hostname 传的），
    /// 所以直接显示它就能认出「这是谁」；万一某台设备没传名字
    /// （比如用户手动起的 easytier-core），回落到机器名也不会显示成空。
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Hostname) ? VirtualIp : Hostname;

    /// <summary>从 easytier-cli peer 的表格输出里解析出所有同伴。</summary>
    public static IReadOnlyList<EasyTierPeer> ParseAll(string output)
    {
        var result = new List<EasyTierPeer>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var cells = EasyTierNodeInfo.SplitRow(rawLine.Trim());
            if (cells is null || cells.Length < 4) continue;

            // ipv4 列带掩码后缀（10.126.126.7/24），先剥掉再判断
            var ip = EasyTierNodeInfo.NormalizeIp(cells[0]);
            if (!System.Net.IPAddress.TryParse(ip, out _)) continue;

            // **本机自己也在 peer 表里**（cost 列显示 Local），不能算作「同伴」，
            // 否则界面上会展示"你和你自己"，用户以为已经有人进来了
            if (cells[2].Equals("Local", StringComparison.OrdinalIgnoreCase)) continue;

            double.TryParse(cells[3], out var latency);

            result.Add(new EasyTierPeer
            {
                VirtualIp = ip,
                Hostname = cells[1],
                LatencyMs = latency,
            });
        }

        return result;
    }
}

/// <summary>
/// 本机的公网出口与 NAT 类型 —— 排查「异地连不上」最该先看的两项。
///
/// 都拿不到（Public IP 空 + NAT 未知）说明本机在严格 NAT 后面，
/// P2P 打洞基本不可能成功，只能靠共享节点中转；
/// 这时候如果共享节点也不通，就必然连不上，而且日志里不会有任何报错。
/// </summary>
public sealed class EasyTierNatInfo
{
    /// <summary>公网出口 IP。空 = 没拿到映射。</summary>
    public string PublicIp { get; init; } = "";

    /// <summary>UDP STUN 探测出的 NAT 类型（FullCone / PortRestricted / Symmetric …）。</summary>
    public string NatType { get; init; } = "";

    /// <summary>是否有可用的公网映射。</summary>
    public bool HasPublicIp => PublicIp.Length > 0;

    /// <summary>是否属于「打洞困难」的 NAT 类型。</summary>
    public bool IsSymmetric =>
        NatType.Contains("Symmetric", StringComparison.OrdinalIgnoreCase);

    public static EasyTierNatInfo? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? ip = null, nat = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var cells = EasyTierNodeInfo.SplitRow(rawLine.Trim());
            if (cells is null || cells.Length < 2) continue;

            // 不同版本这里的标签有 Public IP / Public IPv4 两种写法，都认
            if (cells[0].StartsWith("Public IP", StringComparison.OrdinalIgnoreCase))
                ip = cells[1];
            else if (cells[0].StartsWith("UDP Stun Type", StringComparison.OrdinalIgnoreCase))
                nat = cells[1];
        }

        if (ip is null && nat is null) return null;

        return new EasyTierNatInfo { PublicIp = ip ?? "", NatType = nat ?? "" };
    }
}
