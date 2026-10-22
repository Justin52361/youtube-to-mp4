using System.ComponentModel;
using System.Windows;
using YoutubeToMp4.ViewModels;

namespace YoutubeToMp4.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        StateChanged += (_, _) =>
            // A chromeless maximized window spills past the screen edge by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Shutdown();
        base.OnClosing(e);
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow { Owner = this, DataContext = _vm }.ShowDialog();
}
