using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;
using TimelapseChecker.Core;

namespace TimelapseChecker.App;

/// <summary>指標の推移グラフ（直近30分固定・リアルタイム表示のみ）。</summary>
public partial class MainViewModel
{
    private static readonly TimeSpan GraphSpan = TimeSpan.FromMinutes(30);

    private record GraphPoint(DateTime Time, double Area, double Shift);

    private readonly List<GraphPoint> graphPoints = new();
    private DispatcherTimer? graphTimer;

    /// <summary>幅が狭い配信用レイアウトのときtrue。横軸の罫線・ラベルを間引く。</summary>
    public bool CompactGraph
    {
        get => compactGraph;
        set
        {
            if (compactGraph == value) return;
            compactGraph = value;
            RefreshGraph();
        }
    }
    private bool compactGraph;

    [ObservableProperty]
    private ISeries[] areaSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] areaXAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] areaYAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private ISeries[] shiftSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] shiftXAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] shiftYAxes = Array.Empty<Axis>();

    // 軸ごとに毎回新しいインスタンスを生成する（LiveChartsのPaintは共有できないため）
    private static SolidColorPaint NewAxisLabelPaint() => new(new SKColor(0xDC, 0xDC, 0xDC));
    private static SolidColorPaint NewAxisSeparatorPaint() => new(new SKColor(0x3F, 0x3F, 0x46));

    private static readonly SKColor LineColor = new(0x37, 0x8A, 0xDD);
    private static readonly SKColor CautionColor = new(0xED, 0xA1, 0x00);
    private static readonly SKColor WarningColor = new(0xE3, 0x49, 0x48);

    private void InitializeGraph()
    {
        // 横軸は「現在時刻」まで伸びるため、画像が届かない間も定期的に更新する
        graphTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        graphTimer.Tick += (_, _) =>
        {
            if (IsMonitoring) RefreshGraph();
        };
        graphTimer.Start();
    }

    private void ResetGraph()
    {
        graphPoints.Clear();
        RefreshGraph();
    }

    private void AddGraphPoint(DateTime time, CheckResult r)
    {
        graphPoints.Add(new GraphPoint(time, r.AreaRatio, r.Shift));
        RefreshGraph();
    }

    /// <summary>ウィンドウが実サイズを持ってから呼ぶ（初期化時にゼロサイズのまま描画されるのを避ける）。</summary>
    public void RefreshGraph()
    {
        try
        {
            RefreshGraphCore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshGraph 例外: {ex.Message}");
        }
    }

    private void RefreshGraphCore()
    {
        var now = DateTime.Now;
        var from = now - GraphSpan;
        graphPoints.RemoveAll(p => p.Time < from.AddMinutes(-1));
        var pts = graphPoints.ToArray();
        var th = Settings.Thresholds;

        AreaSeries = BuildSeries(
            pts.Select(p => new DateTimePoint(p.Time, p.Area)).ToArray(),
            from, now, th.AreaCaution, th.AreaWarning);
        ShiftSeries = BuildSeries(
            pts.Select(p => new DateTimePoint(p.Time, p.Shift)).ToArray(),
            from, now, th.ShiftCaution, th.ShiftWarning);

        var separators = ComputeSeparators(from, now, TimeSpan.FromMinutes(CompactGraph ? 10 : 5));

        Axis MakeTimeAxis() => new()
        {
            Labeler = value => new DateTime((long)value).ToString("HH:mm"),
            LabelsRotation = 0,
            // 選択範囲(直近30分)の幅で固定し、罫線は5分刻みのキリの良い時刻に揃える
            MinLimit = from.Ticks,
            MaxLimit = now.Ticks,
            CustomSeparators = separators,
            LabelsPaint = NewAxisLabelPaint(),
            SeparatorsPaint = NewAxisSeparatorPaint(),
            // 既定の文字サイズ(16)だとグラフからはみ出すため小さくする
            TextSize = AxisTextSize,
        };

        AreaXAxes = new[] { MakeTimeAxis() };
        ShiftXAxes = new[] { MakeTimeAxis() };

        double areaMax = Math.Max(1.2, pts.Length > 0 ? pts.Max(p => p.Area) * 1.1 : 0);
        double shiftMax = Math.Max(0.3, pts.Length > 0 ? pts.Max(p => p.Shift) * 1.2 : 0);

        AreaYAxes = new[]
        {
            new Axis
            {
                Name = "面積比",
                NamePaint = NewAxisLabelPaint(),
                LabelsPaint = NewAxisLabelPaint(),
                SeparatorsPaint = NewAxisSeparatorPaint(),
                TextSize = AxisTextSize,
                NameTextSize = AxisNameTextSize,
                MinLimit = 0,
                MaxLimit = areaMax,
            },
        };

        ShiftYAxes = new[]
        {
            new Axis
            {
                Name = "ずれ(半径比)",
                NamePaint = NewAxisLabelPaint(),
                LabelsPaint = NewAxisLabelPaint(),
                SeparatorsPaint = NewAxisSeparatorPaint(),
                TextSize = AxisTextSize,
                NameTextSize = AxisNameTextSize,
                MinLimit = 0,
                MaxLimit = shiftMax,
            },
        };
    }

    /// <summary>実測値の折れ線と、注意(破線)・警告(点線)の閾値線を作る。</summary>
    private static ISeries[] BuildSeries(DateTimePoint[] values, DateTime from, DateTime to, double caution, double warning)
    {
        LineSeries<DateTimePoint> Threshold(double y, SKColor color, float[] dash) => new()
        {
            Values = new[] { new DateTimePoint(from, y), new DateTimePoint(to, y) },
            Stroke = new SolidColorPaint(color, 1.5f) { PathEffect = new DashEffect(dash) },
            Fill = null,
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
        };

        return new ISeries[]
        {
            new LineSeries<DateTimePoint>
            {
                Values = values,
                Stroke = new SolidColorPaint(LineColor, 2),
                Fill = null,
                GeometrySize = 5,
                GeometryFill = new SolidColorPaint(LineColor.WithAlpha(200)),
                GeometryStroke = null,
            },
            Threshold(caution, CautionColor, new float[] { 6, 4 }),
            Threshold(warning, WarningColor, new float[] { 2, 3 }),
        };
    }

    private double AxisTextSize => CompactGraph ? 10 : 11;
    private double AxisNameTextSize => CompactGraph ? 10 : 12;

    private static List<double> ComputeSeparators(DateTime from, DateTime to, TimeSpan step)
    {
        var list = new List<double>();
        var midnight = to.Date;
        double index = Math.Floor((from - midnight).Ticks / (double)step.Ticks);
        var cursor = midnight + TimeSpan.FromTicks((long)(index * step.Ticks));
        while (cursor <= to)
        {
            if (cursor >= from) list.Add(cursor.Ticks);
            cursor += step;
        }
        return list;
    }
}






