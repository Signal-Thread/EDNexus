using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EDNexus.App.Services;

namespace EDNexus.App.Views;

/// <summary>Lists every library and data service EDNexus relies on, with its licence.</summary>
public partial class OpenSourceWindow : Window
{
    private readonly IReadOnlyList<ThirdPartyLicense> _all;

    public OpenSourceWindow()
    {
        InitializeComponent();
        _all = ThirdPartyLicenseCatalog.LoadEmbedded();
        FilterBox.TextChanged += (_, _) => Apply();
        Apply();
    }

    private void Apply()
    {
        var q = FilterBox.Text?.Trim();
        var shown = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(l => l.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || l.License.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Entries.ItemsSource = shown;
        CountLine.Text = _all.Count == 0
            ? "The licence list could not be loaded."
            : $"{shown.Count} of {_all.Count} shown";
    }

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        // SafeUrl is already restricted to http(s); anything else leaves Tag null and the button hidden.
        if (sender is not Button { Tag: string url }) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch { /* Best-effort: no browser registered must not crash the window. */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
