using System.Windows;
using System.Windows.Threading;
using LMCL.App.Services;
using LMCL.App.Views;
using LMCL.Core.Configuration;

namespace LMCL.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 启动阶段就可能抛异常（比如 XAML 解析失败），先把兜底装上
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        try
        {
            Services = new AppServices();
            Services.Initialize();
        }
        catch (Exception ex)
        {
            Report(ex, "初始化失败");
            Shutdown(1);
            return;
        }

        // 构造外壳窗口这一步本身就顺带验证了 ShellWindow.xaml
        var window = new ShellWindow();
        MainWindow = window;

        // LMCL.exe --self-check：把每个页面都构造一遍再退出，用来确认每一页都能打开。
        // 直接 Shutdown 而不是先 Close——OnMainWindowClose 会抢先以 0 退出，退出码就丢了。
        if (e.Args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            var failures = SelfCheck.Run();
            Shutdown(failures);
            return;
        }

        // LMCL.exe --preview-skin：直接打开「账号管理」并停在微软账号的皮肤页。
        //
        // 本机不一定有正版账号，没有的话「上传皮肤 / 切换披风」那几张卡平时看不到，
        // 版式就没法用肉眼过一遍。这个开关纯本地铺一份假数据，只为截图核对。
        if (e.Args.Any(a => string.Equals(a, "--preview-skin", StringComparison.OrdinalIgnoreCase)))
        {
            var preview = new Views.AccountWindow();
            preview.PreviewMicrosoftSkinForCheck();
            MainWindow = preview;
            preview.Show();
            return;
        }

        // LMCL.exe --preview-msgbox：把五种语气的消息框挨个弹一遍，只为肉眼核对观感。
        // 消息框平时都跟在某个操作后面，想一次看全五种得手动造五种场景，太麻烦。
        window.Show();

        var msgIndex = Array.FindIndex(e.Args,
            a => string.Equals(a, "--preview-msgbox", StringComparison.OrdinalIgnoreCase));
        if (msgIndex >= 0)
        {
            // 后面跟一个数字就只弹那一条（--preview-msgbox 3）
            var only = msgIndex + 1 < e.Args.Length ? e.Args[msgIndex + 1] : null;
            PreviewMessageBoxes(only);
        }
    }

    /// <summary>
    /// 开发用：预览消息框（见 --preview-msgbox）。
    /// 加第二个参数可以只弹其中一条，方便截图核对：
    /// <c>--preview-msgbox 3</c> 只弹「错误」。不传就五条依次弹一遍。
    /// </summary>
    private static void PreviewMessageBoxes(string? only = null)
    {
        var all = new Action[]
        {
            () => Ui.Info("这是一条普通信息。\n\n用来交代「事情办好了，不需要你做决定」。"),
            () => Ui.Success("操作成功。\n\n绿色对勾表示这件事已经完全办完。"),
            () => Ui.Warn("这是一条警告。\n\n黄色三角表示「能继续，但最好看一眼」。"),
            () => Ui.Error("这是一条错误提示。\n\n红色表示有东西真的失败了，通常还带原因。"),
            () => _ = Ui.Confirm("这是一个询问。\n\n有「是 / 否」两个按钮，返回你的选择。"),
            () => _ = Ui.ConfirmDanger("危险操作的确认。\n\n主按钮是红字，用来防手滑。", "最后确认"),
        };

        // 指定序号时只弹那一条（1 起算，和上面的顺序一致）
        if (int.TryParse(only, out var index) && index >= 1 && index <= all.Length)
        {
            all[index - 1]();
            return;
        }

        foreach (var show in all) show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 单个操作出错不该让整个启动器退出
        Report(e.Exception);
        e.Handled = true;
    }

    /// <summary>把异常写成崩溃日志并提示用户，绝不静默吞掉。</summary>
    private static void Report(Exception? exception, string? title = null)
    {
        if (exception is null) return;

        var path = "";
        try
        {
            AppPaths.EnsureCreated();
            path = Path.Combine(AppPaths.LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{title}\r\n\r\n{exception}");
        }
        catch
        {
            // 连日志都写不了就只能弹窗了
        }

        var message = $"{title ?? "发生了未处理的错误"}\r\n\r\n{exception.GetType().Name}：{exception.Message}"
                    + (path.Length > 0 ? $"\r\n\r\n详细信息已写入：\r\n{path}" : "");

        // 这个兜底必须**切回 UI 线程**再弹窗：
        // TaskScheduler.UnobservedTaskException 是在线程池线程上触发的，
        // 而 WPF 的窗口只能在 UI 线程上创建/显示 ——
        // 直接在那边弹会再抛一次，于是提示彻底消失。
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try { dispatcher.Invoke(() => ShowError(message)); }
            catch { /* 程序已经在退出，弹不出来了 */ }
            return;
        }

        ShowError(message);
    }

    /// <summary>
    /// 弹崩溃提示。优先用自绘的消息框（和界面一个风格）；
    /// 但本方法本身就是「异常处理也出问题」时的兜底，
    /// 万一自绘窗口都起不来（比如 XAML 资源坏了），
    /// 再抛一次就真的什么都看不到了 —— 那时退回系统 MessageBox，至少把话说清楚。
    /// </summary>
    private static void ShowError(string message)
    {
        try
        {
            Ui.Error(message);
        }
        catch
        {
            try
            {
                MessageBox.Show(message, "LMCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 连弹窗都失败就算了
            }
        }
    }
}
