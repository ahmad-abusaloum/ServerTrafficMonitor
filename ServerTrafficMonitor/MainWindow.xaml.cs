using System.Windows;
using ServerTrafficMonitor.ViewModels;

namespace ServerTrafficMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) => _vm.Dispose();
    }
}
