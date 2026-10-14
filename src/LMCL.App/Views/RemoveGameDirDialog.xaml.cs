using System.Windows;
using LMCL.App.Services;

namespace LMCL.App.Views;

/// <summary>用户从「移除游戏目录」对话框里选了什么。</summary>
public enum GameDirRemoval
{
    /// <summary>取消了，什么都不做。</summary>
    Cancelled,

    /// <summary>只从启动器列表里去掉，磁盘上的文件一个不动。</summary>
    ListOnly,

    /// <summary>连整个文件夹一起删掉（已经过二次确认）。</summary>
    DeleteFolder,
}

/// <summary>
/// 「从列表里移除游戏目录」对话框。
///
/// 两个动作性质差得很远：只是不再显示 vs. 磁盘上的东西全没了。
/// 所以不写成「一个按钮 + 一个复选框」，而是**两个并列的选项**，各自的后果写在下面，
/// 让用户在动手之前就能看清区别；删文件夹那条还要再确认一次。
/// </summary>
public partial class RemoveGameDirDialog : Window
{
    private readonly string _path;

    /// <summary>用户的选择。关掉窗口不改这里就是 Cancelled。</summary>
    public GameDirRemoval Removal { get; private set; } = GameDirRemoval.Cancelled;

    public RemoveGameDirDialog(string path)
    {
        _path = path;
        InitializeComponent();

        DirPath.Text = path;
        DirInfo.Text = Describe(path);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 和别的对话框一样：主窗口把 WindowBrush 改成全透明了，
        // 这里不重新铺一次背景就会渲染成一片窟窿
        WindowEffects.ApplyDialogBackdrop(this);
    }

    /// <summary>说清楚里面有什么、有多大 —— 删之前总得让人知道要删掉多少东西。</summary>
    private static string Describe(string path)
    {
        if (!Directory.Exists(path)) return "（这个目录当前不存在，可能被移动或删掉了）";

        try
        {
            var versions = Directory.Exists(Path.Combine(path, "versions"))
                ? Directory.GetDirectories(Path.Combine(path, "versions")).Length
                : 0;

            var size = Core.Game.VersionService.DirectorySize(path);
            var sizeText = Core.Game.InstalledVersion.FormatSize(size);

            return versions > 0
                ? $"里面有 {versions} 个版本，约 {sizeText}。"
                : $"约 {sizeText}。";
        }
        catch (Exception ex)
        {
            return $"读不出目录信息：{ex.Message}";
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_path)) Ui.OpenFolder(_path);
            else Ui.Warn("这个目录已经不存在了。");
        }
        catch (Exception ex)
        {
            Ui.Warn($"打不开：\n\n{ex.Message}");
        }
    }

    private void RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        Removal = GameDirRemoval.ListOnly;
        DialogResult = true;
    }

    /// <summary>
    /// 删文件夹是不可恢复的，所以这里**再问一次**，而且默认按钮是「取消」。
    /// 用户点错了也只是多一次取消。
    /// </summary>
    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = Path.GetFileName(_path.TrimEnd(Path.DirectorySeparatorChar));

        var confirmed = Ui.ConfirmDanger(
            $"真的要删除磁盘上的整个文件夹吗？\n\n" +
            $"{_path}\n\n" +
            $"这会永久删掉「{name}」里的所有版本、存档、模组和配置。\n" +
            "删掉之后无法恢复，也不进回收站。",
            "最后确认：永久删除");

        if (!confirmed) return;

        Removal = GameDirRemoval.DeleteFolder;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
