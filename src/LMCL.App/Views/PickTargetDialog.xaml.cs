using System.Windows;
using System.Windows.Controls;
using LMCL.App.Services;
using LMCL.Core.Game;
using LMCL.Core.Resources;

namespace LMCL.App.Views;

/// <summary>
/// 「装到哪个版本」的对话框。
///
/// 光影包这类东西是**全版本通用**的：一份 zip 对所有游戏版本都能用，
/// 所以下载页那边不需要先选目标实例，等用户真按了「安装」再问放哪儿就行。
/// 这里列出本机已装的版本，让用户挑一个；也可以手选一个任意文件夹。
///
/// <see cref="TargetDirectory"/> 非 null 表示用户选好了。
/// </summary>
public partial class PickTargetDialog : Window
{
    /// <summary>用户选定的落地目录。null = 取消了。</summary>
    public string? TargetDirectory { get; private set; }

    /// <summary>用户选的版本名（手选文件夹时为 null），只用于提示文案。</summary>
    public string? TargetVersionName { get; private set; }

    /// <summary>
    /// 自检用：这个对话框至少给了用户一条出路吗（有版本可选，或者能手选文件夹）。
    /// 「打开是一片空白、什么都点不了」是构造阶段查不出来的毛病，得断言。
    /// </summary>
    internal bool HasChoicesOrFallback =>
        VersionList.Items.Count > 0 || BtnOk is not null;

    private readonly AppServices _services;
    private readonly ResourceKind _kind;
    private readonly string _fileName;
    private readonly long _size;
    private readonly List<VersionChoice> _choices;

    private string? _customDirectory;
    private bool _ready;

    /// <param name="services">服务容器。</param>
    /// <param name="kind">放进哪个资源子目录（光影 / 资源包 / 模组…）。</param>
    /// <param name="fileName">即将落地的文件名，用来判断「是不是已经装过」。</param>
    /// <param name="size">文件大小，底部提示用。</param>
    /// <param name="itemTitle">下载的东西叫什么，标题里显示。</param>
    public PickTargetDialog(
        AppServices services, ResourceKind kind,
        string fileName, long size, string itemTitle)
    {
        _services = services;
        _kind = kind;
        _fileName = fileName;
        _size = size;

        InitializeComponent();

        HeaderText.Text = $"安装「{itemTitle}」到";
        IntroText.Text = $"「{ResourceLabels.Of(kind)}」与游戏版本无关，选一个版本放进去就行。";

        _choices = services.Versions.List()
            .Select(v => new VersionChoice(v, services, kind, fileName))
            .ToList();

        _ready = true;
        VersionList.ItemsSource = _choices;

        // 默认选中上次玩的那个版本，省一次点击
        var preferred = _choices.FirstOrDefault(c => c.Version.Name == services.Config.Current.LastVersion)
                        ?? _choices.FirstOrDefault();

        if (preferred is not null) VersionList.SelectedItem = preferred;
        else
        {
            PathHint.Text = "本机还没有任何版本。可以先「选择其他文件夹」，或者先去「游戏版本」分区装一个。";
        }

        SizeText.Text = size > 0 ? $"约 {InstalledVersion.FormatSize(size)}" : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 和别的对话框一样：主窗口把 WindowBrush 改成了全透明，
        // 这里不重新铺一次背景就会渲染成一片窟窿
        WindowEffects.ApplyDialogBackdrop(this);
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        if (VersionList.SelectedItem is not VersionChoice choice)
        {
            BtnOk.IsEnabled = _customDirectory is not null;
            return;
        }

        _customDirectory = null;
        CustomPath.Text = "";
        PathHint.Text = choice.Hint;
        BtnOk.IsEnabled = true;
        BtnOk.Content = choice.AlreadyInstalled ? "重新安装" : "安装到这里";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picked = Ui.PickFolder(_services.Config.Current.GameDir);
        if (string.IsNullOrWhiteSpace(picked)) return;

        _customDirectory = picked;
        CustomPath.Text = picked;

        // 手选目录之后，列表里的版本选择就不再代表目标了
        _ready = false;
        VersionList.SelectedIndex = -1;
        _ready = true;

        PathHint.Text = $"会放进：{picked}";
        BtnOk.IsEnabled = true;
        BtnOk.Content = "安装到这里";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_customDirectory is { Length: > 0 })
        {
            TargetDirectory = _customDirectory;
            TargetVersionName = null;
        }
        else if (VersionList.SelectedItem is VersionChoice choice)
        {
            TargetDirectory = choice.Directory;
            TargetVersionName = choice.Version.Name;
        }
        else
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>版本列表里的一行。把「目录算好了没、装过没」这些一次算清楚。</summary>
    private sealed class VersionChoice
    {
        public VersionChoice(InstalledVersion version, AppServices services, ResourceKind kind, string fileName)
        {
            Version = version;
            Directory = services.Resources.DirectoryFor(version.Name, version.Isolated, kind);

            // 目录里已经有同名文件就提示会覆盖 —— 光影包重名很常见（都叫 Complementary.zip）
            AlreadyInstalled = File.Exists(Path.Combine(Directory, fileName));

            Hint = AlreadyInstalled
                ? $"{version.Name} 里已经有同名文件，安装会覆盖它。"
                : $"会放进：{Directory}";
        }

        public InstalledVersion Version { get; }

        public string Directory { get; }

        public bool AlreadyInstalled { get; }

        public string Hint { get; }

        public override string ToString() =>
            $"{Version.Name}（{(Version.Loader == LoaderKind.Vanilla ? "原版" : $"{Version.LoaderLabel} {Version.BaseVersion}")}）";
    }
}
