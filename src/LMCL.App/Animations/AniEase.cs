using System.Windows;
using System.Windows.Media.Animation;

namespace LMCL.App.Animations;

/// <summary>缓动强度。对应 PCL 的 <c>AniEasePower</c>。</summary>
public enum AniEasePower
{
    Weak = 2,
    Middle = 3,
    Strong = 4,
    ExtraStrong = 5,
}

/// <summary>
/// 缓动函数。公式对齐 PCL 的 <c>ModAnimation</c>：
/// 输入输出都归一化到 [0, 1]，<see cref="GetValue"/> 是「进度 → 已播放比例」的映射。
/// </summary>
public abstract class AniEase
{
    /// <summary>进度 t（0~1）对应的动画位置（0~1）。</summary>
    public abstract double GetValue(double t);

    public double GetDelta(double t1, double t0) => GetValue(t1) - GetValue(t0);

    // ── 预置 ──────────────────────────────────────────────

    public static AniEase Linear { get; } = new LinearEase();

    public static AniEase InFluent(AniEasePower power = AniEasePower.Middle) => new InFluentEase(power);

    public static AniEase OutFluent(AniEasePower power = AniEasePower.Middle) => new OutFluentEase(power);

    public static AniEase InoutFluent(AniEasePower power = AniEasePower.Middle, double middle = 0.5) =>
        new InoutEase(new InFluentEase(power), new OutFluentEase(power), middle);

    public static AniEase OutBack(AniEasePower power = AniEasePower.Middle) => new OutBackEase(power);

    public static AniEase InBack(AniEasePower power = AniEasePower.Middle) => new InBackEase(power);

    /// <summary>短平滑开始、回弹结束——下拉菜单、弹出面板用这个最有「果冻感」。</summary>
    public static AniEase OutCar(AniEasePower power = AniEasePower.Middle) =>
        new InoutEase(new InFluentEase(power), new OutBackEase(power), 0.3);

    public static AniEase OutElastic(AniEasePower power = AniEasePower.Middle) => new OutElasticEase(power);

    private static double Clamp(double t) => t < 0 ? 0 : t > 1 ? 1 : t;

    private sealed class LinearEase : AniEase
    {
        public override double GetValue(double t) => Clamp(t);
    }

    /// <summary>t^p——起步很慢，适合「进入」。</summary>
    private sealed class InFluentEase(AniEasePower power) : AniEase
    {
        private readonly double _p = (double)power;

        public override double GetValue(double t) => Math.Pow(Clamp(t), _p);
    }

    /// <summary>1-(1-t)^p——收尾很慢，适合「离开」。</summary>
    private sealed class OutFluentEase(AniEasePower power) : AniEase
    {
        private readonly double _p = (double)power;

        public override double GetValue(double t) => 1 - Math.Pow(1 - Clamp(t), _p);
    }

    /// <summary>两段拼接：前 EaseInPercent 用 EaseIn，剩下用 EaseOut。</summary>
    private sealed class InoutEase(AniEase easeIn, AniEase easeOut, double easeInPercent) : AniEase
    {
        public override double GetValue(double t)
        {
            t = Clamp(t);
            return t < easeInPercent
                ? easeInPercent * easeIn.GetValue(t / easeInPercent)
                : (1 - easeInPercent) * easeOut.GetValue((t - easeInPercent) / (1 - easeInPercent)) + easeInPercent;
        }
    }

    private sealed class InBackEase(AniEasePower power) : AniEase
    {
        private readonly double _p = 3 - (double)power * 0.5;

        public override double GetValue(double t)
        {
            t = Clamp(t);
            return Math.Pow(t, _p) * Math.Cos(1.5 * Math.PI * (1 - t));
        }
    }

    private sealed class OutBackEase(AniEasePower power) : AniEase
    {
        private readonly double _p = 3 - (double)power * 0.5;

        public override double GetValue(double t)
        {
            t = Clamp(t);
            return 1 - Math.Pow(1 - t, _p) * Math.Cos(1.5 * Math.PI * t);
        }
    }

    private sealed class OutElasticEase(AniEasePower power) : AniEase
    {
        private readonly int _p = (int)power + 4;

        public override double GetValue(double percent)
        {
            var t = 1 - Clamp(percent);
            return 1 - Math.Pow(t, (_p - 1) * 0.25) * Math.Cos((_p - 3.5) * Math.PI * Math.Pow(1 - t, 1.5));
        }
    }
}

/// <summary>
/// 把 <see cref="AniEase"/> 适配成 WPF 的 <see cref="IEasingFunction"/>，
/// 这样 DoubleAnimation 就能直接用上面这些缓动。
/// </summary>
public sealed class AniEasingFunction : EasingFunctionBase
{
    public required AniEase Curve { get; init; }

    protected override double EaseInCore(double normalizedTime) => Curve.GetValue(normalizedTime);

    protected override Freezable CreateInstanceCore() => new AniEasingFunction { Curve = Curve };
}
