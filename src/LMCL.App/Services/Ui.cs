using System.Diagnostics;
using System.Windows;
using LMCL.App.Views;

namespace LMCL.App.Services;

/// <summary>零碎的系统交互：弹窗、打开目录、复制。集中一处免得各页各写一套。</summary>
public static class Ui
{
    // 弹窗全部走自绘的 MessageDialog（WinUI 风格），不再用 Win32 的 MessageBox。
    // 后者画的是系统那套样式，跟启动器的云母 / 圆角 / 深色完全是两回事，弹出来很突兀。
    // 全项目 80 多处提示都经过这里，所以只改这一处就够。

    public static void Info(string message, string title = "LMCL") =>
        MessageDialog.Show(message, title, MessageKind.Info);

    public static void Success(string message, string title = "LMCL") =>
        MessageDialog.Show(message, title, MessageKind.Success);

    public static void Warn(string message, string title = "LMCL") =>
        MessageDialog.Show(message, title, MessageKind.Warning);

    public static void Error(string message, string title = "出错了") =>
        MessageDialog.Show(message, title, MessageKind.Error);

    public static bool Confirm(string message, string title = "确认操作") =>
        MessageDialog.Ask(message, title, MessageKind.Question);

    /// <summary>
    /// 危险操作的确认：文案是「删除 / 取消」，主按钮用红字而不是红底 ——
    /// 暗色主题下红底配白字反而看不清。
    /// </summary>
    public static bool ConfirmDanger(string message, string title = "确认操作",
        string okText = "删除") =>
        MessageDialog.Ask(message, title, MessageKind.Warning, okText, "取消", danger: true);

    public static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板被别的程序占用时会抛，忽略 */ }
    }

    /// <summary>用资源管理器打开目录；目录不存在就先建出来。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error($"打开目录失败：{ex.Message}");
        }
    }

    /// <summary>打开目录并选中指定文件。</summary>
    public static void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error($"打开文件位置失败：{ex.Message}");
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error($"打开链接失败：{ex.Message}");
        }
    }

    /// <summary>在系统的「打开文件」对话框里选文件。</summary>
    public static string[] PickFiles(string filter, bool multi = true)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Multiselect = multi,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    /// <summary>在系统的「新建文件」对话框里选保存位置。</summary>
    public static string? PickSavePath(string filter, string defaultName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>文件夹选择框（WPF 自带的不支持只选目录，绕一下 Shell API）。</summary>
    public static string? PickFolder(string? initial = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            dialog.InitialDirectory = initial!;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public static string FormatSize(long bytes) => Core.Game.InstalledVersion.FormatSize(bytes);
}
