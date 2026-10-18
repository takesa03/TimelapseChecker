namespace TimelapseChecker.Core;

public enum CheckLevel
{
    Normal = 0,
    Caution = 1,
    Warning = 2,
}

public sealed class CheckResult
{
    public FrameMetrics Metrics { get; init; } = null!;
    public CheckLevel Level { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>基準が揃うまでの間は判定しない（常にNormal扱い）。</summary>
    public bool IsCalibrating { get; init; }

    /// <summary>面積比（ローリング基準）。</summary>
    public double AreaRatio { get; init; } = 1;
    /// <summary>面積比（セッション基準）。</summary>
    public double AreaRatioSession { get; init; } = 1;
    /// <summary>重心ずれ（円盤半径比）。</summary>
    public double Shift { get; init; }
    public double ContrastRatio { get; init; } = 1;
    /// <summary>円盤の明るさの、基準に対する比。</summary>
    public double BrightnessRatio { get; init; } = 1;
    public double RangeRatio { get; init; } = 1;
    public double BackgroundLift { get; init; }
}

