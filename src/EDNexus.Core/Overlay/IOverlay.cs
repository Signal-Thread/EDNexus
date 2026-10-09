namespace EDNexus.Core.Overlay;

/// <summary>
/// Platform abstraction for the in-game HUD overlay: a transparent, click-through, always-on-top
/// window drawn over Elite Dangerous showing next-jump, fuel, bio-signal and colonisation-shortfall
/// information. Implementations that can't render one (an unsupported OS, or no windowing available)
/// report <see cref="IsSupported"/> as false and treat every other call as a no-op — see
/// <see cref="NullOverlay"/>.
/// </summary>
public interface IOverlay
{
    /// <summary>Whether this platform can actually render an overlay window at all.</summary>
    bool IsSupported { get; }

    /// <summary>Whether the overlay window is currently shown.</summary>
    bool IsVisible { get; }

    /// <summary>Show the overlay window. Idempotent; a no-op when unsupported.</summary>
    void Show();

    /// <summary>Hide the overlay window without disposing it. Idempotent; a no-op when unsupported.</summary>
    void Hide();

    /// <summary>Push a fresh content snapshot to the overlay. A no-op while hidden or unsupported.</summary>
    void Update(OverlayContent content);

    /// <summary>
    /// Whether the overlay should be on screen only while Elite Dangerous (or EDNexus) is the app in front
    /// (see <see cref="OverlayVisibilityPolicy"/>). Takes effect immediately; a no-op when unsupported.
    /// </summary>
    void SetOnlyWhenGameFocused(bool value);
}
