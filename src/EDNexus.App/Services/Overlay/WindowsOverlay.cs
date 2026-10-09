using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using EDNexus.App.Views;
using EDNexus.Core.Overlay;

namespace EDNexus.App.Services.Overlay;

/// <summary>
/// Windows overlay: a transparent, click-through, always-on-top Avalonia window drawn over Elite
/// Dangerous in borderless/windowed mode. Click-through and "never take focus" are applied via the Win32
/// extended window styles once the native handle exists, so the game keeps input focus and the overlay
/// never intercepts a click meant for the cockpit. All calls are best-effort: a failure here degrades to
/// "no overlay shown" rather than crashing the app.
/// </summary>
/// <remarks>
/// Because it is a topmost window, an overlay that is always up sits over every other app (EDCopilot's own
/// overlay included), and anything it does to its own window (a resize, a z-order change) is a desktop
/// event the game and other overlays can react to. So: the window has a fixed size (the panel grows inside
/// it, the native window never resizes on a jump); content is pushed only when it actually changed; the
/// window can never be activated; and by default it is visible only while Elite Dangerous is in front
/// (<see cref="OverlayVisibilityPolicy"/>).
/// </remarks>
public sealed class WindowsOverlay : IOverlay
{
    private static readonly TimeSpan FocusPollInterval = TimeSpan.FromMilliseconds(750);

    // All of the following are touched on the UI thread only.
    private OverlayWindow? _window;
    private DispatcherTimer? _focusTimer;
    private bool _wanted;
    private bool _onlyWhenGameFocused = true;
    private OverlayContent? _lastContent;
    private int _lastForegroundPid;
    private string? _lastForegroundName;
    private readonly string _ownProcess = Process.GetCurrentProcess().ProcessName;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsVisible => _window?.IsVisible == true;

    public void Show()
    {
        if (!IsSupported) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _wanted = true;
                if (_window is null)
                {
                    _window = new OverlayWindow();
                    _window.Opened += (_, _) => MakeClickThrough(_window);
                    // It is created non-activating and styled WS_EX_NOACTIVATE, so this should never fire. If it
                    // does, that is exactly the focus problem this window must not cause: leave a trace.
                    _window.Activated += (_, _) => Trace.TraceWarning("Overlay: the overlay window was activated (it should never take focus).");
                }

                StartFocusTimer();
                ApplyVisibility();
            }
            catch
            {
                // Best-effort — see class remarks.
            }
        });
    }

    public void Hide()
    {
        if (!IsSupported) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _wanted = false;
                _focusTimer?.Stop();
                ApplyVisibility();
            }
            catch { /* best-effort */ }
        });
    }

    public void Update(OverlayContent content)
    {
        if (!IsSupported) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_window is null) return;

                // Called every UI tick (250 ms) with a freshly built snapshot. Writing identical text back
                // and rebuilding the shortfall list each time churns layout for nothing; touch the window
                // only when something changed.
                if (_lastContent is not null && _lastContent.SameAs(content)) return;
                _lastContent = content;
                _window.UpdateContent(content);
            }
            catch { /* best-effort */ }
        });
    }

    public void SetOnlyWhenGameFocused(bool value)
    {
        if (!IsSupported) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _onlyWhenGameFocused = value;
                ApplyVisibility();
            }
            catch { /* best-effort */ }
        });
    }

    private void StartFocusTimer()
    {
        _focusTimer ??= new DispatcherTimer { Interval = FocusPollInterval };
        _focusTimer.Tick -= OnFocusTick;
        _focusTimer.Tick += OnFocusTick;
        _focusTimer.Start();
    }

    private void OnFocusTick(object? sender, EventArgs e)
    {
        try { ApplyVisibility(); }
        catch { /* best-effort */ }
    }

    /// <summary>Shows or hides the window to match the policy. Does nothing when the state is already right.</summary>
    private void ApplyVisibility()
    {
        var window = _window;
        if (window is null) return;

        var foreground = _wanted && _onlyWhenGameFocused ? ForegroundProcessName() : null;
        var show = OverlayVisibilityPolicy.ShouldShow(_wanted, _onlyWhenGameFocused, foreground, _ownProcess);
        if (show == window.IsVisible) return;

        if (show) window.Show();
        else window.Hide();
        Trace.TraceInformation($"Overlay: {(show ? "shown" : "hidden")} (foreground: {foreground ?? "n/a"}).");
    }

    /// <summary>The process that owns the foreground window, or null when it cannot be determined.</summary>
    private string? ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            if ((int)pid == _lastForegroundPid) return _lastForegroundName;

            using var process = Process.GetProcessById((int)pid);
            _lastForegroundPid = (int)pid;
            _lastForegroundName = process.ProcessName;
            return _lastForegroundName;
        }
        catch
        {
            return null;
        }
    }

    // --- Win32: WS_EX_LAYERED | WS_EX_TRANSPARENT makes the window click-through, WS_EX_TOOLWINDOW keeps it
    // out of the taskbar / alt-tab list alongside ShowInTaskbar="False", and WS_EX_NOACTIVATE guarantees it
    // can never become the active window (ShowActivated="False" only covers the first show). ---

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private static void MakeClickThrough(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
        catch
        {
            // Best-effort: worst case the overlay stays interactive rather than crashing the app.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
