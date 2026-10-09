using Avalonia.Controls;
using Avalonia.Media;
using EDNexus.Core.Overlay;

namespace EDNexus.App.Views;

/// <summary>
/// The in-game HUD overlay window: a small, always-on-top panel with no title bar, positioned in the
/// corner of the screen. <see cref="Services.Overlay.WindowsOverlay"/> owns the instance and makes it
/// click-through once the native handle exists.
/// </summary>
public partial class OverlayWindow : Window
{
    // Resolved once from Themes/Theme.axaml rather than parsed from a literal on every update.
    private readonly IBrush? _fuelOkBrush;
    private readonly IBrush? _fuelLowBrush;

    public OverlayWindow()
    {
        InitializeComponent();
        Position = new Avalonia.PixelPoint(24, 24);
        _fuelOkBrush = ThemeBrush("Ink");
        _fuelLowBrush = ThemeBrush("Bad");
    }

    // Null when the theme dictionary is missing: the text then keeps its inherited foreground.
    private IBrush? ThemeBrush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : null;

    /// <summary>Push a fresh content snapshot onto the panel. Must be called on the UI thread.</summary>
    public void UpdateContent(OverlayContent content)
    {
        SystemLine.Text = "System: " + (content.StarSystem ?? "—");

        if (content.NextJumpSystem is { Length: > 0 } next)
        {
            NextJumpLine.Text = "Next jump: " + next;
            NextJumpLine.IsVisible = true;
        }
        else
        {
            NextJumpLine.IsVisible = false;
        }

        FuelLine.Text = content.FuelCapacity > 0
            ? $"Fuel: {content.FuelMain:N1} / {content.FuelCapacity:N1} t ({content.FuelPercent:P0})"
            : "Fuel: —";
        FuelLine.Foreground = content.FuelLow ? _fuelLowBrush : _fuelOkBrush;

        // A sample run can be under way on a body the FSS never flagged, so the detail stands alone.
        var bioLines = new List<string>(2);
        if (content.HasBioSignals)
            bioLines.Add($"Bio signals: {content.BioSignalCount} — {content.BioSignalBody}");
        if (content.BioSignalDetail is { Length: > 0 } detail)
            bioLines.Add(detail);
        BioLine.Text = string.Join("\n", bioLines);
        BioLine.IsVisible = bioLines.Count > 0;

        if (content.HasColonisationShortfall)
        {
            ShortfallHeader.IsVisible = true;
            ShortfallList.IsVisible = true;
            ShortfallList.ItemsSource = content.ColonisationShortfalls
                .Select(l => $"{l.Name}: {l.Remaining}")
                .ToList();
        }
        else
        {
            ShortfallHeader.IsVisible = false;
            ShortfallList.IsVisible = false;
            ShortfallList.ItemsSource = null;
        }
    }
}
