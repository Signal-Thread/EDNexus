namespace EDNexus.Core.Overlay;

/// <summary>
/// No-op <see cref="IOverlay"/> for platforms without a native overlay implementation (Linux, macOS)
/// and for the CLI harness, which has no windowing at all.
/// </summary>
public sealed class NullOverlay : IOverlay
{
    public bool IsSupported => false;
    public bool IsVisible => false;
    public void Show() { }
    public void Hide() { }
    public void Update(OverlayContent content) { }
    public void SetOnlyWhenGameFocused(bool value) { }
}
