using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    // 画面に出しているプレビュー（「元画像で表示」の切り替えに応じて、下の4つのどれかを指す）
    [ObservableProperty]
    private ImageSource? previewImage;

    [ObservableProperty]
    private ImageSource? baselinePreview;

    // 比較用（解析に使うグレースケール）と元画像（カラー）の両方を保持し、表示だけを切り替える
    private ImageSource? baselineGray, baselineOriginal, latestGray, latestOriginal;

    /// <summary>true: 元画像（カラー）で表示、false: 比較用（解析用のグレースケール）で表示。</summary>
    public bool ShowOriginalImage
    {
        get => Settings.ShowOriginalImage;
        set
        {
            if (Settings.ShowOriginalImage == value) return;
            Settings.ShowOriginalImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewModeText));
            UpdatePreviewSelection();
            SaveSettings();
        }
    }

    public string PreviewModeText => ShowOriginalImage ? "元画像" : "比較用";

    private void UpdatePreviewSelection()
    {
        BaselinePreview = ShowOriginalImage ? baselineOriginal : baselineGray;
        PreviewImage = ShowOriginalImage ? latestOriginal : latestGray;
        RefreshEnlarged();
    }

    // ---- 拡大表示（基準画像・最新画像を、ウィンドウいっぱいに表示する） ----
    private enum EnlargeTarget { None, Baseline, Latest }
    private EnlargeTarget enlargeTarget = EnlargeTarget.None;

    [ObservableProperty]
    private ImageSource? enlargedImage;

    [ObservableProperty]
    private string enlargedTitle = "";

    [ObservableProperty]
    private bool isEnlarged;

    private string latestName = "";
    private string baselineName = "";

    [RelayCommand]
    private void EnlargeBaseline() => Enlarge(EnlargeTarget.Baseline);

    [RelayCommand]
    private void EnlargeLatest() => Enlarge(EnlargeTarget.Latest);

    [RelayCommand]
    private void CloseEnlarged() => Enlarge(EnlargeTarget.None);

    private void Enlarge(EnlargeTarget target)
    {
        enlargeTarget = target;
        RefreshEnlarged();
    }

    private void RefreshEnlarged()
    {
        ImageSource? img = enlargeTarget switch
        {
            EnlargeTarget.Baseline => BaselinePreview,
            EnlargeTarget.Latest => PreviewImage,
            _ => null,
        };
        if (img == null)
        {
            enlargeTarget = EnlargeTarget.None;
            IsEnlarged = false;
            EnlargedImage = null;
            EnlargedTitle = "";
            return;
        }
        EnlargedImage = img;
        EnlargedTitle = (enlargeTarget == EnlargeTarget.Baseline ? "基準画像　" + baselineName : "最新画像　" + latestName)
                        + $"　（{PreviewModeText}。クリックまたは Esc で閉じる）";
        IsEnlarged = true;
    }

    // ---- 画像のカウント表示 ----
    /// <summary>監視フォルダに含まれるTIFFの枚数（大きく表示）。既存の画像も含む。</summary>
    [ObservableProperty]
    private int totalImages;

    /// <summary>異常（注意・警告）の件数（小さく表示）。判定履歴にリストアップされている件数。</summary>
    [ObservableProperty]
    private string errorCountText = "異常 0 件（注意 0・警告 0）";

    private DispatcherTimer? fileCountTimer;
    private bool fileCountBusy;

    private void InitializeCounts()
    {
        // 履歴の増減に合わせて、異常の件数を数え直す
        History.CollectionChanged += (_, _) => UpdateErrorCount();

        // 監視フォルダのTIFF枚数は、定期的に数え直す（撮影ソフトが新しい画像を出し続けるため）
        fileCountTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        fileCountTimer.Tick += (_, _) => RefreshFileCount();
        fileCountTimer.Start();
        RefreshFileCount();
    }

    /// <summary>監視フォルダ直下の対象ファイル（既定は .tif/.tiff）の枚数を数える。フォルダが多くても画面が止まらないよう別スレッドで行う。</summary>
    public async void RefreshFileCount()
    {
        if (fileCountBusy) return;

        string folder = Settings.WatchFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            TotalImages = 0;
            return;
        }

        var exts = Settings.CountExtensions
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        fileCountBusy = true;
        try
        {
            int n = await Task.Run(() => Directory.EnumerateFiles(folder).Count(f => exts.Contains(Path.GetExtension(f))));
            TotalImages = n;
        }
        catch
        {
            // フォルダを読めない間は、前回の値のままにする
        }
        finally
        {
            fileCountBusy = false;
        }
    }

    private void UpdateErrorCount()
    {
        int caution = 0, warning = 0;
        foreach (var item in History)
        {
            if (item.LevelText == "注意") caution++;
            else if (item.LevelText == "警告") warning++;
        }
        ErrorCountText = $"異常 {caution + warning} 件（注意 {caution}・警告 {warning}）";
    }

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
            RefreshFileCount();
            // 次回起動時に復元できるよう、変更のたびに保存する
            SaveSettings();
        }
    }

    private string monitoredFolder = "";

    /// <summary>
    /// フォルダ欄の入力確定（Enter／フォーカス移動／参照）で呼ぶ。監視中にフォルダが変わっていたら、
    /// 新しいフォルダで監視し直す（存在しないフォルダなら、そのままにしてメッセージを出す）。
    /// </summary>
    public void ApplyWatchFolderChange()
    {
        if (!IsMonitoring || string.Equals(monitoredFolder, Settings.WatchFolder, StringComparison.OrdinalIgnoreCase)) return;

        if (!Directory.Exists(Settings.WatchFolder))
        {
            StatusMessage = "入力したフォルダが存在しないため、監視中のフォルダは変更しません。";
            return;
        }

        Stop();
        Start();
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
        InitializeCounts();
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
        if (IsMonitoring) Stop(); else Start(userInitiated: true);
    }

    /// <summary>操作（ボタン）に対するエラーは、画面下部ではなく警告ダイアログで知らせる。</summary>
    private static void ShowWarning(string message)
    {
        MessageBox.Show(Application.Current.MainWindow, message, "Timelapse Checker", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <param name="userInitiated">「監視開始」ボタンなど、利用者の操作による開始か（失敗を警告ダイアログで知らせる）。</param>
    private void Start(bool userInitiated = false)
    {
        if (!Directory.Exists(Settings.WatchFolder))
        {
            if (userInitiated) ShowWarning("監視フォルダが存在しません。\nフォルダを指定してから、監視を開始してください。");
            else StatusMessage = "監視フォルダが存在しません。";
            return;
        }

        try
        {
            monitor = new FolderMonitor(Settings.Thresholds, Settings.Extensions);
            monitor.FrameChecked += OnFrameChecked;
            monitor.Message += msg => Application.Current.Dispatcher.BeginInvoke(() => StatusMessage = msg);
            monitor.BaselineChanged += OnBaselineChanged;
            monitor.Start(Settings.WatchFolder);
            monitoredFolder = Settings.WatchFolder;
        }
        catch (Exception ex)
        {
            monitor?.Dispose();
            monitor = null;
            if (userInitiated) ShowWarning($"監視を開始できませんでした。\n{ex.Message}");
            else StatusMessage = $"監視を開始できませんでした: {ex.Message}";
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
        baselineGray = baselineOriginal = latestGray = latestOriginal = null;
        baselineName = latestName = "";
        UpdatePreviewSelection();
        RefreshFileCount();
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
            baselineName = Path.GetFileName(path);
            baselineGray = baseline.PreviewJpeg != null ? ToImage(baseline.PreviewJpeg) : null;
            baselineOriginal = baseline.OriginalJpeg != null ? ToImage(baseline.OriginalJpeg) : null;
            UpdatePreviewSelection();
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
        // 「指定...」「最新を基準に」の操作。結果は、画面下部ではなくダイアログで知らせる
        var candidate = await monitor.PrepareBaselineAsync(path);
        if (candidate.HardError != null)
        {
            ShowWarning(candidate.HardError);
            return;
        }

        // エラー（注意・警告）と判定される画像を、うっかり基準にしないよう確認する（既定は「いいえ」）。
        // 太陽の位置を直した直後など、意図して新しい位置を基準にしたい場合は「はい」で確定できる。
        var inspection = candidate.Inspection;
        if (inspection != null && inspection.Level != CheckLevel.Normal && candidate.Metrics != null)
        {
            string level = inspection.Level == CheckLevel.Warning ? "警告" : "注意";
            string reasons = string.Join("\n", inspection.Reasons.Select(x => "・" + x));
            var answer = MessageBox.Show(Application.Current.MainWindow,
                $"{Path.GetFileName(path)} は、現在の基準画像に対して「{level}」と判定される、エラーのある画像です。\n\n{reasons}\n\n" +
                "エラーのある画像を基準にすると、以後の判定がずれます。\nそれでも基準画像にしますか？",
                "Timelapse Checker", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        if (candidate.Metrics != null) monitor.CommitBaseline(candidate.Metrics);
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
            ShowWarning("基準にできる最新画像がまだありません。\n画像が出力されてから、もう一度押してください。");
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
            : $"面積 {r.AreaRatio:P0} / ずれ {r.Shift:F2} / 明るさ {r.BrightnessRatio:P0} / コントラスト {r.ContrastRatio:P0} / 背景 {r.BackgroundLift:+0.00;-0.00;0.00}";

        var sb = new StringBuilder();
        sb.AppendLine($"面積比（直近基準）: {r.AreaRatio:P1}");
        sb.AppendLine($"面積比（開始時基準）: {r.AreaRatioSession:P1}");
        sb.AppendLine($"重心ずれ（半径比）: {r.Shift:F3}");
        sb.AppendLine($"明るさ比: {r.BrightnessRatio:P1}");
        sb.AppendLine($"コントラスト比: {r.ContrastRatio:P1}");
        sb.AppendLine($"明暗差の比: {r.RangeRatio:P1}");
        sb.AppendLine($"背景の持ち上がり: {r.BackgroundLift:+0.000;-0.000;0.000}");
        sb.Append($"画像端への接触: {(m.TouchesEdge ? "あり" : "なし")}");
        MetricsText = sb.ToString();

        latestName = name;
        latestGray = m.PreviewJpeg != null ? ToImage(m.PreviewJpeg) : null;
        latestOriginal = m.OriginalJpeg != null ? ToImage(m.OriginalJpeg) : null;
        UpdatePreviewSelection();

        RefreshFileCount();

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
            const string header = "time,file,level,area_ratio,area_ratio_session,shift,brightness_ratio,contrast_ratio,range_ratio,bg_lift,edge,edge_artifact,edge_artifact_side,edge_artifact_run,reasons";
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
                r.BrightnessRatio.ToString("F3"),
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



























