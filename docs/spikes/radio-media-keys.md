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

Verified against primary docs (fetched 2026-11-01, revision pass after review):
- Flatpak sandbox permissions (docs.flatpak.org, "D-Bus access"): the default session-bus policy lets
  an app own `$FLATPAK_ID`, its subnames, and `org.mpris.MediaPlayer2.$FLATPAK_ID`; ownership beyond
  that is "typically unnecessary".
- freedesktop MPRIS2 spec: one unique `org.mpris.MediaPlayer2.<name>` bus name per player; additional
  instances add an `.instance<pid>`-style suffix (identifier must not begin with a digit).
- SharpHook README: X11 and Wayland are both supported, but Wayland support lacks features and
  requires elevated privileges.
- Windows SMTC desktop interop (`ISystemMediaTransportControlsInterop::GetForWindow`) and the
  `net*-windows10.0.x` TFM requirement for WinRT projections: confirmed by the PR reviewer against
  Microsoft Learn; the Learn page could not be re-fetched in this revision pass.

NOT verified: nothing below was run on Windows, macOS, or Linux, and no prototype was built.
The remaining OS API behaviour, NuGet package suitability, and permission/entitlement claims
(notably all macOS and all SMTC runtime-behaviour claims) come from prior knowledge and are marked
"(unverified)". Each needs a small prototype before the implementation issue is sized for real.

## Where it plugs in

`src/EDNexus.Core/Radio/IRadioPlayer.cs` exposes `TogglePlayPauseAsync`, `PlayAsync(stationId)`,
`NextStationAsync`, `PreviousStationAsync`, `SetVolumeAsync`, `SetMuteAsync`, `Changed` and `Snapshot`.
It does NOT have `Play`, `Pause` or `Stop`, which does not fit the OS command set.

| Command | Maps to today | Problem |
|---|---|---|
| PlayPause (hardware key) | `TogglePlayPauseAsync()` | Fine: a toggle is what the key means. |
| Play (MPRIS `Play`, SMTC Play) | none | Mapping to the toggle would pause an already-Playing radio. |
| Pause (MPRIS `Pause`, SMTC Pause, `playerctl pause`) | none | Mapping to the toggle would resume an already-paused radio. |
| Next / Previous | `NextStationAsync()` / `PreviousStationAsync()` | Wrap around the catalog; see key auto-repeat below. |
| Stop | `RadioPlayerService.StopAsync()` (concrete class only) | Absent from `IRadioPlayer`; `SimulatedRadioPlayer` has no Stop at all, so Stop cannot work in developer mode. |

Toggle semantics are `RadioPlayerService.ToggleActionFor(status)`: Playing -> Pause,
Buffering/Error -> **Stop**, anything else -> Play. So a play/pause key pressed while the stream is
connecting cancels it rather than being a no-op. That is right for a single physical key but wrong for
a state-addressed `Play` or `Pause` command.

Decision for the implementation issue (it must take one of these):
- Option A (recommended): add idempotent `PauseAsync` and `StopAsync`, plus a resume-only play, to
  `IRadioPlayer`, and implement them in `SimulatedRadioPlayer` too. (`RadioPlayerService` already has
  public `PlayAsync(ct)`, `PauseAsync` and `StopAsync`.) The router then maps Play -> resume only if
  not Playing/Buffering, Pause -> pause only if Playing, Stop -> stop. A small interface change that
  keeps the router free of status races.
- Option B: leave `IRadioPlayer` unchanged and make the router status-aware: read `Snapshot.Status`
  and call the toggle only when the status differs from the target. Stop is then unavailable (map
  MPRIS `Stop` to Pause or omit it). Racy by construction: the snapshot can be stale while a play is
  in `_pendingPlays`, so a doubled Play could still toggle the radio off.

The first draft's conclusion that no `IRadioPlayer` change is needed was wrong for Play/Pause/Stop;
only PlayPause/Next/Previous map cleanly.

The UI reaches the player through `Context.Radio.Active` (`RadioPlayerSelector`), which returns the
`SimulatedRadioPlayer` in developer mode and the real service otherwise. `MainWindowViewModel`
constructs `RadioPlayerService` (line ~56) and the selector right after it; the media-key service
would be created next to them.

### Gating: the feature must not enable or un-suspend the radio

`RadioPlayerService.PlayAsync(stationId)` sets `_enabled = true` and the resume intent, calls
`Persist()` (writing `RadioEnabled=true` to settings), and clears `_suspended`. Next, Previous and the
toggle all go through it. The router must therefore handle:
- **Enabled gate.** `RadioEnabled` defaults to false (`AppSettings.RadioEnabled`). The router must
  read `Snapshot.Enabled` and ignore every command while it is false, and the service must not claim
  an OS session while the radio feature is off. Otherwise a stray Next key or MPRIS `Play` from
  `playerctl` would start LibVLC audio and silently persist the feature as enabled.
- **Settings toggle.** The phase-1 settings toggle is a separate, media-key-only opt-out
  (`MediaKeysEnabled`); it does not replace or alter `RadioEnabled`. Effective rule: the session is
  claimed only if `MediaKeysEnabled && Snapshot.Enabled && status is Playing/Paused`.
- **Developer mode.** In developer mode the real player is suspended
  (`SuspendForDeveloperModeAsync`) and the UI drives `SimulatedRadioPlayer`. Decision: while
  `RadioPlayerSelector.IsSimulated` the media-key service **releases the session and ignores all
  commands**. Publishing the simulation would claim the user's real OS media session (and steal
  Spotify's keys) for a fake player; publishing the real player would show stale data. The router
  must never call the real player directly: a command that bypasses the selector reaches
  `RadioPlayerService.PlayAsync`, which clears `_suspended` and would un-suspend real audio mid
  developer session. The selector's `Changed` fires for both players, so the router re-evaluates
  (`Update` or `Release`) on every `Changed`, including when `IsSimulated` flips.
- **Dev-mode affordance (convention 7).** A media-key command source is not a `JournalSampleSource`
  (those emit journal events through the bus). The honest dev-mode story is a "send media command"
  button group in Settings -> Developer Options that raises `CommandReceived` on a
  `SimulatedMediaKeyService` (a test double implementing `IMediaKeyService`), exercising the router
  against the simulated player. Keep it behind `FeatureFlags.DeveloperTools`, and do not claim it
  satisfies the `JournalSampleSource` rule.

Reverse direction (state out to the OS): `IRadioPlayer.Changed` + `Snapshot` already carry
status and station, which is what each platform needs to publish "Now Playing" metadata and
play/pause state.

## Platform findings

### Windows

Two viable mechanisms.

1. System Media Transport Controls (SMTC). The Windows-native answer, and the only one that also
   gives the Now Playing flyout and lock-screen/volume-flyout card, and (unverified) is the only one
   that cleanly routes keys from Bluetooth headsets. Desktop (non-UWP) apps obtain it through `ISystemMediaTransportControlsInterop.GetForWindow(hwnd)`
   and then use `SystemMediaTransportControls`: enable `IsPlayEnabled/IsPauseEnabled/IsNextEnabled/IsPreviousEnabled`,
   subscribe to `ButtonPressed`, and set `DisplayUpdater` (title = station name, artist = "EDNexus Radio").
   Costs:
   - Needs the WinRT projections. There are two routes, and the repo already has a precedent for one:
     a. **Late-bound / hand-written COM interop (matches precedent).** `SapiVoice` is deliberately
        late-bound COM so the App stays plain `net10.0` and never needs a Windows-only TFM (see its
        header comment); `OverlayFactory` follows suit. `ISystemMediaTransportControlsInterop` is a
        classic COM interface, but the `SystemMediaTransportControls` object it returns is a WinRT
        object, so without a projection assembly it must be driven through hand-declared WinRT
        interfaces (HSTRING handling, `ButtonPressed` event-token registration with a hand-rolled
        handler, `DisplayUpdater`). This is the larger and riskier piece of code: 4-6 days.
        (unverified: no prototype)
     b. **Windows TFM.** Change the App (or a new Windows-only assembly referenced conditionally) to
        `net10.0-windows10.0.x`. Typed and shorter (2-4 days) but structural:
        - CI is `ubuntu-latest` (`.github/workflows/ci.yml`), so building any Windows-TFM project
          there needs `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; otherwise restore fails
          on Linux.
        - The installer's `MinVersion=10.0.14393` (`installer/windows/ednexus.iss`) must be reconciled
          with the TFM `TargetPlatformMinVersion` and with the minimum OS that supports desktop SMTC
          interop. Either raise `MinVersion` (dropping users) or keep the TFM platform version at or
          below the installer floor and guard calls with `OperatingSystem.IsWindowsVersionAtLeast`.
          (unverified: the exact minimum OS for desktop SMTC interop)
        - Core and the test projects must not reference the Windows assembly.
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
   headset buttons (unverified: headset buttons generally surface as `VK_MEDIA_*` key events, in which
   case `RegisterHotKey` would see them). A low-level hook can observe without swallowing but cannot
   cleanly say "I'm the active player". Acceptable only as a fallback.

Recommendation for Windows: SMTC. Prefer the late-bound/hand-written interop route if a prototype
shows it is tractable (precedent: `SapiVoice`), otherwise a Windows-only assembly; `RegisterHotKey` is
the documented fallback if both costs are rejected. SMTC is also the only option that "plays nicely" when the user has other media apps.

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

- MPRIS2 over the session D-Bus is the standard. Outside Flatpak we own a bus name of the form
  `org.mpris.MediaPlayer2.ednexus`, export object `/org/mpris/MediaPlayer2` implementing
  `org.mpris.MediaPlayer2` (Raise/Quit, `CanQuit`, `Identity`, `DesktopEntry`) and
  `org.mpris.MediaPlayer2.Player`
  (`PlayPause/Play/Pause/Next/Previous/Stop`, `PlaybackStatus`, `Metadata`, `CanGoNext/Previous/Play/Pause/Control`).
  GNOME, KDE, and playerctl/Bluetooth stacks route media keys and show Now Playing from this.
  It works the same on X11 and Wayland, which matters because Avalonia runs through XWayland and
  cannot see global keys itself.
- Dependency: `Tmds.DBus.Protocol` is already in the dependency graph (verified, 0.94.1) because
  Avalonia uses it. We can reference it directly (pin the version) and avoid adding anything new.
  Writing the two interfaces by hand against `Tmds.DBus.Protocol` is on the order of 250-350 lines
  (unverified estimate); the `Tmds.DBus.SourceGenerator` package could generate proxies/handlers from
  the XML but is a new dependency. (unverified: whether Avalonia's pinned version is a good public API
  to build on, and the API stability of a 0.x package)
- **Bus name is chosen at runtime.** The Flatpak default session-bus policy already permits owning
  `org.mpris.MediaPlayer2.$FLATPAK_ID` (verified, docs.flatpak.org), so no `--own-name` finish-arg is
  needed or wanted (it would be redundant, and an extra permission for Flathub review to question).
  Conversely the short `org.mpris.MediaPlayer2.ednexus` name would be **denied** inside the sandbox.
  The shim therefore picks the name at startup: under Flatpak (`FLATPAK_ID` set, or `/.flatpak-info`
  exists) use `org.mpris.MediaPlayer2.io.github.Signal_Thread_LLC.EDNexus` (from the app-id in
  `packaging/flatpak/io.github.Signal_Thread_LLC.EDNexus.yml`); otherwise use
  `org.mpris.MediaPlayer2.ednexus`. `DesktopEntry` should be the app's desktop file id (without
  `.desktop`) so shells can attribute it.
- **Name collisions / multiple instances.** The MPRIS spec wants one unique name per player, with an
  `.instance<pid>`-style suffix for additional instances (verified). Request the name without
  replacing an existing owner and without queueing (`DoNotQueue`). If it is already owned (a second
  EDNexus, or a native copy running next to the Flatpak), retry once with an `.instance<pid>` suffix or
  fall back to `NullMediaKeyService` and log it. Never replace another owner. A failure must not throw
  out of `TryCreate`.
- **Scope the exported surface.** Any session-bus peer can call the exported methods. Set
  `CanQuit=false` and make `Quit` a no-op (it must never exit the app); make `Raise` explicit (focus
  the main window, or no-op with `CanRaise=false`); `HasTrackList=false`. Only the transport methods
  map to the router, and all of them go through the same gating as keys. `mpris:trackid` must be a
  valid D-Bus object path built from the station **id** (sanitised to `[A-Za-z0-9_]`, e.g.
  `/org/ednexus/station/<id>`), never the station name. The shim must also cope with empty,
  non-ASCII or very long station names in `xesam:title` (cap the length).
- LibVLC on Linux does not itself register an MPRIS name, so there is no conflict, but the app should
  not export a second name from the same process. (unverified: not re-checked against current LibVLC)
- No D-Bus session (headless, containers, some WSL) must degrade silently to the no-op.

## Cross-platform library question

I know of no maintained library that covers all three and also integrates Now Playing correctly.
What exists, from memory (unverified against current versions):
- SharpHook / libuiohook: global keyboard/mouse hook. Can see media keys on Windows and X11, but
  needs Accessibility permission on macOS, supports Wayland only with limitations and elevated
  privileges (libinput/uinput; per the SharpHook README), requires a native lib per RID, and offers no Now Playing or session ownership. Wrong layer (it observes keys, it doesn't
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
   testing; the shims stay as thin as possible. It applies the gating rules above (Enabled,
   not simulated, `MediaKeysEnabled`).

Phasing:
1. Core abstraction, the `IRadioPlayer` Play/Pause/Stop change (or status-aware router), router, null implementation, `MediaKeysEnabled` setting (independent of `RadioEnabled`), router unit tests.
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

Factory in the App, mirroring `VoiceFactory`/`OverlayFactory` (both use `RuntimeInformation.IsOSPlatform`):

```csharp
public static IMediaKeyService Create() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Linux)     ? MprisMediaKeyService.TryCreate() ?? new NullMediaKeyService()
  : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? SmtcMediaKeyService.TryCreate()  ?? new NullMediaKeyService()
  : new NullMediaKeyService();
```

`TryCreate` returns null on any failure (no session bus, WinRT unavailable) instead of throwing.
The router subscribes `CommandReceived`, resolves `Context.Radio.Active` per command, applies the
gating above, and calls the mapped method (see the command table). Platform code lives in
`src/EDNexus.App/Services/Media/`; the abstraction, enum, record, router, and null implementation in
`src/EDNexus.Core/Radio/`.

### Router robustness (threading, lifecycle)

- `CommandReceived` is raised on an arbitrary thread and the router calls async radio methods. Every
  call must be wrapped so a fault is captured and logged: no fire-and-forget `Task` that can fault
  unobserved from a key press.
- Key auto-repeat / bursts: `RadioPlayerService` queues plays (`_pendingPlays`), so a held or rapid
  Next could queue many station switches. The router should coalesce (ignore Next/Previous while one
  is in flight, or debounce). Pick one in the implementation issue.
- Shutdown order: unsubscribe `CommandReceived` and the radio `Changed` handler, call `Release()`,
  dispose the media-key service, and only then dispose `RadioPlayerService`, so no command reaches a
  disposed player and the OS session disappears cleanly. `MainWindowViewModel.Dispose` is where this
  ordering lives.
- Handlers off the UI thread must not touch Avalonia objects.

### Router test requirements

Unit tests must cover, at minimum: Play while Playing is a no-op and Pause while Paused is a no-op
(idempotence); every command ignored while `Snapshot.Enabled` is false (and `RadioEnabled` stays
false); every command ignored and the session released while `IsSimulated`; Stop routed (or
explicitly unsupported); a faulting radio call does not escape the router; burst Next coalescing;
Release on Stopped/disabled; and dispose ordering. Happy-path-only tests would miss the first three.

Not included in this spike on purpose: adding the interface now would create unused code with design
choices (e.g. whether Windows needs a window handle in `TryCreate`) that the prototypes should settle.

## Effort estimate (engineer-days, rough)

| Item | Estimate |
|---|---|
| Core abstraction, `IRadioPlayer` Play/Pause/Stop, router (gating, coalescing), null impl, setting, unit tests | 1.5-2 |
| Linux MPRIS (Tmds.DBus.Protocol), runtime bus-name selection, collision handling, manual test on GNOME/KDE (no Flatpak finish-arg) | 2-3 |
| Windows SMTC via Windows TFM / separate assembly, incl. CI `EnableWindowsTargeting`, MinVersion, AUMID, state sync | 2-4 (the TFM decision is the swing factor) |
| Windows SMTC via late-bound/hand-written interop (SapiVoice precedent) instead of the TFM | 4-6 |
| Windows `RegisterHotKey` fallback instead of SMTC | 0.5-1 |
| macOS (shim dylib or ObjC P/Invoke, signing, test on real hardware) | 4-6, deferred |
| Dev-mode simulated media-command source (Developer Options buttons; not a `JournalSampleSource`) | 0.5 |

Totals (rows summed, not rounded): core + Linux + Windows (TFM route) + dev source = 6-9.5 days;
with the interop route instead, 8-11.5 days; adding macOS, 10-15.5 and 12-17.5 respectively.
Core + Linux + dev source alone is 4-5.5. These are estimates, not measured against a prototype
(unverified). The first draft's totals did not sum from its rows.

## New dependencies

- Linux: none new in practice. `Tmds.DBus.Protocol` is already transitive; promote it to a direct
  reference with an explicit version. Optional: `Tmds.DBus.SourceGenerator` (dev-time).
- Windows: no NuGet package if the TFM is changed to `net10.0-windows10.0.xxxxx` (WinRT projections
  come with the SDK targeting pack). The alternative that avoids a TFM is late-bound/hand-written
  interop (SapiVoice precedent) or a CsWinRT-based projection. (unverified)
- macOS: none for ObjC P/Invoke; otherwise a first-party native dylib built in CI.
- No cross-platform media-key library recommended.

## Risks

- Windows TFM change ripples through CI (Linux runners build the solution; needs
  `EnableWindowsTargeting`), the installer `MinVersion`, and the Core/App split. Mitigation: late-bound
  interop like `SapiVoice`, an isolated Windows-only assembly, or `RegisterHotKey`.
- Silent side effects: any command path that reaches `RadioPlayerService.PlayAsync` enables the radio,
  persists settings and un-suspends developer-mode audio (see Gating).
- SMTC state is manual with LibVLC; if `PlaybackStatus` drifts from reality, Windows sends keys to the
  wrong app or shows a stale flyout.
- Stealing keys from the user's other media apps if session ownership is not released (see above).
- macOS: ObjC block interop fragility, and no shipped macOS artifact to test signing/notarisation against.
- Cannot be fully exercised in CI; needs manual checks on real desktops (GNOME, KDE, Windows 11, macOS).
- Threading: OS callbacks arrive off the UI thread; the router must not touch Avalonia objects directly.
- Wayland: global-hook approaches (SharpHook) are supported only with limitations and elevated
  privileges, which is impractical for a desktop app; that is why MPRIS is preferred.
- Flatpak bus-name policy: now verified (see above); the remaining risk is the runtime name selection.

## Open questions for the implementation issue

1. Is a Windows-only TFM/project acceptable, or do we ship `RegisterHotKey` first?
2. Should the feature be opt-in (Settings toggle) or on by default? Suggest on by default with a toggle.
3. When the radio is stopped, should Play be able to start the last station (claim the session)? That requires
   holding the session while idle, which conflicts with session ownership; suggest no.
4. Is macOS in scope before a macOS release artifact exists?
