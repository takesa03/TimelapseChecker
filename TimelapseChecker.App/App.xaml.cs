using System.Windows;

namespace TimelapseChecker.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    // 監視中に予期しない例外でアプリが落ちると、警告を見逃す実害が大きいため、
    // メッセージを表示したうえで継続できるようにする。
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"予期しないエラーが発生しました:\n{e.Exception}",
            "TimelapseChecker", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
