using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimelapseChecker.App.Services;
using TimelapseChecker.Core;

namespace TimelapseChecker.App;

public class ResultItem
{
    public DateTime Time { get; init; }
    public string FileName { get; init; } = "";
    public string LevelText { get; init; } = "";
    public Brush LevelBrush { get; init; } = Brushes.Gray;
    public string Detail { get; init; } = "";
    public string Reason { get; init; } = "";
}

public partial class MainViewModel : ObservableObject
{
    private const int MaxHistory = 500;

    private readonly JsonStore<AppSettings> settingsStore = new("app_settings.json");
    private readonly AlarmPlayer alarm = new();
    private FolderMonitor? monitor;
    private bool alarmAcknowledged;

    public AppSettings Settings { get; }

    public ObservableCollection<ResultItem> History { get; } = new();

    [ObservableProperty]
    private bool isMonitoring;

    [ObservableProperty]
    private CheckLevel level = CheckLevel.Normal;

    [ObservableProperty]
    private string bannerText = "停止中";

    [ObservableProperty]
    private Brush bannerBrush = Brushes.DimGray;

    [ObservableProperty]
    private string reasonText = "";

    [ObservableProperty]
    private string latestFile = "";

    [ObservableProperty]
    private string metricsText = "";

    [ObservableProperty]
    private ImageSource? previewImage;

    [ObservableProperty]
    private ImageSource? baselinePreview;

    [ObservableProperty]
    private string statusMessage = "監視フォルダを指定して「監視開始」を押してください。";

    /// <summary>警告音が鳴っている間だけtrue（「確認」ボタンの表示条件）。</summary>
    [ObservableProperty]
    private bool canAcknowledge;

    /// <summary>「確認」済みで、音を停止している間だけtrue（確認済み表示の条件）。</summary>
    [ObservableProperty]
    private bool isAcknowledged;

    private void SetAcknowledged(bool value)
    {
        alarmAcknowledged = value;
        IsAcknowledged = value;
    }

    public string WatchFolder
    {
        get => Settings.WatchFolder;
        set
        {
            if (Settings.WatchFolder == value) return;
            Settings.WatchFolder = value;
            OnPropertyChanged();
            // 次回起動時に復元できるよう、変更のたびに保存する
            SaveSettings();
        }
    }

    [ObservableProperty]
    private string baselineText = "（未設定）";

    private string latestPath = "";

    public string SoundFilePath
    {
        get => Settings.SoundFilePath;
        set
        {
            if (Settings.SoundFilePath == value) return;
            Settings.SoundFilePath = value;
            OnPropertyChanged();
        }
    }

    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
    private static readonly Brush CautionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC7, 0x78, 0x00)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));
    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4F)));

    public MainViewModel()
    {
        Settings = settingsStore.Load();
        BannerBrush = IdleBrush;
        alarm.Error += msg => StatusMessage = msg;
        InitializeGraph();
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public void SaveSettings() => settingsStore.Save(Settings);

    public void StartIfConfigured()
    {
        if (Settings.AutoStart && Directory.Exists(Settings.WatchFolder) && !IsMonitoring)
        {
            Start();
        }
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (IsMonitoring) Stop(); else Start();
    }

    private void Start()
    {
        if (!Directory.Exists(Settings.WatchFolder))
        {
            StatusMessage = "監視フォルダが存在しません。";
            return;
        }

        try
        {
            monitor = new FolderMonitor(Settings.Thresholds, Settings.Extensions);
            monitor.FrameChecked += OnFrameChecked;
            monitor.Message += msg => Application.Current.Dispatcher.BeginInvoke(() => StatusMessage = msg);
            monitor.BaselineChanged += OnBaselineChanged;
            monitor.Start(Settings.WatchFolder);
        }
        catch (Exception ex)
        {
            StatusMessage = $"監視を開始できませんでした: {ex.Message}";
            monitor?.Dispose();
            monitor = null;
            return;
        }

        SaveSettings();
        IsMonitoring = true;
        Level = CheckLevel.Normal;
        BannerText = "監視中（基準画像を取得中）";
        BannerBrush = NormalBrush;
        ReasonText = "";
        BaselineText = "（フォルダ内の最初の画像を使用 / 画像が無い場合は最初に出力された画像）";
        latestPath = "";
        BaselinePreview = null;
        PreviewImage = null;
        ResetGraph();
        StatusMessage = $"監視中: {Settings.WatchFolder}";
    }

    private void OnBaselineChanged(FrameMetrics baseline)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!IsMonitoring) return;
            string path = baseline.FilePath;
            BaselineText = path;
            // 指定した画像が意図通りか確認できるよう、基準画像のプレビューを更新する
            if (baseline.PreviewJpeg != null) BaselinePreview = ToImage(baseline.PreviewJpeg);
            Level = CheckLevel.Normal;
            BannerText = "監視中";
            BannerBrush = NormalBrush;
            ReasonText = "";
            alarm.Stop();
            SetAcknowledged(false);
            CanAcknowledge = false;
            StatusMessage = $"基準画像: {Path.GetFileName(path)}";
        });
    }

    /// <summary>指定した画像を基準にし直す。</summary>
    public async void SetBaselineFile(string path)
    {
        if (monitor == null) return;
        await monitor.SetBaselineAsync(path);
    }

    private void Stop()
    {
        monitor?.Dispose();
        monitor = null;
        alarm.Stop();
        SetAcknowledged(false);
        CanAcknowledge = false;
        IsMonitoring = false;
        BannerText = "停止中";
        BannerBrush = IdleBrush;
        ReasonText = "";
        StatusMessage = "監視を停止しました。";
    }

    public void Shutdown()
    {
        SaveSettings();
        monitor?.Dispose();
        monitor = null;
        alarm.Stop();
    }

    [RelayCommand]
    private void Acknowledge()
    {
        alarm.Stop();
        SetAcknowledged(true);
        CanAcknowledge = false;
    }

    [RelayCommand]
    private void UseLatestAsBaseline()
    {
        if (monitor == null || string.IsNullOrEmpty(latestPath))
        {
            StatusMessage = "基準にできる最新画像がまだありません。";
            return;
        }
        SetBaselineFile(latestPath);
    }

    [RelayCommand]
    private void TestSound()
    {
        alarm.SetSoundFile(Settings.SoundFilePath);
        alarm.PlayOnce();
    }

    private void OnFrameChecked(CheckResult r)
    {
        // 解析はワーカースレッドで行われるため、UI更新はDispatcher経由
        Application.Current.Dispatcher.BeginInvoke(() => Apply(r));
    }

    private void Apply(CheckResult r)
    {
        if (!IsMonitoring) return;

        var m = r.Metrics;
        string name = Path.GetFileName(m.FilePath);
        var now = DateTime.Now;

        Level = r.Level;
        latestPath = m.FilePath;
        LatestFile = name;
        ReasonText = string.Join("\n", r.Reasons);

        (BannerText, BannerBrush) = r.Level switch
        {
            CheckLevel.Warning => ("警告", WarningBrush),
            CheckLevel.Caution => ("注意", CautionBrush),
            _ => (r.IsCalibrating ? "正常（基準取得）" : "正常", NormalBrush),
        };

        string detail = r.IsCalibrating
            ? "基準フレーム"
            : $"面積 {r.AreaRatio:P0} / ずれ {r.Shift:F2} / コントラスト {r.ContrastRatio:P0} / 背景 {r.BackgroundLift:+0.00;-0.00;0.00}";

        var sb = new StringBuilder();
        sb.AppendLine($"面積比（直近基準）: {r.AreaRatio:P1}");
        sb.AppendLine($"面積比（開始時基準）: {r.AreaRatioSession:P1}");
        sb.AppendLine($"重心ずれ（半径比）: {r.Shift:F3}");
        sb.AppendLine($"コントラスト比: {r.ContrastRatio:P1}");
        sb.AppendLine($"明暗差の比: {r.RangeRatio:P1}");
        sb.AppendLine($"背景の持ち上がり: {r.BackgroundLift:+0.000;-0.000;0.000}");
        sb.Append($"画像端への接触: {(m.TouchesEdge ? "あり" : "なし")}");
        MetricsText = sb.ToString();

        if (m.PreviewJpeg != null) PreviewImage = ToImage(m.PreviewJpeg);

        History.Insert(0, new ResultItem
        {
            Time = now,
            FileName = name,
            LevelText = r.Level switch { CheckLevel.Warning => "警告", CheckLevel.Caution => "注意", _ => "正常" },
            LevelBrush = r.Level switch { CheckLevel.Warning => WarningBrush, CheckLevel.Caution => CautionBrush, _ => NormalBrush },
            Detail = detail,
            Reason = string.Join(" / ", r.Reasons),
        });
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);

        AppendLog(now, name, r);
        AddGraphPoint(now, r);
        UpdateAlarm(r.Level);
    }

    private void UpdateAlarm(CheckLevel lv)
    {
        bool wantSound = Settings.SoundEnabled &&
                         (lv == CheckLevel.Warning || (lv == CheckLevel.Caution && Settings.SoundOnCaution));

        if (lv == CheckLevel.Normal)
        {
            alarm.Stop();
            SetAcknowledged(false);
            CanAcknowledge = false;
            return;
        }

        // 新しい画像で問題が判定されたら、以前の「確認」は解除して警告を再開する
        // （確認は、次の画像が届くまでの一時的な消音として扱う）
        SetAcknowledged(false);

        if (lv == CheckLevel.Warning)
        {
            // 警告時はウィンドウを前面に出して気付きやすくする
            var w = Application.Current.MainWindow;
            if (w != null)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
            }
        }

        if (wantSound && !alarmAcknowledged && !alarm.IsRunning)
        {
            alarm.Start(Settings.SoundFilePath, Settings.RepeatSeconds);
        }

        // 音が鳴っている間だけ「確認」ボタンを出す
        CanAcknowledge = alarm.IsRunning;
    }

    private static void AppendLog(DateTime now, string name, CheckResult r)
    {
        try
        {
            const string header = "time,file,level,area_ratio,area_ratio_session,shift,contrast_ratio,range_ratio,bg_lift,edge,edge_artifact,edge_artifact_side,edge_artifact_run,reasons";
            string path = Path.Combine(AppPaths.LogDirectory, $"{now:yyyyMMdd}.csv");

            // 列構成が異なる旧形式のファイルが残っている場合は、列がずれないよう退避して新しいファイルを作る
            if (File.Exists(path))
            {
                string? first;
                using (var rd = new StreamReader(path, Encoding.UTF8)) first = rd.ReadLine();
                if (first != header)
                {
                    string backup = Path.Combine(AppPaths.LogDirectory, $"{now:yyyyMMdd}_old.csv");
                    int n = 1;
                    while (File.Exists(backup))
                        backup = Path.Combine(AppPaths.LogDirectory, $"{now:yyyyMMdd}_old{++n}.csv");
                    File.Move(path, backup);
                }
            }

            bool isNew = !File.Exists(path);
            using var w = new StreamWriter(path, true, new UTF8Encoding(true));
            if (isNew) w.WriteLine(header);
            w.WriteLine(string.Join(",",
                now.ToString("yyyy-MM-dd HH:mm:ss"),
                "\"" + name + "\"",
                r.Level,
                r.AreaRatio.ToString("F3"),
                r.AreaRatioSession.ToString("F3"),
                r.Shift.ToString("F3"),
                r.ContrastRatio.ToString("F3"),
                r.RangeRatio.ToString("F3"),
                r.BackgroundLift.ToString("F3"),
                r.Metrics.TouchesEdge ? 1 : 0,
                r.Metrics.EdgeArtifactStrength.ToString("F4"),
                r.Metrics.EdgeArtifactSide,
                r.Metrics.EdgeArtifactRun,
                "\"" + string.Join(" / ", r.Reasons) + "\""));
        }
        catch
        {
            // ログ書き込み失敗で監視を止めない
        }
    }

    private static BitmapImage ToImage(byte[] jpeg)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(jpeg);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

