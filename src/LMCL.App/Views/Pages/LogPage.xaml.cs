using System.IO;
using System.Windows;
using LMCL.App.Services;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 运行日志页。
///
/// 日志本身由 <see cref="Controls.LogPanel"/> 显示（它绑的是全局 LogBus），
/// 这一页额外提供「打开日志目录」和「导出日志」两个操作 ——
/// 出问题要发给别人看的时候，导出比让人自己去 %APPDATA% 里翻方便得多。
/// </summary>
public partial class LogPage : PageBase
{
    public LogPage()
    {
        InitializeComponent();
        LogDirText.Text = AppPaths.LogDir;
    }

    /// <summary>
    /// 自检用：这一页的关键部件都真的建出来了吗。
    /// 「构造没抛异常」查不出「控件是空的」这类问题，所以给个显式断言。
    /// </summary>
    internal bool PanelReady =>
        LogDirText is not null
        && LogDirText.Text.Length > 0
        && BtnExport is not null
        && BtnOpenLogDir is not null;

    public override void OnNavigatedTo()
    {
        // 目录可能刚被清理过，每次进来重读一遍
        LogDirText.Text = AppPaths.LogDir;
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Ui.OpenFolder(AppPaths.LogDir);
        }
        catch (Exception ex)
        {
            S.Log.Warn($"打开日志目录失败：{ex.Message}");
            Ui.Warn($"打不开日志目录：\n\n{ex.Message}");
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var entries = S.Log.Recent;
        if (entries.Count == 0)
        {
            Ui.Info("现在还没有日志可以导出。");
            return;
        }

        var name = $"LMCL-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var target = Ui.PickSavePath("文本文件 (*.txt)|*.txt", name);
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var text = string.Join(Environment.NewLine, entries.Select(entry => entry.ToString()));
            File.WriteAllText(target, text);

            S.Log.Success($"日志已导出到 {target}");
            Ui.Info($"已导出 {entries.Count} 行日志到：\n\n{target}");
        }
        catch (Exception ex)
        {
            S.Log.Error($"导出日志失败：{ex.Message}");
            Ui.Error($"导出失败：\n\n{ex.Message}");
        }
    }
}
