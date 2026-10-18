namespace TimelapseChecker.Core;

/// <summary>1枚の画像から抽出した、絶対輝度に依存しない特徴量。</summary>
public sealed class FrameMetrics
{
    public string FilePath { get; init; } = "";

    /// <summary>円盤マスクの面積（解析用縮小画像上のピクセル数）。</summary>
    public double Area { get; init; }

    /// <summary>円盤マスクの重心（縮小画像上の座標）。</summary>
    public double CenterX { get; init; }
    public double CenterY { get; init; }

    /// <summary>円盤内の変動係数（標準偏差/平均）。コントラスト指標。</summary>
    public double Contrast { get; init; }

    /// <summary>画像の明部(上位0.5%)と背景(下位5%)の差。フレーム内の相対ダイナミックレンジ。</summary>
    public double Range { get; init; }

    /// <summary>背景レベルの明部に対する比（0〜1）。雲による背景の持ち上がりを表す。</summary>
    public double BackgroundLevel { get; init; }

    /// <summary>円盤マスクが画像の端に接しているか。</summary>
    public bool TouchesEdge { get; init; }

    /// <summary>
    /// 画像処理(位置合わせ)で太陽が画角から外れたときに端に現れる「引き伸ばしノイズ」の強さ。
    /// 端の行(列)が複製されて縞状になる現象で、複製元の行の明暗変化を、画像の明暗差(背景〜明部)に対する比で表す。
    /// 背景だけが複製されている通常の位置合わせでは0付近になる。
    /// </summary>
    public double EdgeArtifactStrength { get; init; }

    /// <summary>引き伸ばしノイズの複製行数（元画像のピクセル数）。</summary>
    public int EdgeArtifactRun { get; init; }

    /// <summary>引き伸ばしノイズが現れている辺（上/下/左/右）。なければ空。</summary>
    public string EdgeArtifactSide { get; init; } = "";

    /// <summary>解析用縮小画像の幅/高さ。</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>円盤の明るさ（円盤マスク内の平均、固定スケールの0〜255）。基準画像との比で減光を見る。</summary>
    public double DiskBrightness { get; init; }

    /// <summary>プレビュー表示用のJPEGデータ（比較用＝解析に使うグレースケール画像を縮小したもの）。</summary>
    public byte[]? PreviewJpeg { get; init; }

    /// <summary>プレビュー表示用のJPEGデータ（元画像のカラーを縮小したもの）。</summary>
    public byte[]? OriginalJpeg { get; init; }
}

