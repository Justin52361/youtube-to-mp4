using System.Windows;

namespace YoutubeToMp4.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    void OnDone(object sender, RoutedEventArgs e) => Close();
}
