namespace TimelapseChecker.Core;

/// <summary>判定閾値。すべて基準フレームに対する相対値。</summary>
public sealed class CheckerThresholds
{
    /// <summary>面積比がこの値を下回ると「注意」。</summary>
    public double AreaCaution { get; set; } = 0.92;
    /// <summary>面積比がこの値を下回ると「警告」。</summary>
    public double AreaWarning { get; set; } = 0.85;

    /// <summary>重心ずれ（円盤半径に対する比）がこの値を超えると「注意」。</summary>
    public double ShiftCaution { get; set; } = 0.08;
    /// <summary>重心ずれがこの値を超えると「警告」。</summary>
    public double ShiftWarning { get; set; } = 0.15;

    /// <summary>コントラスト比がこの値を下回ると「注意」。</summary>
    public double ContrastCaution { get; set; } = 0.8;
    /// <summary>コントラスト比がこの値を下回ると「警告」。</summary>
    public double ContrastWarning { get; set; } = 0.5;

    /// <summary>円盤の明るさが基準画像に対してこの比を下回ると「注意」（薄雲などによる全体の減光）。</summary>
    public double BrightnessCaution { get; set; } = 0.90;
    /// <summary>円盤の明るさが基準画像に対してこの比を下回ると「警告」。</summary>
    public double BrightnessWarning { get; set; } = 0.75;

    /// <summary>背景持ち上がり（基準からの増加分）がこの値を超えると「注意」。</summary>
    public double BackgroundCaution { get; set; } = 0.10;
    /// <summary>背景持ち上がりがこの値を超えると「警告」。</summary>
    public double BackgroundWarning { get; set; } = 0.25;

    /// <summary>ダイナミックレンジ比がこの値を下回ると「警告」（全面雲等）。</summary>
    public double RangeWarning { get; set; } = 0.4;

    /// <summary>
    /// 端の引き伸ばしノイズ（太陽が画角から外れたときに現れる縞）の強さがこの値以上で「警告」。
    /// 画像の明暗差(背景〜明部)に対する比。通常の位置合わせでは0.01未満。
    /// </summary>
    public double EdgeArtifactWarning { get; set; } = 0.02;

    /// <summary>面積が連続して減少している場合の「注意」判定に使う、累積減少率。0で無効。</summary>
    public double TrendDrop { get; set; } = 0.05;

    /// <summary>ローリング基準に使う直近の正常フレーム数。</summary>
    public int BaselineWindow { get; set; } = 10;
}

