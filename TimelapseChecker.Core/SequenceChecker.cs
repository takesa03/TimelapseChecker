namespace TimelapseChecker.Core;

/// <summary>
/// 連続フレームを順に受け取り、直近の正常フレーム(ローリング基準)と
/// 監視開始時の基準(セッション基準)との相対比較で異常を判定する。
/// 絶対輝度は一切使わない。
/// </summary>
public sealed class SequenceChecker
{
    // セッション基準は「最初の1枚（または指定した基準画像）」のみとする
    private const int SessionRefFrames = 1;

    private readonly CheckerThresholds th;
    private readonly List<FrameMetrics> normals = new();
    private readonly List<FrameMetrics> sessionFrames = new();
    private readonly List<double> recentAreas = new();

    public SequenceChecker(CheckerThresholds thresholds)
    {
        th = thresholds;
    }

    public void Reset()
    {
        normals.Clear();
        sessionFrames.Clear();
        recentAreas.Clear();
    }

    /// <summary>指定したフレームを基準として設定し直す（ローリング基準・セッション基準の両方）。</summary>
    /// <returns>端のノイズがあるフレームは基準にできないため、その場合はfalse（基準は変更しない）。</returns>
    public bool SetBaseline(FrameMetrics m)
    {
        if (HasEdgeArtifact(m)) return false;
        Reset();
        Accept(m);
        return true;
    }

    /// <summary>端の引き伸ばしノイズ（太陽が画角から外れた）があるフレームか。</summary>
    public bool HasEdgeArtifact(FrameMetrics m) => m.EdgeArtifactStrength >= th.EdgeArtifactWarning;

    private string ArtifactReason(FrameMetrics m) =>
        $"画像の{m.EdgeArtifactSide}側に引き伸ばしノイズ（太陽が画角から外れている可能性）";

    /// <summary>フレームを判定し、正常なら基準へ取り込む（監視中の通常の判定）。</summary>
    public CheckResult Evaluate(FrameMetrics m) => EvaluateCore(m, commit: true);

    /// <summary>
    /// フレームを現在の基準に対して判定するだけで、内部の状態は変えない。
    /// 基準画像にしようとしている画像が、エラー（注意・警告）と判定されるかの確認に使う。
    /// </summary>
    public CheckResult Inspect(FrameMetrics m) => EvaluateCore(m, commit: false);

    private CheckResult EvaluateCore(FrameMetrics m, bool commit)
    {
        // 基準が1枚も無い間は、このフレームを基準として採用する（ノイズ付きは基準にしない）
        if (normals.Count == 0)
        {
            if (HasEdgeArtifact(m))
            {
                return new CheckResult
                {
                    Metrics = m,
                    Level = CheckLevel.Warning,
                    Reasons = new[] { ArtifactReason(m) },
                };
            }
            if (commit) Accept(m);
            return new CheckResult { Metrics = m, Level = CheckLevel.Normal, IsCalibrating = true };
        }

        var b = Median(normals);
        var s = Median(sessionFrames);

        double r0 = Math.Sqrt(Math.Max(b.Area, 1) / Math.PI);
        double areaRatio = b.Area > 0 ? m.Area / b.Area : 1;
        double areaRatioSession = s.Area > 0 ? m.Area / s.Area : 1;
        double shift = Math.Sqrt(Math.Pow(m.CenterX - b.CenterX, 2) + Math.Pow(m.CenterY - b.CenterY, 2)) / r0;
        double contrastRatio = b.Contrast > 0 ? m.Contrast / b.Contrast : 1;
        double brightnessRatio = b.DiskBrightness > 0 ? m.DiskBrightness / b.DiskBrightness : 1;
        double rangeRatio = b.Range > 0 ? m.Range / b.Range : 1;
        double bgLift = m.BackgroundLevel - b.BackgroundLevel;

        var level = CheckLevel.Normal;
        var reasons = new List<string>();
        void Raise(CheckLevel l, string why)
        {
            if (l > level) level = l;
            reasons.Add(why);
        }

        double areaMin = Math.Min(areaRatio, areaRatioSession);
        if (areaMin < th.AreaWarning)
            Raise(CheckLevel.Warning, $"円盤の面積が基準の{areaMin:P0}に低下（雲・ルーフ遮蔽の可能性）");
        else if (areaMin < th.AreaCaution)
            Raise(CheckLevel.Caution, $"円盤の面積が基準の{areaMin:P0}に低下");

        if (shift > th.ShiftWarning)
            Raise(CheckLevel.Warning, $"太陽位置が半径比{shift:F2}ずれている（追尾エラーの可能性）");
        else if (shift > th.ShiftCaution)
            Raise(CheckLevel.Caution, $"太陽位置が半径比{shift:F2}ずれている");

        if (contrastRatio < th.ContrastWarning)
            Raise(CheckLevel.Warning, $"コントラストが基準の{contrastRatio:P0}に低下（雲）");
        else if (contrastRatio < th.ContrastCaution)
            Raise(CheckLevel.Caution, $"コントラストが基準の{contrastRatio:P0}に低下");

        if (brightnessRatio < th.BrightnessWarning)
            Raise(CheckLevel.Warning, $"円盤の明るさが基準の{brightnessRatio:P0}に低下（薄雲などの可能性）");
        else if (brightnessRatio < th.BrightnessCaution)
            Raise(CheckLevel.Caution, $"円盤の明るさが基準の{brightnessRatio:P0}に低下");

        if (bgLift > th.BackgroundWarning)
            Raise(CheckLevel.Warning, $"背景が持ち上がっている（+{bgLift:P0}）");
        else if (bgLift > th.BackgroundCaution)
            Raise(CheckLevel.Caution, $"背景が持ち上がっている（+{bgLift:P0}）");

        if (rangeRatio < th.RangeWarning)
            Raise(CheckLevel.Warning, $"明暗差が基準の{rangeRatio:P0}に低下（全面の雲の可能性）");

        if (HasEdgeArtifact(m))
            Raise(CheckLevel.Warning, ArtifactReason(m));

        if (m.TouchesEdge && !b.TouchesEdge)
            Raise(CheckLevel.Warning, "太陽が画像の端に接触（見切れ）");

        // 傾向: 面積が連続して減少中なら、閾値前でも注意（確認だけのときは、履歴を変えない）
        var areas = commit ? recentAreas : new List<double>(recentAreas);
        areas.Add(m.Area);
        if (areas.Count > 3) areas.RemoveAt(0);
        if (level == CheckLevel.Normal && th.TrendDrop > 0 && areas.Count == 3
            && areas[0] > areas[1] && areas[1] > areas[2]
            && b.Area > 0 && (areas[0] - areas[2]) / b.Area >= th.TrendDrop)
        {
            Raise(CheckLevel.Caution, "円盤の面積が連続して減少中");
        }

        if (level == CheckLevel.Normal && commit) Accept(m);

        return new CheckResult
        {
            Metrics = m,
            Level = level,
            Reasons = reasons,
            AreaRatio = areaRatio,
            AreaRatioSession = areaRatioSession,
            Shift = shift,
            ContrastRatio = contrastRatio,
            BrightnessRatio = brightnessRatio,
            RangeRatio = rangeRatio,
            BackgroundLift = bgLift,
        };
    }

    private void Accept(FrameMetrics m)
    {
        normals.Add(m);
        while (normals.Count > Math.Max(1, th.BaselineWindow)) normals.RemoveAt(0);
        if (sessionFrames.Count < SessionRefFrames) sessionFrames.Add(m);
    }

    private static FrameMetrics Median(List<FrameMetrics> list)
    {
        double Med(Func<FrameMetrics, double> sel)
        {
            var v = list.Select(sel).OrderBy(x => x).ToArray();
            int n = v.Length;
            return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
        }

        return new FrameMetrics
        {
            Area = Med(x => x.Area),
            CenterX = Med(x => x.CenterX),
            CenterY = Med(x => x.CenterY),
            Contrast = Med(x => x.Contrast),
            DiskBrightness = Med(x => x.DiskBrightness),
            Range = Med(x => x.Range),
            BackgroundLevel = Med(x => x.BackgroundLevel),
            TouchesEdge = list.Count(x => x.TouchesEdge) * 2 > list.Count,
        };
    }
}






