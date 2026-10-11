using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;
        ApplyWindowSettings();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"{Title} v{version}";

        Loaded += (_, _) =>
        {
            // グラフはウィンドウが実サイズを持ってから初期化する
            viewModel.RefreshGraph();
            viewModel.StartIfConfigured();
        };
    }

    private void ApplyWindowSettings()
    {
        if (!windowStore.Exists)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var s = windowStore.Load();
        if (s.Width <= 0 || s.Height <= 0)
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        bool max = WindowState == WindowState.Maximized;
        var s = new WindowSettings
        {
            IsMaximized = max,
            Left = max ? RestoreBounds.Left : Left,
            Top = max ? RestoreBounds.Top : Top,
            Width = max ? RestoreBounds.Width : Width,
            Height = max ? RestoreBounds.Height : Height,
        };
        windowStore.Save(s);
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
