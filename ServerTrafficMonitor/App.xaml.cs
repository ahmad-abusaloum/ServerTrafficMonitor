using System.Windows;
using System.Windows.Threading;

namespace ServerTrafficMonitor;

public partial class App : Application
{
    public App()
    {
        // Keep the app alive if a background trace/DNS callback throws unexpectedly.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Unexpected error:\n\n" + e.Exception.Message,
            "Server Traffic Monitor",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
