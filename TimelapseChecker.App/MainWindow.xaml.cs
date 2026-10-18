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
            ScrollToEnd(FolderBox);
            ScrollToEnd(BaselineBox);
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

            ApplyBannerLayout(true);
            ApplySettingsLayout(true);
            MetricsBlock.Visibility = Visibility.Collapsed;
            UseLatestButton.Content = "最新を基準に";
            LayoutToggleButton.Content = "標準";
            LayoutToggleButton.ToolTip = "標準レイアウトに戻します";
            viewModel.CompactGraph = true;

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

            ApplyBannerLayout(false);
            ApplySettingsLayout(false);
            MetricsBlock.Visibility = Visibility.Visible;
            UseLatestButton.Content = "最新画像を基準にする";
            LayoutToggleButton.Content = "配信用";
            LayoutToggleButton.ToolTip = "配信用レイアウト（設定ファイル app_settings.json の StreamWindowWidth／StreamWindowHeight のサイズ）に切り替えます";
            viewModel.CompactGraph = false;

            MinWidth = 820;
            MinHeight = 520;
            WindowState = WindowState.Normal;
            Width = 1100;
            Height = 900;
            ApplyBounds(standardBounds);
        }
    }

    /// <summary>
    /// 状態バナーの並び。標準: [状態][理由][確認] の3列。
    /// 配信用（幅が狭い）: [状態][理由] の2列で、確認ボタン（確認済み表示）は右側の下段に置く。
    /// </summary>
    private void ApplyBannerLayout(bool stream)
    {
        BannerGrid.RowDefinitions.Clear();

        if (stream)
        {
            BannerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BannerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BannerCol2.Width = new GridLength(0);

            // 左列を2段にする: 上段=状態（「警告」など）、下段=確認ボタン（確認済み表示）。
            // 右列は理由のメッセージ全体で使う（2段にまたがる）。
            Grid.SetRow(BannerTitle, 0);
            Grid.SetRowSpan(BannerTitle, 1);
            Grid.SetRow(BannerReason, 0);
            Grid.SetRowSpan(BannerReason, 2);

            // 左列は「警告」の文字の幅に合わせて最小限にし、右のメッセージの幅をできるだけ広く取る
            BannerTitle.Margin = new Thickness(0, 0, 12, 0);
            BannerCol0.MinWidth = 0;

            foreach (FrameworkElement el in new FrameworkElement[] { AckButton, AckText })
            {
                Grid.SetRow(el, 1);
                Grid.SetColumn(el, 0);
                el.VerticalAlignment = VerticalAlignment.Center;
            }
            // ボタンは「警告」の文字の幅いっぱいに広げる
            AckButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            AckText.HorizontalAlignment = HorizontalAlignment.Left;
            AckButton.Margin = new Thickness(0, 6, 12, 0);
            AckText.Margin = new Thickness(0, 6, 12, 0);
        }
        else
        {
            BannerCol2.Width = GridLength.Auto;

            Grid.SetRow(BannerTitle, 0);
            Grid.SetRowSpan(BannerTitle, 1);
            Grid.SetRow(BannerReason, 0);
            Grid.SetRowSpan(BannerReason, 1);
            BannerTitle.Margin = new Thickness(0, 0, 24, 0);
            BannerCol0.MinWidth = 0;

            foreach (FrameworkElement el in new FrameworkElement[] { AckButton, AckText })
            {
                Grid.SetRow(el, 0);
                Grid.SetColumn(el, 2);
                el.HorizontalAlignment = HorizontalAlignment.Stretch;
                el.VerticalAlignment = VerticalAlignment.Center;
            }
            AckButton.Margin = new Thickness(0);
            AckText.Margin = new Thickness(10, 0, 10, 0);
        }
    }

    /// <summary>
    /// 判定設定の並び。標準: 閾値の表の右側にオプション（音など）を置く。
    /// 配信用（幅が狭い）: オプションを閾値の表の下に回して、右にはみ出さないようにする。
    /// </summary>
    private void ApplySettingsLayout(bool stream)
    {
        if (stream)
        {
            SettingsCol3.Width = new GridLength(0);
            SettingsCol4.Width = new GridLength(0);
            Grid.SetRow(SettingsOptions, 8);
            Grid.SetRowSpan(SettingsOptions, 1);
            Grid.SetColumn(SettingsOptions, 0);
            Grid.SetColumnSpan(SettingsOptions, 5);
            SettingsOptions.Margin = new Thickness(0, 10, 0, 0);
            SettingsScroll.MaxHeight = 360;
        }
        else
        {
            SettingsCol3.Width = new GridLength(30);
            SettingsCol4.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SettingsOptions, 0);
            Grid.SetRowSpan(SettingsOptions, 8);
            Grid.SetColumn(SettingsOptions, 4);
            Grid.SetColumnSpan(SettingsOptions, 1);
            SettingsOptions.Margin = new Thickness(0);
            SettingsScroll.MaxHeight = 520;
        }
    }

    private void PlaceStreamWindow()
    {
        WindowState = WindowState.Normal;

        // サイズは設定ファイル（app_settings.json の StreamWindowWidth／StreamWindowHeight）で決める。
        // 物理ピクセル指定なので、DPIスケーリング（125%等）に合わせてDIPへ換算する。
        // 高さが0以下なら、タスクバーを除いた作業領域の高さいっぱいにする。
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1.0;
        var work = SystemParameters.WorkArea;

        double width = viewModel.Settings.StreamWindowWidth > 0
            ? viewModel.Settings.StreamWindowWidth / scale
            : 550 / scale;
        double height = viewModel.Settings.StreamWindowHeight > 0
            ? viewModel.Settings.StreamWindowHeight / scale
            : work.Height;

        // 作業領域（タスクバーを除く）に収まるようにする
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = width;
        Height = height;
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
        if (dialog.ShowDialog(this) == true)
        {
            viewModel.WatchFolder = dialog.FolderName;
            viewModel.ApplyWatchFolderChange();
        }
    }

    // ---- パス欄: 長いパスでも末尾（フォルダ名側）が見えるよう、カーソルを右端に寄せる ----
    private static void ScrollToEnd(TextBox box)
    {
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.CaretIndex = box.Text.Length;
            box.ScrollToHorizontalOffset(double.MaxValue);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BaselineExpander_Expanded(object sender, RoutedEventArgs e)
    {
        // 展開したときに、基準画像のパスの末尾が見えるようにする
        ScrollToEnd(BaselineBox);
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 入力中は動かさず、編集していないときだけ右端へ寄せる
        if (sender is TextBox box && !box.IsKeyboardFocusWithin) ScrollToEnd(box);
    }

    private void FolderBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        // 監視中にフォルダを書き換えていた場合は、新しいフォルダで監視し直す
        viewModel.ApplyWatchFolderChange();
        ScrollToEnd(FolderBox);
    }

    private void FolderBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            viewModel.ApplyWatchFolderChange();
            ScrollToEnd(FolderBox);
            e.Handled = true;
        }
    }

    // Esc で拡大表示を閉じる
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && viewModel.IsEnlarged)
        {
            viewModel.CloseEnlargedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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
















