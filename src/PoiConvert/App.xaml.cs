using System.Windows;
using System.Windows.Threading;

namespace PoiConvert;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"予期しないエラーが発生しました。\n\n{e.Exception.Message}", "PoiConvert",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
