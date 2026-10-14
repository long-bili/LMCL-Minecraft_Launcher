using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading;

namespace LMCL.App.Animations;

/// <summary>
/// 属性动画入口。命名对齐 PCL 的 <c>ModAnimation</c>（Aa 前缀 = Animation of ...），
/// 让「页面切换淡入」「列表依次滑入」「菜单回弹展开」这类效果一行就能挂上：
/// <code>
/// Animator.AaStack(MyStackPanel);                                  // 子项依次浮入
/// Animator.AaOpacity(card, 1, 200, 100, AniEase.OutFluent());      // 延迟淡入
/// Animator.AaTranslateY(panel, 0, 300, 0, AniEase.OutCar());       // 回弹归位
/// </code>
///
/// 实现走 WPF 原生的 <see cref="DoubleAnimation"/> + <see cref="AniEasingFunction"/>，
/// 不引第三方库；系统关掉动画效果时（「淡化菜单效果」关闭）全部直接到位。
/// </summary>
public static class Animator
{
    /// <summary>
    /// 系统是否允许动画。读的是「淡化菜单效果」——
    /// 用户在系统设置里关掉动画效果时，所有 Aa 方法都会直接设终值、不播放。
    /// </summary>
    public static bool Enabled { get; } = SystemParameters.ClientAreaAnimation;

    // ── 透明度 ────────────────────────────────────────────

    /// <summary>透明度动画。time / delay 单位毫秒。from 指定起始值（不填则从当前值起）。</summary>
    public static void AaOpacity(FrameworkElement obj, double to, double time = 200, double delay = 0,
        AniEase? ease = null, Action? onDone = null, double? from = null)
    {
        if (obj is null) return;

        if (!Enabled || time <= 0)
        {
            obj.Opacity = to;
            onDone?.Invoke();
            return;
        }

        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(time))
        {
            EasingFunction = new AniEasingFunction { Curve = ease ?? AniEase.OutFluent() },
            BeginTime = TimeSpan.FromMilliseconds(delay),
        };
        if (from.HasValue) anim.From = from.Value;
        HookDone(anim, onDone);
        obj.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ── 位移 ──────────────────────────────────────────────

    /// <summary>水平位移（RenderTransform，不触发重新布局）。</summary>
    public static void AaTranslateX(FrameworkElement obj, double to, double time = 300, double delay = 0,
        AniEase? ease = null, Action? onDone = null, double? from = null)
    {
        if (obj is null) return;
        var tt = EnsureTranslate(obj);
        AnimateProperty(to, time, delay, ease, onDone, TranslateTransform.XProperty, tt, from);
    }

    /// <summary>垂直位移（RenderTransform，不触发重新布局）。</summary>
    public static void AaTranslateY(FrameworkElement obj, double to, double time = 300, double delay = 0,
        AniEase? ease = null, Action? onDone = null, double? from = null)
    {
        if (obj is null) return;
        var tt = EnsureTranslate(obj);
        AnimateProperty(to, time, delay, ease, onDone, TranslateTransform.YProperty, tt, from);
    }

    // ── 缩放 ──────────────────────────────────────────────

    /// <summary>整体缩放。默认绕中心缩放（自动补 RenderTransformOrigin）。from 可指定起始缩放。</summary>
    public static void AaScale(FrameworkElement obj, double to, double time = 300, double delay = 0,
        AniEase? ease = null, Action? onDone = null, double from = 1)
    {
        if (obj is null) return;

        var st = EnsureScale(obj);
        if (st is null) return;

        if (!Enabled || time <= 0)
        {
            st.ScaleX = to;
            st.ScaleY = to;
            onDone?.Invoke();
            return;
        }

        var easeFunc = new AniEasingFunction { Curve = ease ?? AniEase.OutFluent() };
        var begin = TimeSpan.FromMilliseconds(delay);
        foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(time))
            {
                EasingFunction = easeFunc,
                BeginTime = begin,
            };
            if (from != 1) anim.From = from;
            HookDone(anim, onDone);
            st.BeginAnimation(prop, anim);
        }
    }

    // ── 延迟执行 ──────────────────────────────────────────

    /// <summary>延迟一段时间后执行代码（在主线程）。动画被系统关掉时立即执行。</summary>
    public static void AaCode(Action code, double delay = 0)
    {
        if (!Enabled || delay <= 0)
        {
            code();
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            code();
        };
        timer.Start();
    }

    // ── 列表依次进入 ──────────────────────────────────────

    /// <summary>
    /// StackPanel 的子项依次浮入（第 i 项延迟 i × <paramref name="delayPerItem"/>）。
    /// 对齐 PCL 的 <c>AaStack</c>，但在它的基础上加了「缩放弹出」：
    /// 每项从 <paramref name="fromScale"/> 稍微放大回 1，配合上移与淡入，
    /// 收尾走 OutCar 的回弹，看着像一片片卡片「弹」到位。
    /// </summary>
    public static void AaStack(Panel panel, double time = 240, double delayPerItem = 32, double slide = 18,
        AniEase? ease = null, double fromScale = 0.965)
    {
        if (panel is null || panel.Children.Count == 0) return;

        if (!Enabled)
        {
            // 动画关掉：全部直接到位，别把元素留在透明状态
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement fe) continue;
                fe.Opacity = 1;
                if (fe.RenderTransform is TranslateTransform tt) tt.Y = 0;
                ResetScale(fe);
            }
            return;
        }

        var useEase = ease ?? AniEase.OutCar();
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement child) continue;

            var delay = i * delayPerItem;
            child.Opacity = 0;
            var tt = EnsureTranslate(child);
            tt.Y = slide;
            AaOpacity(child, 1, time, delay, AniEase.OutFluent(), from: 0);
            AaTranslateY(child, 0, time, delay, useEase, from: slide);
            if (fromScale < 1) AaScale(child, 1, time, delay, useEase, from: fromScale);
        }
    }

    // ── 组合入口 ──────────────────────────────────────────

    /// <summary>
    /// 页面进入：淡入 + 从下方滑入 + 轻微放大回弹。
    /// 三层叠加（透明度 / 位移 / 缩放）比单纯淡入更有「推到位」的力度，
    /// 收尾的缩放走 OutCar 会有一点点过冲再归位。
    /// </summary>
    public static void AaPageEnter(FrameworkElement page, double slide = 22, double time = 340,
        double fromScale = 1.03)
    {
        if (page is null) return;

        if (!Enabled)
        {
            page.Opacity = 1;
            ResetScale(page);
            return;
        }

        page.Opacity = 0;
        var tt = EnsureTranslate(page);
        tt.Y = slide;
        // From 显式给起点：上次退场留下的 hold 值不会把这次的起点带偏
        AaOpacity(page, 1, time, 0, AniEase.OutFluent(AniEasePower.Strong), from: 0);
        AaTranslateY(page, 0, time, 0, AniEase.OutCar(AniEasePower.Strong), from: slide);
        if (fromScale != 1) AaScale(page, 1, time, 0, AniEase.OutCar(), from: fromScale);
    }

    /// <summary>
    /// 页面退出：淡出 + 轻微缩小 + 上移，动作比进入快得多（退场要干脆）。
    /// 播完调用 <paramref name="onDone"/>——宿主靠它把内容换掉。
    /// </summary>
    public static void AaPageExit(FrameworkElement page, Action? onDone = null, double time = 140, double slide = 18)
    {
        if (page is null)
        {
            onDone?.Invoke();
            return;
        }

        if (!Enabled)
        {
            onDone?.Invoke();
            return;
        }

        var tt = EnsureTranslate(page);
        var done = 0;
        void Once()
        {
            if (Interlocked.Increment(ref done) == 1) onDone?.Invoke();
        }

        // 三个属性谁先播完都行，用一次性守卫保证只回调一次
        AaOpacity(page, 0, time, 0, AniEase.InFluent(AniEasePower.Strong), Once);
        AaTranslateY(page, -slide, time, 0, AniEase.InFluent(AniEasePower.Strong), Once);
        AaScale(page, 0.988, time, 0, AniEase.InFluent(AniEasePower.Strong), Once);
    }

    /// <summary>
    /// 浮出层展开：轻微放大 + 淡入，收尾带回弹（OutCar）。
    /// 用于 Popup / 下拉菜单，比 WPF 自带的 PopupAnimation="Fade" 有质感。
    /// </summary>
    public static void AaPopupOpen(FrameworkElement surface, double time = 200)
    {
        if (surface is null) return;

        if (!Enabled)
        {
            surface.Opacity = 1;
            return;
        }

        surface.Opacity = 0;
        // 必须显式给起点：否则缩放是「从当前值到 1」，第一次开就恰好是 1→1，等于没动
        AaScale(surface, 1, time, 0, AniEase.OutCar(), from: 0.94);
        AaOpacity(surface, 1, time, 0, AniEase.OutFluent(), from: 0);
    }

    /// <summary>
    /// 列表项依次浮入。等容器生成后再动（<see cref="DispatcherPriority.Loaded"/>），
    /// 所以 ItemsSource 赋值完直接调就行。虚拟化列表只有可见项有容器，
    /// 动画它们就够——不可见的本来也看不见。
    /// </summary>
    public static void AaItems(ItemsControl list, double time = 260, double delayPerItem = 34, double slide = 20,
        double fromScale = 0.96)
    {
        if (list is null || !Enabled) return;

        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            var index = 0;
            foreach (var item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement fe) continue;

                var delay = index * delayPerItem;
                index++;

                fe.Opacity = 0;
                var tt = EnsureTranslate(fe);
                tt.Y = slide;
                AaOpacity(fe, 1, time, delay, AniEase.OutFluent(), from: 0);
                AaTranslateY(fe, 0, time, delay, AniEase.OutCar(), from: slide);
                if (fromScale < 1) AaScale(fe, 1, time, delay, AniEase.OutCar(), from: fromScale);
            }
        }), DispatcherPriority.Loaded);
    }

    // ── 数字滚动 ──────────────────────────────────────────

    /// <summary>
    /// 数字从 <paramref name="from"/> 滚到 <paramref name="to"/>（缓动），适合主页统计那种「跳出来」的数字。
    /// 同一个 TextBlock 上重复调用会先停掉上一轮，不会几个定时器叠着打架。
    /// </summary>
    public static void AaNumber(TextBlock text, double from, double to, double time = 720, double delay = 0,
        AniEase? ease = null, string format = "0", string suffix = "")
    {
        if (text is null) return;

        // 停掉这个 TextBlock 上还没跑完的上一轮
        if (text.Tag is DispatcherTimer running) { running.Stop(); text.Tag = null; }

        if (!Enabled || time <= 0)
        {
            text.Text = to.ToString(format) + suffix;
            return;
        }

        var useEase = ease ?? AniEase.OutFluent(AniEasePower.Strong);
        var start = DateTime.Now + TimeSpan.FromMilliseconds(delay);
        text.Text = from.ToString(format) + suffix;

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.Now - start).TotalMilliseconds;
            if (elapsed < 0) return;                     // 还在延迟里，先不动

            var t = elapsed / time;
            if (t >= 1) { t = 1; timer.Stop(); text.Tag = null; }

            var value = from + (to - from) * useEase.GetValue(t);
            text.Text = value.ToString(format) + suffix;
        };

        text.Tag = timer;
        timer.Start();
    }

    /// <summary>停掉 <see cref="AaNumber"/> 在这个 TextBlock 上还没跑完的滚动。</summary>
    public static void CancelNumber(TextBlock text)
    {
        if (text?.Tag is DispatcherTimer t) { t.Stop(); text.Tag = null; }
    }

    // ── 内部 ──────────────────────────────────────────────

    private static void AnimateProperty(double to, double time, double delay, AniEase? ease, Action? onDone,
        DependencyProperty property, DependencyObject target, double? from = null)
    {
        if (!Enabled || time <= 0)
        {
            switch (property.Name)
            {
                case nameof(TranslateTransform.X) when target is TranslateTransform tx: tx.X = to; break;
                case nameof(TranslateTransform.Y) when target is TranslateTransform ty: ty.Y = to; break;
            }
            onDone?.Invoke();
            return;
        }

        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(time))
        {
            EasingFunction = new AniEasingFunction { Curve = ease ?? AniEase.OutFluent() },
            BeginTime = TimeSpan.FromMilliseconds(delay),
        };
        if (from.HasValue) anim.From = from.Value;
        HookDone(anim, onDone);
        (target as TranslateTransform)?.BeginAnimation(property, anim);
    }

    private static void HookDone(DoubleAnimation anim, Action? onDone)
    {
        if (onDone is null) return;

        anim.Completed += (s, e) =>
        {
            // 动画被同属性上的新动画顶掉时 Completed 也会触发，
            // 此时 ClockState 是 Active 之外的 Stopped——两种情况都算结束，直接回调
            onDone();
        };
    }

    /// <summary>拿到（没有就建）一个挂在 RenderTransform 上的 TranslateTransform。</summary>
    private static TranslateTransform EnsureTranslate(FrameworkElement obj)
    {
        if (obj.RenderTransform is TranslateTransform existing) return existing;

        if (obj.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            // 复用组里已有的平移，别每次都追加一个：页面会被缓存复用，
            // 每切一次都加一个新的，旧值的 HoldEnd 会一直叠在组里，
            // 表现就是「连续切换左边栏页面，页面越来越往下偏」。
            foreach (var child in group.Children)
                if (child is TranslateTransform t) return t;

            var tt = new TranslateTransform();
            group.Children.Add(tt);
            return tt;
        }

        if (obj.RenderTransform is Transform other && other != Transform.Identity && !other.IsFrozen)
        {
            var ng = new TransformGroup();
            ng.Children.Add(other);
            var tt = new TranslateTransform();
            ng.Children.Add(tt);
            obj.RenderTransform = ng;
            return tt;
        }

        var alone = new TranslateTransform();
        obj.RenderTransform = alone;
        return alone;
    }

    /// <summary>拿到（没有就建）一个挂在 RenderTransform 上的 ScaleTransform。绕中心缩放。</summary>
    private static ScaleTransform? EnsureScale(FrameworkElement obj)
    {
        if (obj.RenderTransform is ScaleTransform existingScale) return existingScale;

        if (obj.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            // 同 EnsureTranslate：复用组里已有的缩放，避免重复追加导致变换叠加
            foreach (var child in group.Children)
                if (child is ScaleTransform s) return s;

            var st = new ScaleTransform(1, 1);
            group.Children.Add(st);
            if (obj.RenderTransformOrigin == default)
                obj.RenderTransformOrigin = new Point(0.5, 0.5);
            return st;
        }

        if (obj.RenderTransform is Transform other && other != Transform.Identity && !other.IsFrozen)
        {
            var ng = new TransformGroup();
            ng.Children.Add(other);
            var st = new ScaleTransform(1, 1);
            ng.Children.Add(st);
            obj.RenderTransform = ng;
            if (obj.RenderTransformOrigin == default)
                obj.RenderTransformOrigin = new Point(0.5, 0.5);
            return st;
        }

        var alone = new ScaleTransform(1, 1);
        obj.RenderTransform = alone;
        if (obj.RenderTransformOrigin == default)
            obj.RenderTransformOrigin = new Point(0.5, 0.5);
        return alone;
    }

    /// <summary>把元素上挂着的缩放复位到 1（动画被系统关掉时用）。</summary>
    private static void ResetScale(FrameworkElement obj)
    {
        switch (obj.RenderTransform)
        {
            case ScaleTransform st:
                st.ScaleX = 1;
                st.ScaleY = 1;
                break;
            case TransformGroup group:
                foreach (var child in group.Children)
                    if (child is ScaleTransform s) { s.ScaleX = 1; s.ScaleY = 1; }
                break;
        }
    }
}
