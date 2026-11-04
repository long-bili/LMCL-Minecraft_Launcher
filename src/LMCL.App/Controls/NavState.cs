using System.Windows;

namespace LMCL.App.Controls;

/// <summary>
/// 侧边栏「收起 / 展开」状态的附加属性。
///
/// 为什么用附加属性、而不是让外层容器把宽度「挤」小：
/// 侧边栏收窄时，里面的 <c>ListBoxItem</c> 仍会被 <c>ListBox</c> 撑到旧宽度，
/// 高亮块就被拉成长方形（用户一眼就看出来了），图标也不会居中。
/// 所以状态要**显式告诉每一项**，让它自己切模板：
/// 高亮块收成 38×38 的正方形、图标居中。
///
/// 加法：`local:NavState.IsCollapsed="True"`。
/// 挂在 <c>ListBox</c> 上会继承给所有子项（<see cref="FrameworkPropertyMetadata.Inherits"/>），
/// 所以整棵树只要设一处。
/// </summary>
public static class NavState
{
    public static readonly DependencyProperty IsCollapsedProperty =
        DependencyProperty.RegisterAttached(
            "IsCollapsed",
            typeof(bool),
            typeof(NavState),
            new FrameworkPropertyMetadata(
                false,
                // 关键：向下继承，挂在 ListBox 上一处生效，不用逐项设
                FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCollapsed(DependencyObject obj) =>
        (bool)obj.GetValue(IsCollapsedProperty);

    public static void SetIsCollapsed(DependencyObject obj, bool value) =>
        obj.SetValue(IsCollapsedProperty, value);
}
