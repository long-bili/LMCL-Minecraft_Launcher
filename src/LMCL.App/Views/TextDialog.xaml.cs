using System.Windows;
using System.Windows.Input;
using LMCL.App.Services;

namespace LMCL.App.Views;

/// <summary>
/// 只读的长文本查看器。启动参数、日志分析这类内容塞 MessageBox 会挤成一团，
/// 而且没法复制格式，所以单独开一个能滚动、能一键复制的窗口。
/// </summary>
public partial class TextDialog : Window
{
    public TextDialog()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 和账号窗口一样：铺云母，铺不上就用不透明的 DialogBrush 兜底。
        // 少了这一步，窗口背景会沿用被主窗口改透明的 WindowBrush，
        // 整个对话框渲染成一片白。
        WindowEffects.ApplyDialogBackdrop(this);
    }

    /// <summary>弹出只读文本窗口。owner 传当前窗口即可。</summary>
    public static void Present(Window? owner, string title, string body, string? hint = null)
    {
        var dialog = new TextDialog
        {
            Owner = owner,
            Title = title,
        };

        dialog.TitleText.Text = title;
        dialog.HintText.Text = hint ?? "";
        dialog.HintText.Visibility = string.IsNullOrWhiteSpace(hint)
            ? Visibility.Collapsed : Visibility.Visible;
        dialog.Body.Text = body;

        var lines = body.Length == 0 ? 0 : body.Split('\n').Length;
        dialog.SizeText.Text = $"{lines} 行 · {body.Length} 字符";

        dialog.ShowDialog();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;   // 纯查看窗口，不折腾最大化
        try { DragMove(); }
        catch { /* 快速连点时会抛，忽略 */ }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Ui.Copy(Body.Text);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
