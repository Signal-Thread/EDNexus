using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EDNexus.App.Views;

public partial class ConfirmInstallWindow : Window
{
    public ConfirmInstallWindow() => InitializeComponent();

    /// <summary>Shows the installer path and the SHA-256 it was verified against.</summary>
    public void SetDetails(string path, string sha256)
    {
        FilePath.Text = path;
        Checksum.Text = sha256;
    }

    private void OnEnable(object? sender, RoutedEventArgs e) => Close(true);
    private void OnDecline(object? sender, RoutedEventArgs e) => Close(false);
}
