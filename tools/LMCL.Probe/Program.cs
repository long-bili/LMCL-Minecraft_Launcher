using System.Text;
using CmlLib.Core;
using LMCL.Core.Accounts;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;
using LMCL.Core.Game;
using LMCL.Core.Http;
using LMCL.Core.Java;
using LMCL.Core.Resources;

Console.OutputEncoding = Encoding.UTF8;

var step = args.Length > 0 ? args[0] : "help";
var mcVersion = args.Length > 1 ? args[1] : "1.20.1";
var gameDir = Environment.GetEnvironmentVariable("PCLITE_GAMEDIR")
    ?? @"C:\Users\a1378\WorkBuddy\2026-10-13-17-45-09\_probe\mc";

var config = new ConfigService();
config.Load();
config.Current.GameDir = gameDir;
config.Current.Source = DownloadSource.Bmclapi;
config.Current.DownloadThreads = 8;

var log = new ActionLogSink(e =>
{
    var color = e.Level switch
    {
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Success => ConsoleColor.Green,
        LogLevel.Debug => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray,
    };
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine($"  [{e.TimeText}] {e.Message}");
    Console.ForegroundColor = previous;
});

var java = new JavaService(config, log);
var factory = new LauncherFactory(config, java, log);
var versions = new VersionService(config, log);
var install = new InstallService(config, java, factory, log);
var injector = new AuthlibInjector(log);
// 皮肤库要先建出来：LauncherService 启动离线账号时会按它解析该下发哪张皮肤
var skins = new LMCL.Core.Accounts.SkinLibrary();
var launcher = new LauncherService(config, java, factory, injector, skins, log);

var lastPercent = -1;
ProgressReporter Reporter() => new(log)
{
    StatusChanged = text => Console.WriteLine($"  » {text}"),
    RatioChanged = ratio =>
    {
        var percent = ratio < 0 ? -1 : (int)(ratio * 100);
        if (percent == lastPercent) return;
        lastPercent = percent;
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(percent < 0 ? "  ......" : $"  ...... {percent}%");
        Console.ForegroundColor = previous;
    },
};

Console.WriteLine($"游戏目录：{gameDir}");
Console.WriteLine($"步骤：{step}");
Console.WriteLine(new string('─', 78));

switch (step)
{
    case "paths":
        Paths();
        break;
    case "java":
        await Java();
        break;
    case "remote":
        await Remote();
        break;
    case "launchcmd":
        await LaunchCommand();
        break;
    case "launch":
        await LaunchReal();
        break;
    case "injector":
        await InjectorStep();
        break;
    case "files":
        await Files();
        break;
    case "mirror":
        MirrorRules();
        break;
    case "vanilla":
        await Vanilla();
        break;
    case "loaders":
        await Loaders();
        break;
    case "forge":
        await Loader(LoaderKind.Forge);
        break;
    case "neo":
        await Loader(LoaderKind.NeoForge);
        break;
    case "fabric":
        await Loader(LoaderKind.Fabric);
        break;
    case "quilt":
        await Loader(LoaderKind.Quilt);
        break;
    case "versionlist":
        VersionList();
        break;
    case "msa":
        await Msa();
        break;
    case "mod":
        await Mod(args.Length > 1 ? args[1] : "sodium",
                  args.Length > 2 ? args[2] : "1.20.1",
                  args.Length > 3 ? args[3] : "fabric");
        break;
    case "modupdate":
        await ModUpdate();
        break;
    case "skinrelay":
        await SkinRelayStep();
        break;
    default:
        Console.WriteLine("用法: LMCL.Probe <paths|java|remote|launchcmd|launch|injector|files|mirror|vanilla|loaders|forge|neo|fabric|quilt|versionlist|msa|mod|modupdate|skinrelay> [参数]");
        break;
}

// ────────────────────────────────────────────────────────────

void Paths()
{
    var shared = GamePaths.Shared(gameDir);
    Console.WriteLine("MinecraftPath（共享布局）：");
    Console.WriteLine($"  BasePath = {shared.BasePath}");
    Console.WriteLine($"  Versions = {shared.Versions}");
    Console.WriteLine($"  Library  = {shared.Library}");
    Console.WriteLine($"  Assets   = {shared.Assets}");
    Console.WriteLine($"  Resource = {shared.Resource}");
    Console.WriteLine($"  Runtime  = {shared.Runtime}");

    var isolated = GamePaths.ForLaunch(gameDir, "1.20.1-forge-47.4.24", true);
    Console.WriteLine("MinecraftPath（版本隔离）：");
    Console.WriteLine($"  BasePath = {isolated.BasePath}");
    Console.WriteLine($"  Versions = {isolated.Versions}");

    Console.WriteLine("路径换算：");
    Console.WriteLine($"  版本目录   = {GamePaths.VersionDir(gameDir, "1.20.1")}");
    Console.WriteLine($"  版本 JSON  = {GamePaths.VersionJson(gameDir, "1.20.1")}");
    Console.WriteLine($"  mods(隔离) = {GamePaths.ResourceDir(gameDir, "1.20.1", true, "mods")}");
}

async Task Java()
{
    var detected = await java.DetectAsync(force: true);
    Console.WriteLine($"检测到 {detected.Count} 个 Java：");
    foreach (var info in detected)
        Console.WriteLine($"  Java {info.Major,-3} {info.Version,-16} [{info.Source}] {info.Path}");

    foreach (var need in new[] { 8, 17, 21 })
        Console.WriteLine($"  Best({need}) => {(java.Best(need) is { } j ? $"Java {j.Major} {j.Path}" : "(无)")}");

    Console.WriteLine("版本号解析：");
    foreach (var text in new[] { "1.8.0_401", "17.0.2", "21", "21.0.4+7-LTS", "1.7.0_80" })
        Console.WriteLine($"  {text,-16} => {JavaService.ParseMajor(text)}");
}

async Task Remote()
{
    var list = await install.GetRemoteVersionsAsync();
    Console.WriteLine($"官方清单共 {list.Count} 个版本，最新 12 个正式版：");
    foreach (var item in list.Where(v => v.Type == "release").Take(12))
        Console.WriteLine($"  {item.Name,-12} {item.TypeLabel,-6} {item.ReleaseText}");

    Console.WriteLine($"最新正式版：{list.FirstOrDefault(v => v.Type == "release")?.Name}");
}

/// <summary>
/// 真启动一次游戏：走完整的 LaunchAsync（装齐文件 → 选 Java → 组装参数 → 起进程），
/// 观察若干秒，把游戏自己写的日志读回来判断它到底有没有跑起来，然后收掉进程。
///
/// 这是唯一能证明「启动器真能开游戏」的步骤——launchcmd 只组装命令不起进程，
/// 而命令拼对了不代表进程能起来（JavaPath 空、jar 缺失、参数不认都会在这一步暴露）。
/// </summary>
async Task LaunchReal()
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await java.DetectAsync(force: true);

    var account = new Account
    {
        Name = Environment.GetEnvironmentVariable("PCLITE_USER") ?? "Steve",
        Type = AccountType.Offline,
        Uuid = OfflineUuid.ForName(Environment.GetEnvironmentVariable("PCLITE_USER") ?? "Steve"),
        // PCLITE_SKIN 指定用哪张本地皮肤（official-steve-offline / official-alex-offline / custom-…）。
        // 测「皮肤有没有真的进游戏」时这一步很关键：默认的 Steve 和游戏内置外观**长得一模一样**，
        // 拿它测等于没测 —— 必须换一张明显不同的（比如 Alex）才看得出区别。
        LocalSkinId = Environment.GetEnvironmentVariable("PCLITE_SKIN"),
    };

    // 游戏目录里没写日志的话，至少还能从进程存活与退出码上判断
    var instance = GamePaths.ForLaunch(gameDir, mcVersion, true);
    var logDir = Path.Combine(instance.BasePath, "logs");

    Console.WriteLine($"以离线账号「{account.Name}」启动 {mcVersion}");

    // PCLITE_WORLD 指定要快速进入的存档名。
    // 测皮肤时必须用上：皮肤是玩家实体生成时才向服务端要的，
    // 停在主菜单的话客户端根本不会去查档案，中继也就收不到请求。
    var quickWorld = Environment.GetEnvironmentVariable("PCLITE_WORLD");

    var result = await launcher.LaunchAsync(
        new LaunchRequest
        {
            Version = mcVersion,
            Account = account,
            QuickPlayWorld = string.IsNullOrWhiteSpace(quickWorld) ? null : quickWorld,
        }, Reporter());

    Console.WriteLine();
    Console.WriteLine($"已启动 = {result.Started}");
    Console.WriteLine($"消息   = {result.Message}");
    Console.WriteLine($"PID    = {result.ProcessId}");

    if (!result.Started)
    {
        Console.WriteLine("没起来，后面不用看了");
        return;
    }

    // 给它一点时间把日志文件写出来；Minecraft 起窗口通常在 10 秒内
    var seconds = int.TryParse(Environment.GetEnvironmentVariable("PCLITE_WAIT"), out var s) ? s : 45;
    Console.WriteLine($"观察 {seconds} 秒…");

    string? log = null;
    for (var elapsed = 0; elapsed < seconds; elapsed += 3)
    {
        await Task.Delay(3000);
        var candidate = Path.Combine(logDir, "latest.log");
        if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) log = candidate;
    }

    Console.WriteLine($"进程还活着 = {launcher.IsRunning}");

    launcher.Kill();
    await Task.Delay(2000);
    Console.WriteLine($"收掉之后还活着 = {launcher.IsRunning}");

    Console.WriteLine();
    if (log is null)
    {
        Console.WriteLine($"!! {logDir} 下没有 latest.log —— 游戏可能没走到写日志那一步");
        return;
    }

    Console.WriteLine($"游戏日志：{log}（{new FileInfo(log).Length / 1024} KB）");
    var lines = File.ReadAllLines(log);
    Console.WriteLine($"共 {lines.Length} 行。头 6 行：");
    foreach (var line in lines.Take(6)) Console.WriteLine("  " + line);
    Console.WriteLine("尾 12 行：");
    foreach (var line in lines.TakeLast(12)) Console.WriteLine("  " + line);

    Console.WriteLine();
    Console.WriteLine(LauncherService.AnalyzeLog(lines));
    Console.WriteLine($"总耗时 {watch.Elapsed.TotalSeconds:0.0} 秒");
}

async Task LaunchCommand()
{
    await java.DetectAsync(force: true);
    var built = versions.List();
    if (built.Count == 0)
    {
        Console.WriteLine("没有已安装的版本，先跑 vanilla 步骤");
        return;
    }

    var target = built[0];
    Console.WriteLine($"目标版本：{target.Name}（{target.Subtitle}）");

    // PCLITE_ACCT 可以指定账号类型（offline / third），用来验证
    // 「外置登录」和「本地皮肤中继」这两个 -javaagent 不会互相覆盖
    var kind = (Environment.GetEnvironmentVariable("PCLITE_ACCT") ?? "offline").ToLowerInvariant();

    var account = kind == "third"
        ? new Account
        {
            Name = "ThirdUser",
            Type = AccountType.ThirdParty,
            Uuid = OfflineUuid.ForName("ThirdUser"),
            AccessToken = "probe-token",
            Server = Environment.GetEnvironmentVariable("PCLITE_SERVER") ?? "https://littleskin.cn/api/yggdrasil",
        }
        : new Account
        {
            Name = "Steve",
            Type = AccountType.Offline,
            Uuid = OfflineUuid.ForName("Steve"),
            LocalSkinId = Environment.GetEnvironmentVariable("PCLITE_SKIN"),
        };

    Console.WriteLine($"账号类型：{account.Type}");

    var text = await launcher.BuildCommandLineAsync(
        new LaunchRequest { Version = target.Name, Account = account }, Reporter());

    if (!string.IsNullOrEmpty(text)) Console.WriteLine(text);

    var tokens = text.Split(Environment.NewLine)
        .Select(t => t.Trim())
        .Where(t => t.Length > 0)
        .ToArray();

    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"参数总数：{tokens.Length - 1}");

    for (var i = 0; i < tokens.Length; i++)
    {
        if (tokens[i] is "-cp" or "-classpath")
        {
            var entries = tokens[i + 1].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var missing = entries.Where(e => !File.Exists(e)).ToArray();
            Console.WriteLine($"classpath：{entries.Length} 条，缺失 {missing.Length} 条");
            foreach (var gone in missing) Console.WriteLine($"  ✗ 缺 {gone}");
        }
        if (tokens[i] is "--gameDir" or "--assetsDir" or "--assetIndex" or "--version" or
            "--uuid" or "--accessToken" or "--userType" or "--username" or "--clientId")
            Console.WriteLine($"  {tokens[i]} = {tokens[i + 1]}");
        if (tokens[i].StartsWith("-Djava.library.path"))
            Console.WriteLine($"  {tokens[i].Split('=')[0]} = {tokens[i].Split('=')[1]}  存在={Directory.Exists(tokens[i].Split('=')[1])}");
    }

    var unresolved = tokens.Where(t => t.Contains("${")).ToArray();
    Console.WriteLine(unresolved.Length == 0
        ? "占位符：全部已替换 ✓"
        : $"未替换的占位符：{string.Join(", ", unresolved)}");
}

/// <summary>
/// 把地址改写的规则一条条断言出来。改镜像策略时最怕「以为改了其实没生效」，
/// 尤其 /v1/objects 这种靠路径排除的例外。
/// </summary>
void MirrorRules()
{
    var cases = new (string Url, bool ShouldMirror, string Why)[]
    {
        ("https://resources.download.minecraft.net/ab/abcdef", true, "资源对象：镜像直连，快"),
        ("https://libraries.minecraft.net/com/foo/bar.jar", true, "依赖库：镜像直连"),
        ("https://maven.minecraftforge.net/net/x/y.jar", true, "Forge maven"),
        ("https://meta.fabricmc.net/v2/versions/loader", true, "fabric-meta"),
        ("https://piston-meta.mojang.com/v1/packages/aa/1.20.1.json", true, "版本 JSON：小文件"),
        ("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", false, "版本清单：镜像 302 到不可靠 CDN"),
        ("https://piston-data.mojang.com/v1/objects/0c3e/client.jar", false, "客户端 jar：镜像只有 38~350 KB/s"),
        ("https://piston-data.mojang.com/v1/objects/abcd/java-runtime-gamma.zip", false, "Mojang 运行时：同理"),
        ("ftp://example.com/x", false, "非 https 不改写"),
    };

    var failed = 0;
    foreach (var (url, shouldMirror, why) in cases)
    {
        var mirrored = MirrorHttpHandler.ToMirror(new Uri(url));
        var ok = (mirrored is not null) == shouldMirror;
        if (!ok) failed++;

        Console.WriteLine($"{(ok ? "✓" : "✗")} {(shouldMirror ? "改写" : "保留")}  {url}");
        Console.WriteLine($"    → {mirrored?.ToString() ?? "(官方源)"}    {why}");
    }

    Console.WriteLine();
    Console.WriteLine(failed == 0 ? $"{cases.Length} 条规则全部符合预期 ✓" : $"{failed} 条不符合预期 ✗");
}

/// <summary>
/// 把「安装到底会动哪些文件」摊开看：提取器清单、目标路径重复情况、客户端 jar 在不在。
/// </summary>
async Task Files()
{
    using var http = factory.CreateHttpClient();
    var launcher = factory.Create(GamePaths.Shared(gameDir), http);

    Console.WriteLine($"版本：{mcVersion}");
    Console.WriteLine("提取器（按执行顺序）：");
    var index = 0;
    foreach (var extractor in launcher.FileExtractors)
        Console.WriteLine($"  {++index}. {extractor.GetType().FullName}");

    var version = await launcher.GetVersionAsync(mcVersion);
    var files = (await launcher.ExtractFiles(version)).ToArray();

    Console.WriteLine();
    Console.WriteLine($"GameFile 总数：{files.Length}");

    var duplicates = files
        .GroupBy(f => f.Path ?? "", StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .OrderByDescending(g => g.Count())
        .ToArray();

    Console.WriteLine($"目标路径重复的组：{duplicates.Length}");
    foreach (var group in duplicates.Take(10))
        Console.WriteLine($"  ×{group.Count()}  {group.Key}");

    var missing = files.Where(f => !File.Exists(f.Path ?? "")).ToArray();
    Console.WriteLine($"磁盘上还不存在的：{missing.Length}");

    var jarName = $"versions{Path.DirectorySeparatorChar}{mcVersion}{Path.DirectorySeparatorChar}{mcVersion}.jar";
    var client = files.FirstOrDefault(f => f.Path?.EndsWith(jarName, StringComparison.OrdinalIgnoreCase) == true);
    Console.WriteLine();
    Console.WriteLine(client is null
        ? $"!! 文件清单里根本没有客户端 jar（versions\\{mcVersion}\\{mcVersion}.jar）"
        : $"客户端 jar：{client.Path}\n  url = {client.Url}\n  存在 = {File.Exists(client.Path)}");
}

/// <summary>
/// 验证外置登录这条链路：下载 authlib-injector → 用一个第三方账号组装启动命令，
/// 确认 -javaagent 与 --userType 都按预期出现。
/// </summary>
async Task InjectorStep()
{
    const string server = "https://example.com/api/yggdrasil/";

    Console.WriteLine("① 读发布清单并下载（走当前配置的下载源）");
    var release = await injector.CheckAsync(config.UseMirror);
    if (release is null)
    {
        Console.WriteLine("   ✗ 发布清单读取失败，后面的步骤没法继续");
        return;
    }
    Console.WriteLine($"   版本 {release.Version}");
    Console.WriteLine($"   地址 {release.Url}");
    Console.WriteLine($"   sha256 {release.Sha256}");

    var jar = await injector.EnsureAsync(config.UseMirror, force: true);
    if (jar is null)
    {
        Console.WriteLine("   ✗ 下载或校验失败");
        return;
    }
    Console.WriteLine($"   ✓ 已就位 {jar}（{new FileInfo(jar).Length / 1024} KB）");

    Console.WriteLine("② 再调一次，确认本地已有时不会再下（幂等）");
    var again = await injector.EnsureAsync(config.UseMirror);
    Console.WriteLine(again == jar ? "   ✓ 直接复用本地文件" : $"   ✗ 路径变了：{again}");

    Console.WriteLine("③ 组装第三方账号的启动命令");
    var built = versions.List();
    if (built.Count == 0)
    {
        Console.WriteLine("   没有已安装的版本，跳过（先跑 vanilla 步骤）");
        return;
    }

    var target = built[0];
    var account = new Account
    {
        Name = "ThirdUser",
        Uuid = OfflineUuid.ForName("ThirdUser"),
        AccessToken = "fake-token-for-layout-check",
        Type = AccountType.ThirdParty,
        Server = server,
    };

    Console.WriteLine($"   目标版本 {target.Name}");
    var text = await launcher.BuildCommandLineAsync(
        new LaunchRequest { Version = target.Name, Account = account }, Reporter());

    var tokens = LauncherService.SplitCommandLine(
        text.Split(Environment.NewLine).Skip(1).Aggregate("", (acc, line) => acc + " " + line.Trim()));

    var agent = tokens.FirstOrDefault(t => t.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase));
    var userTypeIndex = tokens.ToList().IndexOf("--userType");
    var userType = userTypeIndex >= 0 && userTypeIndex + 1 < tokens.Count ? tokens[userTypeIndex + 1] : "(缺失)";
    var accessTokenIndex = tokens.ToList().IndexOf("--accessToken");
    var accessToken = accessTokenIndex >= 0 && accessTokenIndex + 1 < tokens.Count
        ? tokens[accessTokenIndex + 1] : "(缺失)";

    Console.WriteLine($"   -javaagent = {agent ?? "（缺失！）"}");
    Console.WriteLine($"   --userType = {userType}");
    Console.WriteLine($"   --accessToken = {accessToken}");

    var pass = agent is not null
               && agent.Contains(jar, StringComparison.OrdinalIgnoreCase)
               && agent.EndsWith(server, StringComparison.Ordinal)
               && userType == "mojang"
               && accessToken == "fake-token-for-layout-check";

    Console.WriteLine(pass ? "   ✓ 外置登录参数正确" : "   ✗ 参数不符合预期");
}

async Task Vanilla()
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await install.InstallAsync(mcVersion, Reporter());
    watch.Stop();
    Console.WriteLine($"耗时 {watch.Elapsed.TotalSeconds:0.0} 秒");

    var installed = versions.List(includeSize: true);
    Console.WriteLine($"已安装 {installed.Count} 个版本：");
    foreach (var item in installed)
        Console.WriteLine($"  {item.Name,-32} {item.Subtitle,-24} {item.SizeText}");
}

async Task Loaders()
{
    foreach (var kind in new[] { LoaderKind.Forge, LoaderKind.NeoForge, LoaderKind.Fabric, LoaderKind.Quilt })
    {
        try
        {
            var list = await install.GetLoaderVersionsAsync(kind, mcVersion);
            Console.WriteLine($"{LoaderLabels.Of(kind),-10} {list.Count,4} 个版本   最新 {list.FirstOrDefault()?.Version ?? "(无)"}"
                + (list.FirstOrDefault(v => v.Recommended) is { } r ? $"   推荐 {r.Version}" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoaderLabels.Of(kind),-10} 失败：{ex.Message}");
        }
    }
}

async Task Loader(LoaderKind kind)
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var name = await install.InstallLoaderAsync(kind, mcVersion, null, Reporter());
    watch.Stop();

    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"{LoaderLabels.Of(kind)} 安装完成：{name}，耗时 {watch.Elapsed.TotalSeconds:0.0} 秒");
    var installed = versions.List(includeSize: true);
    foreach (var item in installed)
        Console.WriteLine($"  {item.Name,-34} {item.Subtitle,-26} {item.SizeText}");
}

void VersionList()
{
    var installed = versions.List(includeSize: true);
    Console.WriteLine($"已安装 {installed.Count} 个版本：");
    foreach (var item in installed)
        Console.WriteLine($"  {item.Name,-34} {item.LoaderLabel,-10} {item.LoaderVersion,-12} 隔离={item.Isolated,-6} {item.SizeText}");
}

/// <summary>
/// 内置 client_id 的冒烟测试：只问微软认不认，不需要真人登录。
/// 另外把「桌面回调」这条约定也验一遍——授权地址里必须是
/// https://login.live.com/oauth20_desktop.srf，别的回环地址内置 id 一概不认。
/// 哪天正版登录集体失败，先跑这一步区分「id 作废了」还是「网断了」。
/// </summary>
/// <summary>
/// 社区资源的实打实验证：搜索 → 列版本 → 看能不能定位到具体 jar。
/// </summary>
async Task Mod(string keyword, string gameVersion, string loader)
{
    var searcher = new ResourceSearcher(config, log);

    var result = new ResourceSearchResult();
    await searcher.SearchAsync(new ResourceSearchRequest
    {
        Type = CommunityResourceType.Mod,
        SearchText = keyword,
        GameVersion = gameVersion,
        Loader = loader,
        PageSize = 20,
    }, result);

    if (result.Results.Count == 0)
    {
        Console.WriteLine($"没搜到「{keyword}」。");
        return;
    }

    Console.WriteLine($"搜到 {result.Results.Count} 条（平台报的总数 {result.Total}）：");
    foreach (var item in result.Results)
        Console.WriteLine($"  {item.Title,-28} {item.AuthorText,-16} {item.DownloadsText}");

    var hit = result.Results[0];
    var list = await searcher.GetVersionsAsync(hit.Id, gameVersion, loader);
    if (list.Count == 0)
    {
        Console.WriteLine($"{hit.Title} 没有 {gameVersion} / {loader} 的版本。");
        return;
    }

    Console.WriteLine($"{hit.Title} 共 {list.Count} 个版本，取第一个：{list[0].Name}");
    var modDir = Path.Combine(gameDir, "versions", gameVersion, "mods");
    var file = await searcher.InstallAsync(modDir, list[0], new Progress<double>(_ => { }));

    Console.WriteLine($"装好了：{file}");
}

/// <summary>
/// 更新检查的验证：先装一个版本，再问一次「有没有更新」。
/// 已是最新的文件不该再报更新——这条能反着抓住「哈希算错」之类的毛病。
/// </summary>
async Task ModUpdate()
{
    var searcher = new ResourceSearcher(config, log);

    var result = new ResourceSearchResult();
    await searcher.SearchAsync(new ResourceSearchRequest
    {
        Type = CommunityResourceType.Mod,
        SearchText = "fabric-api",
        GameVersion = "1.20.1",
        Loader = "fabric",
        PageSize = 5,
    }, result);

    if (result.Results.Count == 0)
    {
        Console.WriteLine("没搜到 fabric-api。");
        return;
    }

    var list = await searcher.GetVersionsAsync(result.Results[0].Id, "1.20.1", "fabric");
    if (list.Count == 0)
    {
        Console.WriteLine("fabric-api 没有 1.20.1 / fabric 的版本。");
        return;
    }

    var modDir = Path.Combine(gameDir, "versions", "1.20.1", "mods");
    var file = await searcher.InstallAsync(modDir, list[0], new Progress<double>(_ => { }));
    Console.WriteLine($"装了最新版：{Path.GetFileName(file)}");

    var again = await searcher.CheckUpdatesAsync(new[] { file }, "1.20.1", "fabric");
    Console.WriteLine($"已是最新版，再查一次：{(again.Count == 0 ? "没有更新（正确）" : "报了 " + again.Count + " 个（不对）")}");
}

async Task Msa()
{
    var auth = new MicrosoftAuth(HttpClients.Create(useMirror: false, log), log);

    Console.WriteLine($"client_id：{MicrosoftAuth.ClientId}");

    var (ok, message) = await auth.ProbeClientIdAsync();

    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"client_id：{(ok ? "可用" : "不可用")}：{message}");
    Console.ResetColor();

    var url = auth.BuildAuthorizeUrl();
    var usesDesktop = url.Contains("login.live.com/oauth20_desktop.srf", StringComparison.OrdinalIgnoreCase);
    var hasClient = url.Contains($"client_id={Uri.EscapeDataString(MicrosoftAuth.ClientId)}", StringComparison.OrdinalIgnoreCase);

    Console.ForegroundColor = usesDesktop && hasClient ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"授权地址：{(usesDesktop && hasClient ? "正确" : "不对")}"
                      + $"（桌面回调={(usesDesktop ? "是" : "否")}　带 client_id={(hasClient ? "是" : "否")}）");
    Console.WriteLine($"地址：{url}");
    Console.ResetColor();
}

/// <summary>
/// 验证本地皮肤中继：起一个真的 relay，然后按客户端会走的顺序把它问一遍，
/// 并且**用 relay 自己公布的公钥验一次签名** —— 签名验不过，客户端就会当成
/// 伪造档案丢掉，皮肤照样出不来。这是「皮肤下发」这条链路上最容易写错的一环。
/// </summary>
async Task SkinRelayStep()
{
    var skins = new LMCL.Core.Accounts.SkinLibrary();
    skins.EnsureSeeded(gameDir);

    var entry = skins.ListBuiltIn(SkinLibraryKind.Offline).FirstOrDefault(skins.IsReady);
    if (entry is null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("皮肤库里一张内置皮肤都没有，没法测。");
        Console.ResetColor();
        return;
    }

    var png = skins.PathOf(entry);
    Console.WriteLine($"用这张皮肤测试：{entry.DisplayName}（{entry.ArmModelLabel}）");
    Console.WriteLine($"文件：{png}");

    const string uuid = "8ee2e0a1b6c34c9aa2a0d1a1b1c1d1e1";
    const string name = "ProbePlayer";

    using var relay = SkinRelay.TryCreate(uuid, name, png, slim: false, log);
    if (relay is null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("皮肤中继启动失败。");
        Console.ResetColor();
        return;
    }

    Console.WriteLine($"中继地址：{relay.BaseUri}");
    Console.WriteLine($"javaagent 地址：{relay.InjectorAddress}");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var failures = new List<string>();

    // ── ① 根文档：公钥 + skinDomains ──
    var rootText = await http.GetStringAsync(relay.BaseUri);
    using var root = System.Text.Json.JsonDocument.Parse(rootText);
    var publicKey = root.RootElement.TryGetProperty("signaturePublickey", out var pk)
        ? pk.GetString() ?? "" : "";
    var domains = root.RootElement.TryGetProperty("skinDomains", out var sd)
        ? sd.EnumerateArray().Select(x => x.GetString()).ToArray() : [];

    Check("根文档带 signaturePublickey", publicKey.Contains("BEGIN PUBLIC KEY"));
    Check("skinDomains 含 127.0.0.1", domains.Contains("127.0.0.1"));

    // ── ② 档案：id / name / properties ──
    var profileText = await http.GetStringAsync($"{relay.BaseUri}sessionserver/session/minecraft/profile/{uuid}");
    using var profile = System.Text.Json.JsonDocument.Parse(profileText);

    Check("档案 id 一致",
        string.Equals(profile.RootElement.GetProperty("id").GetString(), uuid, StringComparison.OrdinalIgnoreCase));
    Check("档案 name 一致", profile.RootElement.GetProperty("name").GetString() == name);

    var props = profile.RootElement.GetProperty("properties").EnumerateArray().ToList();
    Check("properties 恰好一条", props.Count == 1);

    var prop = props[0];
    var value = prop.GetProperty("value").GetString() ?? "";
    var signature = prop.GetProperty("signature").GetString() ?? "";

    // ── ③ 用 relay 公布的公钥验证签名（客户端就是这么做）──
    var verified = VerifySignature(publicKey, value, signature);
    Check("textures 属性签名可验证", verified);

    // ── ④ 解出 textures，看皮肤地址指不指向本中继 ──
    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    using var blob = System.Text.Json.JsonDocument.Parse(decoded);
    var skinUrl = blob.RootElement
        .GetProperty("textures").GetProperty("SKIN").GetProperty("url").GetString() ?? "";

    Check("皮肤地址指回本中继", skinUrl.StartsWith(relay.BaseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

    // ── ⑤ 皮肤 PNG 真的能下下来，而且和源文件一致 ──
    var downloaded = await http.GetByteArrayAsync(skinUrl);
    var original = await File.ReadAllBytesAsync(png);
    Check($"皮肤 PNG 可下载且字节一致（{downloaded.Length} 字节）", downloaded.SequenceEqual(original));

    // ── ⑥ 别的 UUID 应当返回 204（不能给谁都说有档案）──
    using var other = await http.GetAsync($"{relay.BaseUri}sessionserver/session/minecraft/profile/00000000000000000000000000000000");
    Check("其它 UUID 返回 204", (int)other.StatusCode == 204);

    // ── ⑦ 生命周期：反复起停，确认没有「未观察的任务异常」──
    //
    // 这条是回归测试。曾经的写法用 GetContextAsync().WaitAsync(token)，
    // 取消时留下一个没人管的 GetContextAsync 任务；Dispose 里 Close() 一调，
    // 它就带着 ObjectDisposedException 结束，而异常没人观察 ——
    // 终结器线程会把它重新抛成 AggregateException，
    // 用户看到的是「游戏一退出，主界面弹一个未处理的错误」。
    //
    // 这里装一个和 App 同款的 TaskScheduler.UnobservedTaskException，
    // 起停若干次后强制 GC 并等终结器跑完，看有没有异常冒出来。
    var unobserved = new List<Exception>();
    EventHandler<UnobservedTaskExceptionEventArgs> watcher = (_, e) =>
    {
        lock (unobserved) unobserved.Add(e.Exception);
        e.SetObserved();
    };

    TaskScheduler.UnobservedTaskException += watcher;
    try
    {
        for (var i = 0; i < 5; i++)
        {
            var temp = SkinRelay.TryCreate(uuid, name, png, slim: false, log);
            if (temp is null) break;

            // 打一个请求，让它把接客循环真的跑起来
            try { await http.GetStringAsync(temp.BaseUri); } catch { /* 不看结果 */ }

            temp.Dispose();
        }

        // 终结器要等 GC 才跑；这几步就是原来爆异常的时刻
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(600);
    }
    finally
    {
        TaskScheduler.UnobservedTaskException -= watcher;
    }

    Check($"反复起停 5 次没有未观察异常（收到 {unobserved.Count} 个）", unobserved.Count == 0);

    Console.WriteLine();
    if (failures.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("皮肤中继：全部通过。");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"皮肤中继：{failures.Count} 项失败");
        foreach (var f in failures) Console.WriteLine("  · " + f);
    }
    Console.ResetColor();

    void Check(string what, bool ok)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {what}");
        Console.ResetColor();
        if (!ok) failures.Add(what);
    }
}

/// <summary>用 PEM 公钥验 RSA-SHA1 签名（Mojang 的 textures 属性就是这个算法）。</summary>
static bool VerifySignature(string pem, string value, string signature)
{    try
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(pem);

        return rsa.VerifyData(
            System.Text.Encoding.UTF8.GetBytes(value),
            Convert.FromBase64String(signature),
            System.Security.Cryptography.HashAlgorithmName.SHA1,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    }
    catch
    {
        return false;
    }
}

