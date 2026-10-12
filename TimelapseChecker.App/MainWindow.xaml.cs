using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TimelapseChecker.App.Services;

namespace TimelapseChecker.App;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    // YouTube配信用レイアウトのウィンドウ幅（物理ピクセル）。OBSで画面の一部を切り出して配信する想定
    private const double StreamWindowWidthPx = 550;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private readonly MainViewModel viewModel;
    private readonly JsonStore<WindowSettings> windowStore = new("window_settings.json");

    private bool isStreamLayout;

    // 標準レイアウトのウィンドウ位置・サイズ。配信用レイアウト中も保持し、標準へ戻すときに復元する
    private WindowSettings? standardBounds;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;

        standardBounds = windowStore.Exists ? windowStore.Load() : null;

        if (viewModel.Settings.StreamLayout)
        {
            ApplyLayout(true);
        }
        else
        {
            ApplyBounds(standardBounds);
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"{Title} v{version}";

        Loaded += (_, _) =>
        {
            // グラフはウィンドウが実サイズを持ってから初期化する
            viewModel.RefreshGraph();
            viewModel.StartIfConfigured();
        };
    }

    /// <summary>保存されている標準レイアウトの位置・サイズを適用する（無ければ画面中央）。</summary>
    private void ApplyBounds(WindowSettings? s)
    {
        if (s == null || s.Width <= 0 || s.Height <= 0)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Width = Math.Max(s.Width, MinWidth);
        Height = Math.Max(s.Height, MinHeight);

        // 保存時と画面構成が変わっている場合に画面外へ出ないようチェック
        bool withinBounds =
            s.Left >= SystemParameters.VirtualScreenLeft &&
            s.Top >= SystemParameters.VirtualScreenTop &&
            s.Left + 50 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            s.Top + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (withinBounds)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.Left;
            Top = s.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (s.IsMaximized) WindowState = WindowState.Maximized;
    }

    private WindowSettings CaptureCurrentBounds()
    {
        bool max = WindowState == WindowState.Maximized;
        return new WindowSettings
        {
            IsMaximized = max,
            Left = max ? RestoreBounds.Left : Left,
            Top = max ? RestoreBounds.Top : Top,
            Width = max ? RestoreBounds.Width : Width,
            Height = max ? RestoreBounds.Height : Height,
        };
    }

    /// <summary>
    /// 標準レイアウトと配信用レイアウトを切り替える。
    /// 配信用: 幅550px・タスクバーを除いた画面の高さいっぱいで、水平方向の中央に配置する。
    /// 内容は縦1列（プレビュー → グラフ → 履歴）に並べ替える。
    /// </summary>
    private void ApplyLayout(bool stream)
    {
        isStreamLayout = stream;
        viewModel.Settings.StreamLayout = stream;

        MainArea.RowDefinitions.Clear();

        if (stream)
        {
            MainArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            MainArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            MainCol0.Width = new GridLength(1, GridUnitType.Star);
            MainCol1.Width = new GridLength(0);
            MainCol2.Width = new GridLength(0);

            Grid.SetRow(LeftPanel, 0);
            Grid.SetColumn(LeftPanel, 0);
            LeftPanel.Height = 250;
            LeftPanel.Margin = new Thickness(0, 0, 0, 8);

            Grid.SetRow(RightPanel, 1);
            Grid.SetColumn(RightPanel, 0);

            MetricsBlock.Visibility = Visibility.Collapsed;
            UseLatestButton.Content = "最新を基準に";
            LayoutToggleButton.Content = "標準レイアウトに戻す";

            // 高DPI（150%等）では550pxが400DIPを下回るため、最小幅は配信幅より小さく取る
            MinWidth = 250;
            MinHeight = 520;
            PlaceStreamWindow();
        }
        else
        {
            MainCol0.Width = new GridLength(480);
            MainCol1.Width = new GridLength(10);
            MainCol2.Width = new GridLength(1, GridUnitType.Star);

            Grid.SetRow(LeftPanel, 0);
            Grid.SetColumn(LeftPanel, 0);
            LeftPanel.Height = double.NaN;
            LeftPanel.Margin = new Thickness(0);

            Grid.SetRow(RightPanel, 0);
            Grid.SetColumn(RightPanel, 2);

            MetricsBlock.Visibility = Visibility.Visible;
            UseLatestButton.Content = "最新画像を基準にする";
            LayoutToggleButton.Content = "配信用レイアウト";

            MinWidth = 820;
            MinHeight = 520;
            WindowState = WindowState.Normal;
            Width = 1100;
            Height = 900;
            ApplyBounds(standardBounds);
        }
    }

    private void PlaceStreamWindow()
    {
        WindowState = WindowState.Normal;

        // 550pxは物理ピクセルなので、DPIスケーリング（125%等）に合わせてDIPへ換算する
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = StreamWindowWidthPx / (scale > 0 ? scale : 1.0);

        // 作業領域 = タスクバーを除いた画面領域
        var work = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = width;
        Height = work.Height;
        Left = work.Left + (work.Width - width) / 2;
        Top = work.Top;
    }

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!isStreamLayout)
        {
            standardBounds = CaptureCurrentBounds();
            ApplyLayout(true);
        }
        else
        {
            ApplyLayout(false);
        }
        viewModel.SaveSettings();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // 配信用レイアウト中は、標準レイアウトの位置・サイズを上書きしない
        var toSave = isStreamLayout ? standardBounds : CaptureCurrentBounds();
        if (toSave != null) windowStore.Save(toSave);

        viewModel.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // タイトルバーもダークにする
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "タイムラプスの出力フォルダを選択" };
        if (Directory.Exists(viewModel.WatchFolder)) dialog.InitialDirectory = viewModel.WatchFolder;
        if (dialog.ShowDialog(this) == true) viewModel.WatchFolder = dialog.FolderName;
    }

    private void BrowseBaseline_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "基準画像を選択",
            Filter = "画像ファイル (*.tif;*.tiff;*.png;*.jpg;*.jpeg)|*.tif;*.tiff;*.png;*.jpg;*.jpeg|すべてのファイル (*.*)|*.*",
        };
        if (Directory.Exists(viewModel.WatchFolder)) dialog.InitialDirectory = viewModel.WatchFolder;
        if (dialog.ShowDialog(this) == true) viewModel.SetBaselineFile(dialog.FileName);
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音声ファイル (*.wav;*.mp3)|*.wav;*.mp3|すべてのファイル (*.*)|*.*",
            Title = "警告音を選択",
        };
        if (dialog.ShowDialog(this) == true) viewModel.SoundFilePath = dialog.FileName;
    }
}
