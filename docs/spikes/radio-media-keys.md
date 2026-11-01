# Spike: OS media keys for the Radio player (issue #124)

Status: research only. Date: 2026-11-01. No code changed.

## Verification status (read this first)

Be clear about what this document is built on.

Verified in this repo:
- `IRadioPlayer` / `RadioPlayerService` surface and where the player is built and selected (below).
- The platform-abstraction pattern the app already uses (`VoiceFactory`, `OverlayFactory`).
- `Tmds.DBus.Protocol 0.94.1` is already a transitive dependency of `EDNexus.App`
  (via `Avalonia.FreeDesktop` / `Avalonia.X11` 12.1.4), checked with
  `dotnet list src/EDNexus.App package --include-transitive`.
- Release pipeline: Windows ships through Inno Setup, Linux through Flatpak
  (`packaging/flatpak`). There is no macOS packaging job in `.github/workflows/release.yml`.

NOT verified: nothing below was run on Windows, macOS, or Linux, and no prototype was built.
The OS API behaviour, NuGet package suitability, and permission/entitlement claims come from
prior knowledge of those platforms and have not been re-checked against current docs (no web
access in this spike). Anything marked "(unverified)" needs a small prototype before the
implementation issue is sized for real.

## Where it plugs in

`src/EDNexus.Core/Radio/IRadioPlayer.cs` already exposes exactly the four operations the keys need:

| Media key | Call | Notes |
|---|---|---|
| Play/Pause | `TogglePlayPauseAsync()` | Single control; semantics defined by `RadioPlayerService.ToggleActionFor(status)`. |
| Next | `NextStationAsync()` | Wraps around the catalog. |
| Previous | `PreviousStationAsync()` | Wraps around the catalog. |
| Stop (optional) | `RadioPlayerService.StopAsync()` | Not on `IRadioPlayer`; only the concrete service has it. |

The UI reaches the player through `Context.Radio.Active` (`RadioPlayerSelector`), which returns the
`SimulatedRadioPlayer` in developer mode and the real service otherwise. The media-key router must
resolve the player through the selector on every command (not capture `Real`), so developer mode
keeps working and never touches the real audio device. `MainWindowViewModel` constructs
`RadioPlayerService` (line ~56); the media-key service would be created next to it.

No change to `IRadioPlayer` is needed. A key press is just another caller of the existing methods.

Reverse direction (state out to the OS): `IRadioPlayer.Changed` + `Snapshot` already carry
status and station, which is what each platform needs to publish "Now Playing" metadata and
play/pause state.

## Platform findings

### Windows

Two viable mechanisms.

1. System Media Transport Controls (SMTC). The Windows-native answer, and the only one that also
   gives the Now Playing flyout, lock-screen/volume-flyout card, and routes keys from Bluetooth
   headsets. Desktop (non-UWP) apps obtain it through `ISystemMediaTransportControlsInterop.GetForWindow(hwnd)`
   and then use `SystemMediaTransportControls`: enable `IsPlayEnabled/IsPauseEnabled/IsNextEnabled/IsPreviousEnabled`,
   subscribe to `ButtonPressed`, and set `DisplayUpdater` (title = station name, artist = "EDNexus Radio").
   Costs:
   - Needs the WinRT projections, which means changing the App's `TargetFramework` to a
     `net10.0-windows10.0.x` TFM (or isolating the code in a separate Windows-only assembly). This
     is the biggest structural change: it makes the App project (currently plain `net10.0` and
     cross-built on Linux CI) Windows-TFM, or forces a new project plus conditional reference. (unverified: exact CI impact)
   - Needs a real HWND from Avalonia (`TopLevel.TryGetPlatformHandle()`), so it can only be created
     after the main window exists.
   - Because LibVLC (not `Windows.Media.Playback.MediaPlayer`) produces the audio, SMTC does not
     see a playing session automatically. We must set `PlaybackStatus` ourselves and keep it in sync
     with `IRadioPlayer.Changed`. Windows routes keys to the most recent session that reports
     Playing, so state must be accurate.
   - Display name in the flyout is derived from the app identity; an unpackaged exe may show the
     exe name unless the Start Menu shortcut carries an AppUserModelID. The Inno installer already
     makes a shortcut, so setting an AUMID there is probably enough. (unverified)
   - Callbacks arrive on a thread-pool thread; marshal into the radio service (it already has its own gate).

2. `RegisterHotKey` with `VK_MEDIA_PLAY_PAUSE` / `VK_MEDIA_NEXT_TRACK` / `VK_MEDIA_PREV_TRACK`
   (or a `WH_KEYBOARD_LL` hook). Plain P/Invoke, no TFM change, trivial. But it is global and
   exclusive: it steals the keys from Spotify/browsers/whatever the user normally controls, fails
   if another app already registered them, gives no Now Playing integration, and does not handle
   headset buttons. A low-level hook can observe without swallowing but cannot cleanly say "I'm
   the active player". Acceptable only as a fallback.

Recommendation for Windows: SMTC, with `RegisterHotKey` as a documented fallback if the TFM cost is
rejected. SMTC is also the only option that "plays nicely" when the user has other media apps.

### macOS

- `MPRemoteCommandCenter` + `MPNowPlayingInfoCenter` (MediaPlayer.framework) is the sanctioned
  route for play/pause/next/previous and Now Playing, and does not require Accessibility or Input
  Monitoring permission. It works from any process with a running main-thread run loop (Avalonia's
  macOS backend provides one). (unverified for an Avalonia host)
- The friction is calling it from .NET: the handlers are Objective-C blocks. Options:
  a. Raw ObjC runtime P/Invoke (`objc_msgSend`, building blocks by hand). No extra native binary, but fragile.
  b. A tiny native shim dylib (Swift/ObjC, ~100 lines) exposing C callbacks, built for arm64+x64, bundled and signed/notarised with the app.
  c. Microsoft.macOS bindings (`net10.0-macos` workload). Gives typed API but pulls a workload and a
     second app-framework surface into an Avalonia app; heavy for one feature.
  (Option a or b is the realistic choice; b is more robust.)
- Alternative: `CGEventTap` on `NX_SYSDEFINED` events to see raw media keys. Requires the user to grant
  Accessibility/Input Monitoring in System Settings, and it intercepts keys regardless of which app
  is "Now Playing". Worse UX and a permissions prompt; not recommended.
- macOS routes media keys to the app that most recently registered as the Now Playing app; with no
  claimant it launches Apple Music. We must only claim the session while the radio is playing/paused
  (see "Session ownership").
- Packaging: no macOS release artifact exists today, so there is nothing to entitle or notarise yet.
  `MPRemoteCommandCenter` needs no special entitlement as far as I know, but a shim dylib would need
  hardened-runtime signing. (unverified) This makes macOS the item to defer until a macOS build ships.

### Linux

- MPRIS2 over the session D-Bus is the standard. We own the bus name `org.mpris.MediaPlayer2.ednexus`,
  export object `/org/mpris/MediaPlayer2` implementing `org.mpris.MediaPlayer2` (Raise/Quit,
  `CanQuit`, `Identity`, `DesktopEntry`) and `org.mpris.MediaPlayer2.Player`
  (`PlayPause/Play/Pause/Next/Previous/Stop`, `PlaybackStatus`, `Metadata`, `CanGoNext/Previous/Play/Pause/Control`).
  GNOME, KDE, and playerctl/Bluetooth stacks route media keys and show Now Playing from this.
  It works the same on X11 and Wayland, which matters because Avalonia runs through XWayland and
  cannot see global keys itself.
- Dependency: `Tmds.DBus.Protocol` is already in the dependency graph (verified, 0.94.1) because
  Avalonia uses it. We can reference it directly (pin the version) and avoid adding anything new.
  Writing the two interfaces by hand against `Tmds.DBus.Protocol` is on the order of 250-350 lines;
  the `Tmds.DBus.SourceGenerator` package could generate proxies/handlers from the XML but is a new
  dependency. (unverified: whether Avalonia's pinned version is a good public API to build on)
- Flatpak (the shipped Linux package): the sandbox filters the session bus, so the manifest needs
  `--own-name=org.mpris.MediaPlayer2.ednexus` in `finish-args`. Without it the bus-name request is
  denied. Flatpak apps are expected to use the `org.mpris.MediaPlayer2.<appid>` form, i.e. probably
  `org.mpris.MediaPlayer2.io.github.Signal_Thread_LLC.EDNexus`; confirm against Flatpak docs. (unverified)
  `DesktopEntry` should be the app's desktop file id so shells can attribute it.
- LibVLC on Linux does not itself register an MPRIS name, so there is no conflict, but the app should
  not export a second name from the same process.
- No D-Bus session (headless, containers, some WSL) must degrade silently to the no-op.

## Cross-platform library question

I know of no maintained library that covers all three and also integrates Now Playing correctly.
What exists, from memory (unverified against current versions):
- SharpHook / libuiohook: global keyboard/mouse hook. Can see media keys on Windows and X11, but
  needs Accessibility permission on macOS, does not work under Wayland, requires a native lib per
  RID, and offers no Now Playing or session ownership. Wrong layer (it observes keys, it doesn't
  register a media session). Not recommended.
- Avalonia itself has no media-key or media-session API.
- Per-platform MPRIS libraries exist on NuGet but are thin wrappers over Tmds.DBus; no advantage over
  using `Tmds.DBus.Protocol` we already have.

Conclusion: no library. Three small platform shims behind one interface is the right shape, and it
matches how the app already does `IVoice` (SAPI on Windows, `NullVoice` elsewhere) and the overlay
(`OverlayFactory`), and satisfies AGENTS.md convention 4 (interface + no-op fallback).

## Recommendation

Implement an `IMediaKeyService` abstraction (sketch below) with:
1. `NullMediaKeyService`: no-op; used on unsupported platforms, when no D-Bus session exists, when the
   user disables the feature, and for tests/CLI.
2. `MprisMediaKeyService` (Linux), `SmtcMediaKeyService` (Windows), `MacMediaKeyService` (macOS, later).
3. A platform-neutral `MediaKeyRouter` in Core that maps a `MediaKeyCommand` to `IRadioPlayer` and
   publishes `IRadioPlayer.Snapshot` back as `MediaSessionInfo`. This holds all logic worth unit
   testing; the shims stay as thin as possible.

Phasing:
1. Core abstraction, router, null implementation, Settings toggle, router unit tests.
2. Linux MPRIS (cheapest, pure managed, no new dependency, easiest to test).
3. Windows SMTC (decide on the TFM approach first; fall back to `RegisterHotKey` if rejected).
4. macOS, only once a macOS build is actually shipped.

### Session ownership (design requirement)

Claim the OS media session only while the radio is Playing or Paused, and release it when stopped or
disabled. Otherwise EDNexus would hold the media keys while idle and break the user's Spotify/browser
controls, which would be a regression people notice immediately. Subscribe to `IRadioPlayer.Changed`,
read `Snapshot`, and call `Update`/`Release`.

## Interface sketch (not implemented)

```csharp
namespace EDNexus.Core.Radio;

/// <summary>An OS-level transport command, already decoded from a media key / SMTC / MPRIS call.</summary>
public enum MediaKeyCommand { PlayPause, Play, Pause, Next, Previous, Stop }

/// <summary>What the OS "Now Playing" UI should show.</summary>
public sealed record MediaSessionInfo(
    bool IsPlaying,
    string? StationName,    // _Localised-style display text, not an id
    bool CanGoNext,
    bool CanGoPrevious);

/// <summary>
/// Receives hardware media-key commands and publishes Now Playing state. Implementations are
/// platform-specific; <see cref="NullMediaKeyService"/> is the fallback.
/// </summary>
public interface IMediaKeyService : IDisposable
{
    /// <summary>Raised on an arbitrary thread when the OS delivers a command.</summary>
    event Action<MediaKeyCommand>? CommandReceived;

    /// <summary>True when a real platform backend is active (false for the no-op).</summary>
    bool IsSupported { get; }

    /// <summary>Claims (or refreshes) the OS media session with the given state.</summary>
    void Update(MediaSessionInfo info);

    /// <summary>Releases the OS media session so other players get the keys back.</summary>
    void Release();
}
```

Factory in the App, mirroring `VoiceFactory`:

```csharp
public static IMediaKeyService Create() =>
    OperatingSystem.IsLinux()   ? MprisMediaKeyService.TryCreate() ?? new NullMediaKeyService()
  : OperatingSystem.IsWindows() ? SmtcMediaKeyService.TryCreate()  ?? new NullMediaKeyService()
  : new NullMediaKeyService();
```

`TryCreate` returns null on any failure (no session bus, WinRT unavailable) instead of throwing.
The router subscribes `CommandReceived`, resolves `Context.Radio.Active`, and calls
`TogglePlayPauseAsync` / `NextStationAsync` / `PreviousStationAsync`. Platform code lives in
`src/EDNexus.App/Services/Media/`; the abstraction, enum, record, router, and null implementation in
`src/EDNexus.Core/Radio/`.

Not included in this spike on purpose: adding the interface now would create unused code with design
choices (e.g. whether Windows needs a window handle in `TryCreate`) that the prototypes should settle.

## Effort estimate (engineer-days, rough)

| Item | Estimate |
|---|---|
| Core abstraction, router, null impl, setting, unit tests | 1-1.5 |
| Linux MPRIS (Tmds.DBus.Protocol) + Flatpak finish-arg + manual test on GNOME/KDE | 2-3 |
| Windows SMTC incl. TFM/CI handling, AUMID, state sync | 2-4 (the TFM decision is the swing factor) |
| Windows `RegisterHotKey` fallback instead of SMTC | 0.5-1 |
| macOS (shim dylib or ObjC P/Invoke, signing, test on real hardware) | 4-6, deferred |
| Dev-mode sample/simulated command source (convention 7) | 0.5 |

MVP (core + Linux + Windows): about 5-8 days. All three platforms: about 10-14 days. These are
estimates, not measured against a prototype.

## New dependencies

- Linux: none new in practice. `Tmds.DBus.Protocol` is already transitive; promote it to a direct
  reference with an explicit version. Optional: `Tmds.DBus.SourceGenerator` (dev-time).
- Windows: no NuGet package if the TFM is changed to `net10.0-windows10.0.xxxxx` (WinRT projections
  come with the SDK targeting pack). Alternative is the `Microsoft.Windows.CsWinRT`-based projection
  or hand-written COM interop. (unverified)
- macOS: none for ObjC P/Invoke; otherwise a first-party native dylib built in CI.
- No cross-platform media-key library recommended.

## Risks

- Windows TFM change ripples through CI (currently Linux runners build the solution), the Core/App
  split, and test projects. Mitigation: isolate in a separate `net10.0-windows` project referenced
  conditionally, or use `RegisterHotKey`.
- SMTC state is manual with LibVLC; if `PlaybackStatus` drifts from reality, Windows sends keys to the
  wrong app or shows a stale flyout.
- Stealing keys from the user's other media apps if session ownership is not released (see above).
- Flatpak bus-name policy and name format could differ from what is assumed here.
- macOS: ObjC block interop fragility, and no shipped macOS artifact to test signing/notarisation against.
- Cannot be fully exercised in CI; needs manual checks on real desktops (GNOME, KDE, Windows 11, macOS).
- Threading: OS callbacks arrive off the UI thread; the router must not touch Avalonia objects directly.
- Wayland: any global-hook approach (SharpHook) silently fails there, which is why MPRIS is preferred.

## Open questions for the implementation issue

1. Is a Windows-only TFM/project acceptable, or do we ship `RegisterHotKey` first?
2. Should the feature be opt-in (Settings toggle) or on by default? Suggest on by default with a toggle.
3. When the radio is stopped, should Play be able to start the last station (claim the session)? That requires
   holding the session while idle, which conflicts with session ownership; suggest no.
4. Is macOS in scope before a macOS release artifact exists?
