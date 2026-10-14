using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Http;
using LMCL.Core.Java;

namespace LMCL.Core.Game;

public sealed class LaunchRequest
{
    public required string Version { get; init; }
    public required Account Account { get; init; }
    public string? ServerIp { get; init; }
    public int ServerPort { get; init; }
    public string? QuickPlayWorld { get; init; }
}

public sealed record LaunchResult(bool Started, string Message, string? CommandLine = null, int ProcessId = 0);

/// <summary>组装启动命令、拉起游戏进程、把游戏日志回灌到 UI。</summary>
public sealed class LauncherService
{
    private readonly ConfigService _config;
    private readonly JavaService _java;
    private readonly LauncherFactory _factory;
    private readonly AuthlibInjector _injector;
    private readonly SkinLibrary _skins;
    private readonly ILogSink? _log;

    private System.Diagnostics.Process? _process;

    /// <summary>
    /// 正在为这一次游戏进程服务的皮肤中继。
    /// 必须留一个引用：老版本把它建成局部变量，方法一返回就被 GC 掉，
    /// 游戏跑起来时中继早没了，皮肤自然贴不上。
    /// </summary>
    private SkinRelay? _relay;

    public LauncherService(
        ConfigService config, JavaService java, LauncherFactory factory,
        AuthlibInjector injector, SkinLibrary skins, ILogSink? log = null)
    {
        _config = config;
        _java = java;
        _factory = factory;
        _injector = injector;
        _skins = skins;
        _log = log;
    }

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    public event EventHandler<int>? Exited;

    /// <summary>把全局配置和该版本的独立设置合并后的结果。</summary>
    private sealed class EffectiveSettings
    {
        public required InstanceSettings Instance { get; init; }
        public required int MaxMemoryMb { get; init; }
        public required int MinMemoryMb { get; init; }
        public required bool FullScreen { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required string JvmArgs { get; init; }
        public required string GameArgs { get; init; }
        public required bool AutoJava { get; init; }
        public string? JavaPath { get; init; }
    }

    private EffectiveSettings Merge(string version)
    {
        var global = _config.Current;
        var instance = GamePaths.LoadInstance(_config.GameDir, version);

        return new EffectiveSettings
        {
            Instance = instance,
            MaxMemoryMb = instance.MaxMemoryMb ?? global.MaxMemoryMb,
            MinMemoryMb = instance.MinMemoryMb ?? global.MinMemoryMb,
            FullScreen = instance.FullScreen || global.FullScreen,
            Width = instance.WindowWidth > 0 ? instance.WindowWidth : global.WindowWidth,
            Height = instance.WindowHeight > 0 ? instance.WindowHeight : global.WindowHeight,
            JvmArgs = instance.ExtraJvmArgs ?? global.ExtraJvmArgs,
            GameArgs = instance.ExtraGameArgs ?? global.ExtraGameArgs,
            AutoJava = instance.AutoJava ?? global.AutoJava,
            JavaPath = !string.IsNullOrWhiteSpace(instance.JavaPath) ? instance.JavaPath : global.JavaPath,
        };
    }

    public async Task<LaunchResult> LaunchAsync(
        LaunchRequest request, ProgressReporter reporter, CancellationToken ct = default)
    {
        if (IsRunning)
            return new LaunchResult(false, "游戏已经在运行了");

        var settings = Merge(request.Version);
        var gameDir = _config.GameDir;
        var path = GamePaths.ForLaunch(gameDir, request.Version, settings.Instance.Isolated);

        using var http = _factory.CreateHttpClient();
        var launcher = _factory.Create(path, http);

        // 1) 补齐文件（已存在的按大小与 SHA1 跳过，天然可续传）
        reporter.Status($"正在检查 {request.Version} 的完整性…");
        await ResilientInstaller.InstallAsync(launcher, request.Version, reporter, _log, ct).ConfigureAwait(false);

        // 2) 选定 Java
        var version = await launcher.GetVersionAsync(request.Version, ct).ConfigureAwait(false);
        var required = JavaService.ParseMajor(version.JavaVersion?.MajorVersion);
        var javaPath = await ResolveJavaAsync(settings, required, launcher, version, reporter, ct)
            .ConfigureAwait(false);

        // 2.5) 外置登录要先让 authlib-injector 就位，否则客户端不认这台验证服务器
        var injector = await PrepareInjectorAsync(request.Account, reporter, ct).ConfigureAwait(false);

        // 2.6) 离线账号想让游戏里看到自己选的皮肤，就得靠本地皮肤中继
        var relay = await PrepareSkinRelayAsync(request.Account, reporter, ct).ConfigureAwait(false);

        // 3) 组装启动参数
        var session = BuildSession(request.Account, reporter);
        var option = BuildOption(path, session, settings, javaPath, request, injector, relay);

        reporter.Status("正在组装启动参数…");
        var process = await launcher.BuildProcessAsync(request.Version, option, ct).ConfigureAwait(false);

        return StartProcess(process, request.Version, reporter);
    }

    /// <summary>只组装命令、不启动进程——供「查看/复制启动参数」用。</summary>
    public async Task<string> BuildCommandLineAsync(
        LaunchRequest request, ProgressReporter reporter, CancellationToken ct = default)
    {
        var settings = Merge(request.Version);
        var path = GamePaths.ForLaunch(_config.GameDir, request.Version, settings.Instance.Isolated);

        using var http = _factory.CreateHttpClient();
        var launcher = _factory.Create(path, http);
        var version = await launcher.GetVersionAsync(request.Version, ct).ConfigureAwait(false);
        var required = JavaService.ParseMajor(version.JavaVersion?.MajorVersion);

        var injector = await PrepareInjectorAsync(request.Account, reporter, ct).ConfigureAwait(false);
        var relay = await PrepareSkinRelayAsync(request.Account, reporter, ct).ConfigureAwait(false);

        var resolved = await ResolveJavaAsync(settings, required, launcher, version, reporter, ct)
            .ConfigureAwait(false);

        var option = BuildOption(path, BuildSession(request.Account, reporter), settings,
            resolved, request, injector, relay);
        var process = await launcher.BuildProcessAsync(request.Version, option, ct).ConfigureAwait(false);

        return BuildReadable(process);
    }

    /// <summary>
    /// 外置登录需要一个 -javaagent 参数才会真的生效。这里负责把 jar 准备好：
    /// 本地有就直接用，没有就顺手下一份；实在拿不到也不算致命，只是提醒用户。
    /// </summary>
    private async Task<string?> PrepareInjectorAsync(
        Account account, ProgressReporter reporter, CancellationToken ct)
    {
        if (account.Type != AccountType.ThirdParty) return null;

        if (string.IsNullOrWhiteSpace(account.Server))
        {
            reporter.Warn("这个外置账号没有记录验证服务器地址，游戏大概率无法登录，建议重新添加一次账号");
            return null;
        }

        reporter.Status("正在准备外置登录组件…");

        var reported = 0;
        var progress = new Progress<double>(r =>
        {
            // 这个 jar 只有几 MB，按 10% 的粒度报一次就够了，免得刷屏
            var percent = (int)(r * 100);
            if (percent / 10 <= reported) return;
            reported = percent / 10;
            reporter.Status($"正在下载外置登录组件… {percent}%");
        });

        var jar = await _injector.EnsureAsync(_config.UseMirror, progress, ct: ct).ConfigureAwait(false);
        if (jar is null)
            reporter.Warn("外置登录组件准备失败，游戏可能无法登录第三方服务器；可稍后到「工具」里重试");

        return jar;
    }

    /// <summary>
    /// 离线账号的「本地皮肤」要真的出现在游戏里，必须做两件事：
    ///   ① 在本机起一个冒充 Yggdrasil 的回环服务（<see cref="SkinRelay"/>），
    ///      它返回的档案里带着我们选的皮肤；
    ///   ② 挂上 authlib-injector 这个 javaagent，把客户端对 Mojang 的请求改写过去。
    ///
    /// 只做 ① 没用：客户端不会主动来问我们；只做 ② 也没用：那边的档案得有人应答。
    ///
    /// 这里**只处理离线账号**：
    ///   · 微软正版账号的皮肤本来就存在 Mojang 那边，游戏自己就能拿到，
    ///     硬插一个中继反而会把它的档案覆盖掉；
    ///   · 第三方账号的材质由那台服务器决定，而且它已经在用 authlib-injector 了，
    ///     两个 agent 抢同一个位置只会互相打架（后挂的覆盖先挂的）。
    ///
    /// 任何一步失败都只是「这次没皮肤」，绝不阻断启动。
    /// </summary>
    private async Task<SkinRelay?> PrepareSkinRelayAsync(
        Account account, ProgressReporter reporter, CancellationToken ct)
    {
        // 上一次的遗留先收掉（正常路径上进程退出时已经收过）
        ReleaseSkinRelay();

        if (account.Type != AccountType.Offline) return null;

        var entry = ResolveOfflineSkin(account);
        if (entry is null)
        {
            reporter.Info("这个离线账号没有选定本地皮肤，游戏里会是默认外观");
            return null;
        }

        var png = _skins.PathOf(entry);
        if (!File.Exists(png))
        {
            reporter.Warn($"选定的皮肤文件不在（{entry.DisplayName}），游戏里会是默认外观");
            return null;
        }

        reporter.Status("正在准备本地皮肤…");

        // authlib-injector 缺失就没法改写客户端请求，皮肤也就无从谈起
        var jar = await _injector.EnsureAsync(_config.UseMirror, ct: ct).ConfigureAwait(false);
        if (jar is null)
        {
            reporter.Warn("皮肤组件（authlib-injector）准备失败，这次游戏里会是默认外观；可稍后到「工具」里重试");
            return null;
        }

        var slim = entry.ArmModel == SkinArmModel.Slim;
        var relay = SkinRelay.TryCreate(account.Uuid, account.Name, png, slim, _log);

        if (relay is null)
        {
            reporter.Warn("本地皮肤中继没能启动，这次游戏里会是默认外观");
            return null;
        }

        // 记住 jar 路径，BuildOption 里拼 -javaagent 要用
        _relay = relay;
        _relayAgentJar = jar;

        reporter.Success($"本地皮肤已就绪：{entry.DisplayName}（{entry.ArmModelLabel}）");
        _log?.Info($"离线皮肤中继：{entry.DisplayName} → {relay.BaseUri}");

        return relay;
    }

    /// <summary>本次中继配套的 javaagent jar 路径。</summary>
    private string? _relayAgentJar;

    /// <summary>
    /// 这个离线账号该用哪张本地皮肤。
    /// 优先用账号自己绑的那张；没绑或文件不在就退回内置的 Steve。
    /// </summary>
    private SkinEntry? ResolveOfflineSkin(Account account)
    {
        var bound = _skins.Find(account.LocalSkinId);

        if (bound is not null && _skins.IsReady(bound)) return bound;

        // 退回内置 Steve（而不是放弃），这样「没特意选」也能有个像样的皮肤
        var steve = _skins.Find(SkinLibrary.SteveId(SkinLibraryKind.Offline));
        if (steve is not null && _skins.IsReady(steve)) return steve;

        return _skins.ListBuiltIn(SkinLibraryKind.Offline).FirstOrDefault(_skins.IsReady);
    }

    /// <summary>收掉上一次的皮肤中继。进程退出、重新启动、或对象释放时调。</summary>
    private void ReleaseSkinRelay()
    {
        _relay?.Dispose();
        _relay = null;
        _relayAgentJar = null;
    }

    private MSession BuildSession(Account account, ProgressReporter reporter)
    {
        // 离线账号缺的字段由 Account.ToSession() 自己补齐，这里只处理需要真令牌的两种账号
        if (account.Type != AccountType.Offline)
        {
            if (string.IsNullOrEmpty(account.AccessToken))
                throw new InvalidOperationException(
                    $"账号「{account.Name}」没有可用的登录凭证，请到账号窗口重新登录一次再启动。");

            if (account.IsExpired)
                reporter.Warn("登录凭证可能已过期，若启动失败请重新登录");
        }

        return account.ToSession();
    }

    private MLaunchOption BuildOption(
        MinecraftPath path, MSession session, EffectiveSettings settings, string? javaPath,
        LaunchRequest request, string? injector, SkinRelay? relay)
    {
        var option = new MLaunchOption
        {
            Session = session,
            Path = path,
            MaximumRamMb = Math.Max(512, settings.MaxMemoryMb),
            MinimumRamMb = Math.Max(128, Math.Min(settings.MinMemoryMb, settings.MaxMemoryMb)),
            FullScreen = settings.FullScreen,
            JavaPath = javaPath ?? "",
            VersionType = "LMCL",
            GameLauncherName = "LMCL",
            GameLauncherVersion = "2.0.1",
            ClientId = "lmcl",
        };

        if (settings.Width > 0) option.ScreenWidth = settings.Width;
        if (settings.Height > 0) option.ScreenHeight = settings.Height;

        if (!string.IsNullOrWhiteSpace(request.ServerIp))
        {
            option.ServerIp = request.ServerIp;
            option.ServerPort = request.ServerPort > 0 ? request.ServerPort : 25565;
        }

        if (!string.IsNullOrWhiteSpace(request.QuickPlayWorld))
            option.QuickPlaySingleplayer = request.QuickPlayWorld;

        // JVM 参数按顺序拼：先放 -javaagent（外置登录的地基），再叠用户自己写的，
        // 这样用户还能在后面覆盖掉我们用默认值填的东西。
        var jvmArgs = new List<MArgument>();

        if (injector is not null)
        {
            try
            {
                jvmArgs.Add(new MArgument(AuthlibInjector.BuildJavaAgent(injector, request.Account.Server)));
                _log?.Info($"已启用外置登录：注入 authlib-injector，验证服务器 {request.Account.Server}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"外置登录参数生成失败：{ex.Message}");
            }
        }
        else if (relay is not null && _relayAgentJar is { Length: > 0 } relayJar)
        {
            // 离线账号的本地皮肤：把客户端对 Mojang 的请求改写到我方回环服务上。
            //
            // 注意这里是 else if —— 外置登录已经占用了 -javaagent，
            // 再挂一个会把前一个覆盖掉（JVM 只认最后一个），两边都会坏。
            try
            {
                jvmArgs.Add(new MArgument($"{AuthlibInjector.BuildJavaAgent(relayJar, relay.InjectorAddress)}"));
                jvmArgs.Add(new MArgument("-Dauthlibinjector.side=client"));
                _log?.Info($"已启用本地皮肤中继：{relay.BaseUri}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"本地皮肤参数生成失败：{ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.JvmArgs))
        {
            try { jvmArgs.Add(MArgument.FromCommandLine(settings.JvmArgs)); }
            catch (Exception ex) { _log?.Warn($"JVM 参数解析失败，已忽略：{ex.Message}"); }
        }

        if (jvmArgs.Count > 0) option.ExtraJvmArguments = jvmArgs;

        if (!string.IsNullOrWhiteSpace(settings.GameArgs))
        {
            try { option.ExtraGameArguments = new[] { MArgument.FromCommandLine(settings.GameArgs) }; }
            catch (Exception ex) { _log?.Warn($"游戏参数解析失败，已忽略：{ex.Message}"); }
        }

        return option;
    }

    /// <summary>
    /// 决定用哪个 java.exe。
    ///
    /// 这里必须返回一个非空字符串：MLaunchOption.CheckValid() 见到空 JavaPath 会直接抛
    /// ArgumentNullException，也就是说「本机没有合适的 Java、打算用自动下载的运行时」
    /// 这条路根本走不通——运行时得由我们解析成具体路径再交出去。
    /// 优先级：用户指定 → 本机合适的 Java → 库解析出的 Mojang 运行时 → 系统默认 Java。
    /// </summary>
    private async Task<string> ResolveJavaAsync(
        EffectiveSettings settings, int requiredMajor,
        MinecraftLauncher launcher, CmlLib.Core.Version.IVersion version,
        ProgressReporter reporter, CancellationToken ct)
    {
        // 1) 手工指定的路径最优先
        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath))
        {
            reporter.Info($"使用指定 Java：{settings.JavaPath}");
            return settings.JavaPath;
        }

        // 缓存可能是空的（比如刚启动就点了启动），这里按需扫一次盘
        if (_java.Cached.Count == 0)
            await _java.DetectAsync(ct: ct).ConfigureAwait(false);

        // 2) 自动选择
        if (settings.AutoJava)
        {
            var best = _java.Best(requiredMajor > 0 ? requiredMajor : null);
            if (best is not null && (requiredMajor <= 0 || best.Major >= requiredMajor))
            {
                reporter.Info($"使用本机 Java {best.Major}：{best.Path}");
                return best.Path;
            }

            reporter.Info(requiredMajor > 0
                ? $"本机没有 Java {requiredMajor}，改用已下载的运行时"
                : "本机没有可用 Java，改用已下载的运行时");
        }
        else
        {
            reporter.Info("自动选择 Java 已关闭，改用系统默认 Java");
        }

        // 3) 让库把运行时解析成具体路径。启动前已经跑过 InstallAsync，
        //    该下的运行时这会儿应该躺在 runtime 目录里了。
        foreach (var candidate in new[] { SafeJavaPath(launcher, version), launcher.GetDefaultJavaPath() })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!File.Exists(candidate)) continue;

            reporter.Info($"使用运行时自带 Java：{candidate}");
            return candidate;
        }

        // 4) 退无可退：本机总得有一个能用的 Java，否则游戏铁定起不来
        if (_java.Cached.FirstOrDefault() is { } fallback)
        {
            reporter.Warn($"没能确定匹配的 Java，退回本机 Java {fallback.Major}：{fallback.Path}");
            return fallback.Path;
        }

        throw new InvalidOperationException(
            "本机一个可用的 Java 都没有，也没能自动下载运行时。\n" +
            "请到「设置 → Java」里手动指定一个 java.exe，或先装一个 Java 21。");
    }

    /// <summary>库的 GetJavaPath 在版本信息不全时可能抛异常，这里吞掉当成「没解析出来」。</summary>
    private static string? SafeJavaPath(MinecraftLauncher launcher, CmlLib.Core.Version.IVersion version)
    {
        try { return launcher.GetJavaPath(version); }
        catch { return null; }
    }

    private LaunchResult StartProcess(System.Diagnostics.Process process, string version, ProgressReporter reporter)
    {
        var startInfo = process.StartInfo;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        // java.exe 是控制台子系统程序，UseShellExecute=false 时 CreateNoWindow 留 false
        // 就会跟着弹一个 CMD 黑窗（输出全被我们重定了，那窗口纯属干扰）。
        // 两个都设上：CreateNoWindow 管创建，WindowStyle 兜底隐藏。
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) reporter.Info(e.Data!); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) reporter.Warn(e.Data!); };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }

            // 游戏没了，中继也就没用了：留着只会白占一个回环端口
            ReleaseSkinRelay();

            reporter.Info($"游戏进程已退出（代码 {code}）");
            Exited?.Invoke(this, code);
            _process = null;
        };

        if (!process.Start())
            return new LaunchResult(false, "启动失败：进程未能创建");

        // 关掉子进程的标准输入，免得它卡在等输入上。
        // 这一步必须在 Start() 之后——Process.StandardInput 在进程起来之前根本没有实体，
        // 提前访问会直接抛 InvalidOperationException("StandardIn has not been redirected")，
        // 结果就是「任何版本都启动失败」。曾经踩过，别再把顺序调回去。
        try { process.StandardInput.Close(); }
        catch (Exception ex) { _log?.Warn($"关闭标准输入失败（不影响游戏）：{ex.Message}"); }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        _config.Current.LaunchCount++;
        _config.Current.LastVersion = version;
        _config.Save();

        _log?.Success($"已启动 {version}（PID {process.Id}）");
        return new LaunchResult(true, $"已启动 {version}", BuildReadable(process), process.Id);
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { _log?.Warn($"结束游戏进程失败：{ex.Message}"); }

        // 主动结束这条路不会走 process.Exited 的时机不一定早，这里直接收掉更稳
        ReleaseSkinRelay();
    }

    /// <summary>把启动参数整理成可读的多行文本（用于展示 / 复制 / 排查）。</summary>
    public static string BuildReadable(System.Diagnostics.Process process)
    {
        var args = process.StartInfo.Arguments;
        var text = new System.Text.StringBuilder();
        text.Append(process.StartInfo.FileName).Append(' ');
        foreach (var token in SplitCommandLine(args))
            text.Append(Environment.NewLine).Append("  ").Append(token);
        return text.ToString();
    }

    /// <summary>按 Windows 命令行规则切分参数（保留引号内的空格）。</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return result;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>读游戏日志、找常见崩溃原因。</summary>
    public static string AnalyzeLog(IEnumerable<string> lines)
    {
        var samples = lines.TakeLast(400).ToArray();
        var text = string.Join('\n', samples);
        var findings = new List<string>();

        void Check(string needle, string advice)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) findings.Add(advice);
        }

        Check("UnsupportedClassVersionError", "Java 版本太低：请给这个版本换一个更高的 Java（1.17+ 需要 Java 17，1.20.5+ 需要 Java 21）。");
        Check("Unsupported major.minor", "Java 版本不匹配，换 Java 后重试。");
        // 离线账号必然出现这一条（拿占位令牌去问官方，当然 401），游戏随后照样进去，
        // 不单独说明的话用户会以为出了问题
        Check("Failed to verify authentication", "「Failed to verify authentication / 401」是离线账号的正常现象：游戏会拿你的令牌去官方校验，而离线身份本来就没有真令牌。只要后面还能看到「Setting user: 你的名字」，就说明已经用离线身份进去了，单机与离线服都不受影响。");
        Check("OutOfMemoryError", "内存不足：到「版本设置」里调大最大内存，或减少模组数量。");
        Check("java.lang.ClassNotFoundException: net.minecraft", "缺少游戏本体：请先安装对应的原版版本。");
        Check("NoClassDefFoundError", "依赖缺失或模组不兼容，检查是否漏装了前置模组。");
        Check("DuplicateModsFoundException", "存在重复模组，删掉重复的 jar。");
        Check("Missing or unsupported mandatory dependencies", "模组缺少前置，按提示补装。");
        Check("LWJGLException", "显卡/OpenGL 初始化失败：更新显卡驱动，或尝试切换渲染后端。");
        Check("Pixel format not accelerated", "显卡驱动不支持所需的 OpenGL：更新驱动。");
        Check("Failed to create display", "无法创建游戏窗口：检查显卡驱动或远程桌面环境。");
        Check("The game crashed whilst", "游戏崩溃，详见崩溃报告。");
        Check("An error occurred while loading mods", "模组加载失败，检查模组与加载器版本是否匹配。");
        Check("Mixin apply failed", "Mixin 注入失败：模组与当前游戏/加载器版本不兼容。");
        Check("Could not find or load main class", "主类缺失：安装不完整，建议重新安装该版本。");
        Check("Failed to download", "下载失败，换个网络或镜像源重试。");
        Check("Connection refused", "无法连接服务器。");
        Check("Unrecognized option", "启动参数不被识别，检查自定义 JVM 参数。");
        Check("Invalid maximum heap size", "最大内存设置不合理，请调小。");
        Check("The specified size exceeds the maximum representable size", "内存值太大，请调小。");

        if (findings.Count == 0)
            findings.Add(text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                ? "日志里有异常，但没有命中常见原因，建议把完整日志发到模组或整合包作者处。"
                : "没有发现明显的报错。");

        return string.Join(Environment.NewLine, findings.Distinct().Select(f => "· " + f));
    }

    /// <summary>把崩溃报告 / 最新日志找出来，方便导出。</summary>
    public static string? FindLatestLog(string gameDir, string version, bool isolated)
    {
        var root = GamePaths.InstanceRoot(gameDir, version, isolated);
        var candidates = new List<string> { Path.Combine(root, "logs", "latest.log") };

        var crashDir = Path.Combine(root, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            var newest = new DirectoryInfo(crashDir).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (newest is not null) candidates.Add(newest.FullName);
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
