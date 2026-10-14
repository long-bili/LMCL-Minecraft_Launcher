using CommunityToolkit.Mvvm.ComponentModel;
using LMCL.Core.Game;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 版本列表里的一行。
///
/// 体积要后台算（几千个文件的目录，同步扫会卡住界面），所以 SizeText 得是可变的，
/// 算完再由后台回填——这就是它要实现 INotifyPropertyChanged 的原因。
/// </summary>
public sealed partial class VersionRow : ObservableObject
{
    public VersionRow(InstalledVersion model)
    {
        Model = model;
        SizeText = model.SizeBytes > 0 ? model.SizeText : "统计中…";
    }

    public InstalledVersion Model { get; }

    public string Name => Model.Name;

    public string Subtitle => Model.Subtitle;

    public string LoaderLabel => Model.LoaderLabel;

    public bool Isolated => Model.Isolated;

    public string Meta =>
        $"{Model.InstalledAt:yyyy-MM-dd HH:mm} 安装"
        + (Model.LoaderVersion.Length > 0 ? $"  ·  加载器版本 {Model.LoaderVersion}" : "")
        + (Model.Isolated ? "  ·  独立目录" : "  ·  与全局共用目录");

    [ObservableProperty]
    private string _sizeText;

    /// <summary>这个版本是不是首页当前要显示/启动的版本（等于 Config.LastVersion）。</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
