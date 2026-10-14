using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LMCL.Core.Resources;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 资源列表里的一行。
///
/// ResourceFile 的 Enabled 是只读的（它描述的是「扫描那一刻磁盘上的状态」），
/// 所以开关状态由这一层自己拿着，改完再往磁盘上落。
/// </summary>
public sealed partial class ResourceRow : ObservableObject
{
    public ResourceRow(ResourceFile model, bool canToggle)
    {
        Model = model;
        CanToggle = canToggle;
        _enabled = model.Enabled;
    }

    public ResourceFile Model { get; }

    /// <summary>存档用 .disabled 改名去「禁用」没有意义，所以不给开关。</summary>
    public bool CanToggle { get; }

    public string Name => Model.Name;

    public string Meta => $"{Model.SizeText}　·　{Model.ModifiedText}";

    public Visibility ToggleVisibility => CanToggle ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DisabledHintVisibility =>
        CanToggle && !Enabled ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(DisabledHintVisibility));
}
