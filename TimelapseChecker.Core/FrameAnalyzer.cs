using OpenCvSharp;

namespace TimelapseChecker.Core;

/// <summary>
/// 画像を読み込み、太陽円盤の面積・位置・コントラスト等を抽出する。
/// 閾値はすべてフレーム自身の輝度分布(背景〜明部)に対する相対値で決めるため、
/// 露出・ゲイン・色味が撮影ごとに変わっても影響を受けにくい。
/// </summary>
public static class FrameAnalyzer
{
    public static FrameMetrics? Analyze(string path, int analysisWidth = 580, bool withPreview = true)
    {
        using var src = Cv2.ImRead(path, ImreadModes.Grayscale | ImreadModes.AnyDepth);
        if (src.Empty()) return null;

        using var gray8 = ToByte(src);
        var edges = EdgeRuns(gray8);

        Size size = gray8.Width > analysisWidth
            ? new Size(analysisWidth, Math.Max(1, (int)Math.Round(gray8.Height * (analysisWidth / (double)gray8.Width))))
            : new Size(gray8.Width, gray8.Height);

        using var small = new Mat();
        Cv2.Resize(gray8, small, size, 0, 0, InterpolationFlags.Area);
        // ホットピクセル等の孤立ノイズが重心・面積に影響しないよう軽く平滑化する
        Cv2.GaussianBlur(small, small, new Size(5, 5), 0);

        small.GetArray(out byte[] data);
        var hist = new int[256];
        foreach (byte b in data) hist[b]++;
        double bg = Percentile(hist, data.Length, 0.05);
        double hi = Percentile(hist, data.Length, 0.995);
        double range = hi - bg;

        // 背景〜明部の中間を閾値とする（フレーム内相対）
        double thr = bg + 0.5 * Math.Max(range, 1);
        using var mask = new Mat();
        Cv2.Threshold(small, mask, thr, 255, ThresholdTypes.Binary);
        using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
        {
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k);
        }

        int area = Cv2.CountNonZero(mask);
        double cx = 0, cy = 0, contrast = 0;
        if (area > 0)
        {
            var m = Cv2.Moments(mask, true);
            cx = m.M10 / m.M00;
            cy = m.M01 / m.M00;
            Cv2.MeanStdDev(small, out Scalar mean, out Scalar std, mask);
            contrast = mean.Val0 > 1 ? std.Val0 / mean.Val0 : 0;
        }

        const int edgeMargin = 3;
        bool edge = EdgeCount(mask.Row(0)) >= edgeMargin
                 || EdgeCount(mask.Row(mask.Rows - 1)) >= edgeMargin
                 || EdgeCount(mask.Col(0)) >= edgeMargin
                 || EdgeCount(mask.Col(mask.Cols - 1)) >= edgeMargin;

        // 端の行(列)が複製されており、かつ複製元に明暗変化がある辺を探す
        string[] sideNames = { "上", "下", "左", "右" };
        int artRun = 0;
        double artStrength = 0;
        string artSide = "";
        for (int i = 0; i < edges.Length; i++)
        {
            if (edges[i].Run < MinArtifactRun) continue;
            // 全面が平坦なフレーム(range≈0)で値が発散しないよう上限を設ける
            double s = Math.Min(edges[i].Std / Math.Max(range, 1), 1.0);
            if (s > artStrength)
            {
                artStrength = s;
                artRun = edges[i].Run;
                artSide = sideNames[i];
            }
        }

        byte[]? preview = null;
        if (withPreview)
        {
            Cv2.ImEncode(".jpg", small, out preview, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
        }

        return new FrameMetrics
        {
            FilePath = path,
            Area = area,
            CenterX = cx,
            CenterY = cy,
            Contrast = contrast,
            Range = range,
            BackgroundLevel = hi > 1 ? bg / hi : 0,
            TouchesEdge = edge,
            EdgeArtifactStrength = artStrength,
            EdgeArtifactRun = artRun,
            EdgeArtifactSide = artSide,
            Width = small.Width,
            Height = small.Height,
            PreviewJpeg = preview,
        };
    }

    /// <summary>引き伸ばしと見なす最小の複製行数（元画像のピクセル数）。</summary>
    private const int MinArtifactRun = 5;

    /// <summary>
    /// 各辺(上,下,左,右の順)から内側へ、直前の行と実質同一(平均差&lt;0.05)な行がどこまで続くかと、
    /// 複製元の行の標準偏差を返す。
    /// </summary>
    private static (int Run, double Std)[] EdgeRuns(Mat g8)
    {
        using var t = new Mat();
        Cv2.Transpose(g8, t);
        return new[]
        {
            RunFromEdge(g8, false),
            RunFromEdge(g8, true),
            RunFromEdge(t, false),
            RunFromEdge(t, true),
        };
    }

    private static (int Run, double Std) RunFromEdge(Mat m, bool fromEnd)
    {
        int rows = m.Rows;
        int max = rows / 4;
        int n = 0;
        using var diff = new Mat();
        for (; n < max; n++)
        {
            int i0 = fromEnd ? rows - 1 - n : n;
            int i1 = fromEnd ? i0 - 1 : i0 + 1;
            using var r0 = m.Row(i0);
            using var r1 = m.Row(i1);
            Cv2.Absdiff(r0, r1, diff);
            if (Cv2.Mean(diff).Val0 >= 0.05) break;
        }

        int srcIndex = fromEnd ? rows - 1 - n : n;
        using var srcRow = m.Row(srcIndex);
        Cv2.MeanStdDev(srcRow, out _, out Scalar std);
        return (n, std.Val0);
    }

    private static int EdgeCount(Mat line)
    {
        using (line) return Cv2.CountNonZero(line);
    }

    private static Mat ToByte(Mat src)
    {
        if (src.Depth() == MatType.CV_8U) return src.Clone();
        var dst = new Mat();
        // 16bit等は0〜255へ正規化（絶対値は後段で使わないため線形変換で十分）
        src.MinMaxLoc(out _, out double maxVal);
        double max = maxVal > 0 ? maxVal : 65535;
        src.ConvertTo(dst, MatType.CV_8U, 255.0 / max);
        return dst;
    }

    private static double Percentile(int[] hist, int total, double p)
    {
        long target = (long)Math.Ceiling(total * p);
        long acc = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return hist.Length - 1;
    }
}
