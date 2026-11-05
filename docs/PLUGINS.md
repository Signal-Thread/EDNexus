# EDNexus plugin developer guide

How to build, test, package and ship a plugin for EDNexus. It is written against the code in this
repository (`src/EDNexus.Plugins.Abstractions` and `src/EDNexus.Plugins.Hosting`); where something
is not implemented yet, this guide says so rather than describing the intended design.

> **Read this first: what works today.**
>
> | Works today | Not available yet |
> |---|---|
> | The SDK contract (`EDNexus.Plugins.Abstractions` 2.0): events, read-only state, storage, logging | **The desktop app does not load plugins.** Wiring the host into the app is in progress; today plugins run in the **CLI harness** and in tests |
> | Manifest, folder layout, `.ednplugin` packaging and install (`EDNexus.Plugins.Hosting`) | **UI injection**: no dashboard widget or overlay panel contracts exist ([UI injection](#11-ui-injection-planned)) |
> | Loading in an isolated, unloadable `AssemblyLoadContext`; fault isolation; quarantine | A consent screen, an in-app plugin manager, a command-line installer, a NuGet package for the SDK, a plugin gallery |
> | The CLI harness: `--once --plugin <folder>` / `--plugins` / `--plugins-strict` | Handler / `Initialize` timeouts, and a stable guarantee about what `ui.*` will look like |
>
> A runnable, tested example is [`samples/HelloCommander`](../samples/HelloCommander/README.md).
> The on-disk format is specified in
> [`src/EDNexus.Plugins.Hosting/PLUGIN_FORMAT.md`](../src/EDNexus.Plugins.Hosting/PLUGIN_FORMAT.md);
> this guide summarises it and adds the how-to.

**Contents**

1. [Quick start](#1-quick-start)
2. [Project setup](#2-project-setup)
3. [The plugin class and its lifecycle](#3-the-plugin-class-and-its-lifecycle)
4. [Manifest reference (`plugin.json`)](#4-manifest-reference-pluginjson)
5. [Capabilities](#5-capabilities)
6. [Threading and fault isolation](#6-threading-and-fault-isolation)
7. [Events](#7-events)
8. [Commander state](#8-commander-state)
9. [Storage](#9-storage)
10. [Logging](#10-logging)
11. [UI injection (planned)](#11-ui-injection-planned)
12. [Testing and running](#12-testing-and-running)
13. [Packaging and installing (`.ednplugin`)](#13-packaging-and-installing-ednplugin)
14. [SDK versioning and compatibility](#14-sdk-versioning-and-compatibility)
15. [API reference](#15-api-reference)
16. [Known limitations](#16-known-limitations)

---

## 1. Quick start

You will build a plugin, "Jump Counter", that logs `CMDR <name> - N jumps, now in <system>` for each
hyperspace jump, and run it in the CLI harness. You need the **.NET 10 SDK** and a clone of this
repository (it is where the SDK and the harness live; there is no NuGet package yet).

Do all of this in a scratch directory *outside* the clone. `$EDNEXUS` below is the clone's path.

### 1.1 Build the SDK once

```sh
dotnet build "$EDNEXUS/src/EDNexus.Plugins.Abstractions" -c Release
# -> $EDNEXUS/src/EDNexus.Plugins.Abstractions/bin/Release/net10.0/EDNexus.Plugins.Abstractions.dll
```

### 1.2 Create the project

```sh
dotnet new classlib -n Acme.JumpCounter -f net10.0 -o JumpCounter
cd JumpCounter
rm Class1.cs
```

Replace the contents of `Acme.JumpCounter.csproj` with (adjust the `HintPath`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="EDNexus.Plugins.Abstractions">
      <HintPath>/path/to/EDNexus/src/EDNexus.Plugins.Abstractions/bin/Release/net10.0/EDNexus.Plugins.Abstractions.dll</HintPath>
      <!-- Do not copy the SDK next to the plugin: the host always supplies its own copy. -->
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <!-- Ship the manifest next to the assembly. -->
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

### 1.3 Write the plugin

`JumpCounterPlugin.cs`:

```csharp
using EDNexus.Plugins.Abstractions;

namespace Acme.JumpCounter;

public sealed class JumpCounterPlugin : IEDNexusPlugin
{
    private IPluginContext? _context;
    private IDisposable? _subscription;
    private long _jumps;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _subscription = context.Events.On("FSDJump", OnJump);
        context.Log.Info("Jump Counter ready.");
    }

    public void Shutdown()
    {
        _subscription?.Dispose();
        _context?.Log.Info("Jump Counter stopped.");
    }

    private void OnJump(IJournalEvent e)
    {
        var state = _context!.State.Snapshot();
        var system = e.GetString("StarSystem") ?? state.StarSystem ?? "an unknown system";
        _context.Log.Info($"CMDR {state.Name ?? "Unknown"} - {Interlocked.Increment(ref _jumps)} jumps, now in {system}");
    }
}
```

### 1.4 Write the manifest

`plugin.json`:

```json
{
  "id": "com.acme.jumpcounter",
  "name": "Jump Counter",
  "version": "1.0.0",
  "sdkVersion": "2.0",
  "entryAssembly": "Acme.JumpCounter.dll",
  "entryType": "Acme.JumpCounter.JumpCounterPlugin",
  "capabilities": [ "events", "state" ]
}
```

### 1.5 Build into a folder named after the id

The host expects `<id>/plugin.json` beside `<id>/<entryAssembly>`, and the folder name must equal the
manifest `id`:

```sh
dotnet build -c Release -o out/com.acme.jumpcounter
ls out/com.acme.jumpcounter     # Acme.JumpCounter.dll  plugin.json  (+ .deps.json, .pdb)
```

### 1.6 Run it

The harness replays a journal through the real plugin host. Use your latest journal (omit `--dir`) or
any folder holding a `Journal.*.log` (this repository ships a synthetic one in
[`samples/HelloCommander/sample-journal`](../samples/HelloCommander/sample-journal)):

```sh
dotnet run --project "$EDNEXUS/src/EDNexus.Cli" -- --once \
  --dir "$EDNEXUS/samples/HelloCommander/sample-journal" \
  --plugin out/com.acme.jumpcounter
```

You should see `[plugin info]` lines for each jump and, in the `Plugins` section of the summary,
`com.acme.jumpcounter 1.0.0  [Loaded]` with `events : 3 seen`. A status other than `Loaded` comes
with a `reason :` line; see [Troubleshooting](#troubleshooting). The harness runs your code in-process
with every declared capability granted and **no consent prompt**; it replays the journal you point it
at, so use `--dir` with a synthetic journal when you do not want a plugin near your real one.

---

## 2. Project setup

A plugin is a normal .NET class library.

- **Target `net10.0`.** The plugin loads into the host's runtime.
- **Reference only `EDNexus.Plugins.Abstractions`.** Never `EDNexus.Core`, `EDNexus.Plugins.Hosting`,
  Avalonia, or any other `EDNexus.*` / `EliteDangerous.*` assembly. The host deliberately does not
  resolve its own assemblies for plugins: one that references them without shipping its own copy fails
  to load with a `FileLoadException` naming the assembly. `EDNexus.Plugins.Abstractions` itself is
  always bound to the **host's** copy, even if you bundle one, so mark the reference `Private=false`.
- **Getting the SDK.** There is no NuGet package. Either keep a `ProjectReference` to
  `src/EDNexus.Plugins.Abstractions/EDNexus.Plugins.Abstractions.csproj` (what the sample does, if you
  work inside a clone) or build that project and reference the DLL with a `HintPath` as above. The
  DLL's assembly version is the SDK version (`2.0.0.0`), which the host reads from your compiled
  plugin (see [section 14](#14-sdk-versioning-and-compatibility)); build against the SDK you declare.
- **Your own dependencies** go in the plugin folder next to the entry assembly. They resolve from
  there (through `<name>.deps.json` if present, otherwise any assembly beside the entry assembly), in
  an isolated load context, so two plugins can ship different versions of the same library. Anything you
  do not ship resolves from the host process (the .NET base class library).
- **Nullable and warnings.** The SDK is annotated for nullable reference types. Turn them on.
- **Do not rely on file locations.** The entry assembly is loaded from memory, so
  `Assembly.GetExecutingAssembly().Location` is an empty string and there is no PDB (stack traces have no
  line numbers). Use [storage](#9-storage) for data.

---

## 3. The plugin class and its lifecycle

The entry type (`entryType` in the manifest) must be a **concrete, non-generic class implementing
`IEDNexusPlugin` with a public parameterless constructor** (make it `public sealed`). The host constructs it
without arguments.

```
folder found ─► plugin.json valid? ─► compatible? ─► consent ─► load assembly ─► new Plugin() ─► Initialize(context)
                                                                                                    │
                                                                       running: handlers receive events
                                                                                                    │
                                                              Shutdown() ─► context disposed ─► ALC unloaded
```

| Stage | What the host does | If it fails |
|---|---|---|
| Discover | Every folder under the plugins root whose name does not start with `.`, in ordinal order | `Rejected` if `plugin.json` is missing/invalid, the folder name is not exactly the `id`, or two folders claim one `id` (all claimants are rejected) |
| Compatibility | `PluginCompatibility.Check` (SDK + `minAppVersion`) and a check of the SDK version your assembly was compiled against | `Incompatible`; no plugin code ran |
| Consent | The app's consent callback decides whether the plugin may run and with which capabilities | `Denied`; no plugin code ran |
| Load | Reads the entry assembly (at most 64 MiB) into a collectible load context | `Failed` / `Rejected` (entry type missing, wrong shape) |
| Construct + `Initialize(context)` | On the thread that runs the load pass | `Failed`: the host calls `Shutdown()` best-effort, disposes the context, unloads the context. The reason records the exception type and message |
| Run | Events are delivered to your handlers ([section 7](#7-events)) | Handler exceptions are reported, delivery continues; too many get you [quarantined](#6-threading-and-fault-isolation) |
| `Shutdown()` | On unload: app exit, plugin disabled, reload. A throw is reported, not propagated | Reported in the unload result |
| Teardown | The context is disposed (event delivery stops), the host waits up to 5 s for running handlers, then drops all references and unloads the load context | A handler still running at that point is reported as **stuck** and the unload is not clean |

Rules for your class:

- Subscribe and register everything in `Initialize`. The context is valid from `Initialize` until
  `Shutdown` returns; **do not use it afterwards**.
- `Shutdown` may run **while a handler is still running** (handlers run on a different thread). Guard
  shared state (the sample uses `Interlocked`), and do not assume handlers are quiet while you tear down.
- Release what you started. A thread, timer, or a handler you attached to a static event (for example
  `AppDomain.ProcessExit`) is outside the host's control: it keeps running and keeps the load context
  alive after "unload". Unloading is cooperative.
- `Initialize` may throw `UnauthorizedAccessException` if you use a capability you were not granted
  ([section 5](#5-capabilities)); catch it when the capability is optional.

---

## 4. Manifest reference (`plugin.json`)

UTF-8 JSON, at most **64 KiB**. A BOM, `//` comments and trailing commas are tolerated; property names
match case-insensitively; **unknown properties are ignored** (so a newer manifest loads on an older
host); a property given twice is an error. An invalid manifest is rejected with every reason listed;
parsing never throws.

### Annotated example

```jsonc
{
  // REQUIRED. Lowercase reverse-DNS: at least two dot-separated segments of [a-z0-9_-], each starting
  // and ending with a letter or digit; at most 128 characters. Unique, and also the install folder name.
  "id": "com.acme.jumpcounter",

  // REQUIRED. Display name, at most 100 characters; no control characters and no invisible formatting
  // characters (bidi overrides, zero-width spaces/joiners).
  "name": "Jump Counter",

  // REQUIRED. The plugin's own version, strict SemVer 2.0.0 ("1.2.0", "2.0.0-beta.1").
  "version": "1.2.0",

  // optional, at most 200 characters, same character rules as "name".
  "author": "Acme Plugins",

  // optional, at most 2000 characters; newlines and tabs allowed.
  "description": "Counts hyperspace jumps this session.",

  // REQUIRED. The EDNexus.Plugins.Abstractions contract version you built against, "major.minor"
  // exactly (no patch). Currently "2.0". See section 14.
  "sdkVersion": "2.0",

  // optional. Minimum EDNexus app version, SemVer.
  "minAppVersion": "0.0.1",

  // REQUIRED. Path of the entry .dll relative to the plugin folder, '/'-separated, no '..'.
  "entryAssembly": "Acme.JumpCounter.dll",

  // REQUIRED. Full CLR name of the IEDNexusPlugin class (nested types use '+'; no generics).
  "entryType": "Acme.JumpCounter.JumpCounterPlugin",

  // optional. Subset of: events, state, ui.dashboard, ui.overlay, storage, network. Exact, lowercase.
  // Anything else rejects the whole manifest; duplicates are collapsed; at most 32 entries.
  "capabilities": [ "events", "state", "storage" ]
}
```

The reference manifest [`plugin.sample.json`](../src/EDNexus.Plugins.Hosting/plugin.sample.json) is
parsed by a unit test, so it cannot rot; so is the sample's
[`plugin.json`](../samples/HelloCommander/plugin.json).

### Field summary

| Field | Required | Limits / rules |
|---|---|---|
| `id` | yes | Lowercase reverse-DNS as above. The folder holding the plugin must have exactly this name |
| `name` | yes | 1-100 chars, no control / invisible formatting characters |
| `version` | yes | SemVer 2.0.0 |
| `author` | no | at most 200 chars |
| `description` | no | at most 2000 chars |
| `sdkVersion` | yes | `"major.minor"` only. Compatible when the major equals the host's and the minor is not above the host's ([section 14](#14-sdk-versioning-and-compatibility)) |
| `minAppVersion` | no | SemVer. The host rejects the plugin (`Incompatible`) when the running app is older |
| `entryAssembly` | yes | Relative, `/`-separated, inside the folder, must exist |
| `entryType` | yes | at most 512 chars, namespace-qualified, no generics |
| `capabilities` | no | See [section 5](#5-capabilities) |

Your plugin can read its own manifest at run time as `context.Manifest` (`PluginManifest`). Note that
`Manifest.Capabilities` is what you **declared**, not what the user **granted**.

---

## 5. Capabilities

A capability is a request in `plugin.json`. The user (through the host's consent callback) decides what
is actually granted: the granted set is the declared set intersected with what the consent returned
(anything granted but undeclared is ignored). The consent screen itself is not built yet; in the CLI
harness everything you declare is granted.

An ungranted capability does not silently do nothing: the matching part of the context throws
**`UnauthorizedAccessException`** on every call, with a message naming your plugin and the capability.

| Capability | Gives you | If not granted | Example |
|---|---|---|---|
| `events` | `context.Events`: subscribe to journal events. The feed carries everything the game logs, **including the commander's name and credit balance** (`LoadGame`, `Commander`, `Statistics`), location, ship, cargo, materials, missions. Data-equivalent to `state` | `Subscribe`, `SubscribeAny`, `On`, `OnAny` throw | `"capabilities": ["events"]` |
| `state` | `context.State`: the commander's **in-game name and credit balance**, system, body, station, ship, cargo and materials, as of the last completed event. No narrower grant exists for the name or balance | Every member, including `Snapshot()`, throws | `["state"]` |
| `storage` | `context.Storage`: per-plugin key/value storage in `<plugins root>/<id>/data/` ([section 9](#9-storage)) | `GetString`, `SetString`, `Remove` throw. **Also throws, even when granted, if the host was given no plugins root** (the host app does not configure one yet) | `["storage"]` |
| `network` | Nothing in the API. It is a **declaration** that your plugin makes network calls. Cannot be enforced in-process, so declare it honestly; the user is warned. With `events` or `state` the plugin can send everything above off the machine, and consent text must say so | n/a. See also *developer mode* below | `["events", "network"]` |
| `ui.dashboard` | **Reserved.** Intended for dashboard widgets ([UI injection](#11-ui-injection-planned)). The name is valid in a manifest, but today `context.Ui.Register(...)` refuses every call | `Register` throws (it always does today) | `["ui.dashboard"]` |
| `ui.overlay` | **Reserved**, as above, for in-game overlay panels | as above | `["ui.overlay"]` |

Declare only what you use; a request you do not need is a reason for a user to refuse you. An unknown
string (a typo, or a capability from a newer SDK) rejects the manifest rather than being ignored.

**Developer mode.** The app's developer mode feeds the engine fabricated events. A plugin that
**declares** `network` (granted or not) never sees them and its `State` reads as an unknown commander.
Every other plugin sees them with `IJournalEvent.IsSimulated == true`. Never forward simulated events
off the machine.

---

## 6. Threading and fault isolation

This is what the code does today; it is not a promise of a sandbox.

### Where your code runs

| Your code | Runs on | Notes |
|---|---|---|
| Constructor, `Initialize`, `Shutdown` | The thread running the host's load/unload pass | Not the UI thread: the supervisor documents that lifecycle calls must come from a background thread. A constructor, `Initialize` or `Shutdown` that **never returns blocks the whole load/unload pass**. There is no timeout yet |
| Event handlers | A dedicated background thread **per plugin** (named `EDNexus plugin: <id>`), **never the journal thread and never the UI thread** | One event at a time, in journal order, handlers for one event in registration order. A slow handler delays only your plugin's later events |
| Anything you start (threads, timers, tasks) | Wherever you put it | Yours to stop in `Shutdown`; the host cannot |

Because handlers are off the UI thread, **never touch UI objects from a handler**. A way for plugins to
hand work to the UI thread does not exist yet (no `PostToUi`); it arrives with the UI contracts.

### What the host contains

- **Handler exceptions.** Each handler call is wrapped. A throw is reported to the host as text (plugin
  id, event name, exception type, message; never the exception object, which would pin your load
  context) and delivery to your other handlers and to other plugins continues.
- **Quarantine.** The app's `PluginSupervisor` counts failed calls per plugin. More than **10 failures
  within 1 minute** (the default `PluginFaultPolicy`) unloads the plugin and marks it *Quarantined*. That
  lasts until the user enables or reloads it, or the app restarts; it is not persisted. A plugin
  quarantined this way is unloaded through the normal `Shutdown` path. Do not throw from a handler as
  control flow. (The CLI harness only counts handler errors; it does not quarantine.)
- **`Initialize` / constructor failures** mark the plugin `Failed` with the reason; other plugins are
  unaffected.
- **Backpressure.** Each plugin has its own queue holding only events some handler of that plugin
  matches, bounded by **8192 events and about 16 MiB**. When either is exceeded the **oldest** events
  are dropped (counted by the host; your plugin is not told). A startup replay of a long session fits;
  a plugin stalled in a handler loses its oldest events.
- **Unload.** After `Shutdown` and context disposal the host waits up to `PluginHost.UnloadTimeout`
  (default 5 s) for a running handler to return. If it does not, the result reports a **stuck handler**
  (`PluginUnloadResult.Stuck`): your code is still on a stack, the unload is not clean, your files stay
  locked (on Windows) until it returns, and the app is expected to refuse to replace or delete the
  folder meanwhile.

### What the host does *not* protect against

- **Isolation is not a security sandbox.** The `AssemblyLoadContext` isolates dependency *versions* and
  makes unloading possible. Your code runs **in-process with the host's full trust**: it can read files,
  use the network, and use reflection on the host. A user who installs a plugin is trusting its author.
  There is no code signing or package signature either.
- A stack overflow, `Environment.FailFast` or an unhandled exception on a thread **you** create ends
  the whole process. Catch everything on threads you own.
- "Unloaded" and "failed" mean the host released everything *it* holds. They do not prove your code
  stopped.
- No CPU, memory or time limits on your code.

### Reading state from handlers

By the time a handler runs, the host has already folded that event into `context.State`, and the state
may by then reflect **later** events too. Read related values off one `Snapshot()` so they agree, and
prefer the event's own fields for facts about *that* event (the sample reads `StarSystem` from the
event, falling back to the state).

---

## 7. Events

```csharp
IDisposable sub = context.Events.On("FSDJump", e => { /* ... */ });     // removable
context.Events.Subscribe("Docked", e => { /* ... */ });                 // lives until unload
context.Events.SubscribeAny(e => { /* every event */ });
```

- Names match the journal's `event` field, **case-sensitive and exact** (`"FSDJump"`).
- `On` / `OnAny` return a handle; disposing it removes the handler (idempotent; an event already queued
  for a removed handler is not delivered). `Subscribe` / `SubscribeAny` cannot be removed before unload.
  On a host that predates removable subscriptions `On`/`OnAny` throw `NotSupportedException`.
- A plugin that never subscribes costs the host nothing.

### `IJournalEvent`

| Member | Meaning |
|---|---|
| `Event` | The event name |
| `Timestamp` | The `timestamp` field, or `DateTimeOffset.MinValue` if absent/unparsable |
| `IsHistorical` | `true` when replayed from an existing journal at startup, not seen live. Suppress alerts and do not double-count in persistent totals for these |
| `IsSimulated` | `true` for events fabricated by developer mode |
| `GetString / GetInt64 / GetDouble / GetBool(field)` | The value, or `null` when the field is missing or the wrong type. They never throw |
| `GetLocalised(field)` | `<field>_Localised`, falling back to the raw field. Prefer it for anything shown to the user |
| `Payload` | The whole event as an immutable, detached `JsonElement`, for arrays and nested objects |

**Parse defensively.** Journal events change between game updates and fields come and go; read only what
you need and treat every field as optional.

**Do not bind `Payload` to your own types with reflection-based `System.Text.Json`**
(`Payload.Deserialize<MyType>()`). The serializer keeps a process-wide cache entry per type that pins
your load context, so the plugin could never be updated or reinstalled without restarting EDNexus. That
is why the SDK has no `Deserialize<T>` (removed in SDK 2.0). Navigate the element
(`TryGetProperty`, `EnumerateArray`, ...) or use a source-generated `JsonSerializerContext` defined in
your plugin.

---

## 8. Commander state

`context.State` is an `IReadOnlyCommanderState` (needs `state`). Only the host's tracker can change the
underlying state; you only read.

| Member | Notes |
|---|---|
| `Name` | In-game commander name, `null` until known. Personally identifying |
| `Balance` | Credits. Sensitive |
| `Ship`, `ShipName` | Internal ship symbol; commander-assigned name |
| `StarSystem`, `Body` | `null` until known / when not near a body |
| `Docked`, `StationDisplayName` | `StationDisplayName` is `null` when undocked |
| `LastUpdated` | UTC time of the last event that updated the state |
| `Cargo`, `RawMaterials`, `ManufacturedMaterials`, `EncodedMaterials` | Name to count; immutable copies |
| `Snapshot()` | One consistent, immutable copy of everything above |

The state is as of the last **completed** event, so you never see an event half-applied. Separate
property reads can still straddle an event: use `Snapshot()` when values must agree. Members added
after 2.0 will arrive with default implementations, so test doubles you write keep compiling.

---

## 9. Storage

`context.Storage` (needs `storage`) is a small string key/value store, kept as files in
`<plugins root>/<id>/data/`.

| Rule | Value |
|---|---|
| Keys | 1-100 chars of `A-Z a-z 0-9 . _ -`; **case-insensitive** (`Foo` is `foo`). Anything else: `ArgumentException` |
| Values | Valid Unicode text, at most 1 MiB as UTF-8. Larger or a lone surrogate: `ArgumentException` |
| Quota | At most 256 keys and 8 MiB in total per plugin; exceeding it: `IOException` |
| Reads | `GetString` returns `null` **only** when the key is unset. A value that exists but cannot be read throws `IOException` / `UnauthorizedAccessException`, so you never mistake a glitch for "no data" and overwrite good data |
| Not granted / unloaded | `UnauthorizedAccessException` on every call |

`PluginStorageExtensions` adds `ReadJson<T>(key)` / `WriteJson<T>(key, value)`. `ReadJson` returns
`default` for missing or malformed JSON but lets IO errors through. They use reflection-based
`System.Text.Json`, so by the same reasoning as in [section 7](#7-events) prefer built-in types
(`string`, numbers, `Dictionary<string,long>`, ...) over plugin-defined classes, or serialize with a
source-generated context yourself and use `GetString` / `SetString`. (The XML docs on the extensions
do not mention this; it follows from how the serializer caches types.)

Schema versioning is yours: store a version key next to your data. **A plugin update that replaces
the plugin folder does not yet preserve `data/`.** In the CLI harness storage is an in-memory stub with
none of these limits, and the desktop host does not configure storage yet, so write storage code that
survives refusal as the sample does:

```csharp
try { context.Storage.SetString("lifetime-jumps", total.ToString()); }
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* carry on without persistence */ }
```

---

## 10. Logging

`context.Log` (`IPluginLog`: `Debug`, `Info`, `Warn`, `Error(message, exception?)`) is scoped to your
plugin, and needs no capability. Where the output ends up is the host's choice: the CLI harness writes
`[plugin info] ...` lines to stderr and counts `Error` calls as errors (`--plugins-strict` then exits
`1`). The desktop app does not load plugins yet, so it has no log destination to document.

Do not log secrets or the commander's data unnecessarily; logs may be shared in bug reports.

---

## 11. UI injection (planned)

> **Not available yet.** There is no dashboard widget or overlay panel contract, no
> `EDNexus.Plugins.Abstractions.Ui` assembly, no `IDashboardWidget` / `IOverlayPanel`, and no way to
> post work to the UI thread. The issues that track this (#58, #59, #62) are open. Do not build against
> names you may have seen in planning discussions.

What exists is a placeholder:

```csharp
public interface IUiRegistry { void Register(string id, object descriptor); }   // context.Ui
```

The SDK keeps this assembly free of Avalonia on purpose, so plugins and the host can load and validate
without a UI. `ui.dashboard` and `ui.overlay` are valid capability names that the manifest parser
accepts, but the host's registry refuses every `Register` call with `UnauthorizedAccessException`,
whatever is granted. So today:

- You *can* declare `ui.dashboard` / `ui.overlay`; it only adds a line to the (future) consent prompt.
  The sample does not declare them.
- You *cannot* show anything in the app. Plugins that must show output today have to use logs, or
  files / network you manage yourself.
- Expect the UI contracts to be **new SDK surface** (see [section 14](#14-sdk-versioning-and-compatibility)),
  and design your plugin so its logic is separate from any future widget.

---

## 12. Testing and running

### In the CLI harness

```sh
# Load ONE plugin folder (copied to a temp folder first; siblings are never scanned)
dotnet run --project src/EDNexus.Cli -- --once --plugin path/to/com.acme.jumpcounter

# Load every plugin in EDNEXUS_PLUGINS_DIR (or the default plugins folder) before the replay
dotnet run --project src/EDNexus.Cli -- --once --plugins

# CI: as --plugins / --plugin, but exit 1 on a non-Loaded plugin, handler/log error,
# drain timeout, or unload problem.  Exit: 0 ok, 1 no journal / plugin problems, 2 usage error
dotnet run --project src/EDNexus.Cli -- --once --plugins-strict --plugin path/to/com.acme.jumpcounter

# Use a specific journal folder
... -- --once --dir path/to/journals --plugin ...
```

What the harness stands in for: UI registrations are *counted*, not rendered; storage is an **in-memory
stub** with none of the real key validation or quotas; every declared capability is granted without
consent. A stderr warning appears for plugins declaring `network` together with `events`/`state`.
Plugins gated against the CLI's own version; override with `EDNEXUS_APP_VERSION`. The summary reports,
per plugin: status and reasons, capabilities, events seen/dropped, UI registrations, storage writes, log
lines, errors.

**Plugin code runs in-process with full trust against the journal you point it at, including the
commander name and balance. Only run plugins you trust.**

### The plugins folder

| OS | Plugins root |
|---|---|
| Windows | `%LOCALAPPDATA%\EDNexus\plugins\` |
| Linux | `~/.local/share/EDNexus/plugins/` (Flatpak: `~/.var/app/<app-id>/data/EDNexus/plugins/`) |
| macOS | `~/Library/Application Support/EDNexus/plugins/` |

Set `EDNEXUS_PLUGINS_DIR` to an **absolute** path to use another folder while developing (relative
values are ignored). Each plugin is one folder named by its id. Hidden folders (names starting with `.`)
are ignored. `--plugins` runs install recovery on that folder, so it is not strictly read-only.

### Unit tests

You can unit-test a plugin without the host: `IPluginContext` and its parts are plain interfaces. The
repository's `tests/EDNexus.Plugins.Abstractions.Tests/TestDoubles.cs` has reusable doubles, and
`tests/EDNexus.Samples.Tests/HelloCommanderTests.cs` shows the end-to-end version (real `PluginHost`
and `PluginBridge`, real journal events, assertions on what the plugin logs and saves). Event handlers
run on another thread, so poll with a timeout rather than asserting immediately.

### Troubleshooting

The harness prints a `reason :` line for any plugin that is not `Loaded`.

| Status | Typical causes |
|---|---|
| `Rejected` | `plugin.json` missing or invalid (the reasons are listed); folder name is not exactly the `id`; duplicate `id`; `entryAssembly` missing; `entryType` not found, not concrete / not generic-free, no public parameterless constructor, or does not implement the host's `IEDNexusPlugin` |
| `Incompatible` | `sdkVersion` major differs from the host's, or its minor is higher; or your assembly was compiled against a newer SDK than it declares; or `minAppVersion` is above the running app |
| `Denied` | The consent callback refused it |
| `Failed` | Your constructor or `Initialize` threw, or a dependency failed to load (a `FileLoadException` naming an `EDNexus.*` assembly means you referenced a host assembly) |
| `Disabled` | The user disabled it |

---

## 13. Packaging and installing (`.ednplugin`)

A `.ednplugin` is a **zip of the plugin folder**. `plugin.json` must be at the archive root, or inside
a single top-level folder that wraps everything (what "compress folder" tools produce).

```sh
cd out/com.acme.jumpcounter
zip -r ../com.acme.jumpcounter-1.0.0.ednplugin .
```

(Verified with Info-ZIP `zip` on Linux: the resulting package installs with `PluginInstaller`. On
Windows use any zip tool that stores `/`-separated, unencrypted entries and name the result
`*.ednplugin`; the Windows route has not been verified for this guide.)

The manifest must be named exactly `plugin.json`, be valid, and its `entryAssembly` must be in the
package with the same casing. There is no packaging command or tool: this is a plain zip.

**What the installer rejects** (`PluginInstaller` / `PluginPackage`): absolute paths, `..` or `.`
segments, empty segments, `:` and `<>"|?*`, control and invisible formatting characters, Windows device
names (`CON`, `nul.txt`, `COM1`), segments ending in a dot or space, names longer than 240 characters or
nested deeper than 16 levels, entries that collide case-insensitively (after Unicode normalisation),
symbolic links, encrypted entries; and packages over **64 MiB**, **2,000 entries**, **128 MiB per file**,
**256 MiB uncompressed in total**, or a **100:1 compression ratio** for files over 1 MiB. Extraction
re-checks sizes against the real bytes and verifies every file's CRC-32.

**Installing.** `PluginInstaller.Install(packagePath, pluginsRoot, replaceExisting)` validates the whole
package, extracts it to a `.staging-<guid>` folder and moves it to `<plugins root>/<id>/`. An id that is
already installed is rejected unless `replaceExisting` is set. A replace renames the old copy to
`.replaced-<id>.<guid>` first and moves it back if the swap fails; `RecoverInterrupted` finishes any
replace cut short by a crash the next time the host starts. Version numbers are **not compared**:
`replaceExisting` replaces whatever is there, older or newer. **There is no end-user install UI or
command yet**; until the app wires it in, the way to install by hand is to unzip the package into the
plugins root as a folder named after the id, so that `plugin.json` sits directly inside it
(`unzip com.acme.jumpcounter-1.0.0.ednplugin -d ~/.local/share/EDNexus/plugins/com.acme.jumpcounter`).

Distributing: nothing signs or verifies a package beyond its structure. Publish a checksum with your
download, and expect users to trust you personally.

**Updating a running plugin.** Because the entry assembly is read into memory (the file is not held
open), a rebuilt plugin can be copied over a running one and picked up by a reload. Dependency DLLs are
still loaded by path and are locked on Windows until the old copy is unloaded **and** collected.

---

## 14. SDK versioning and compatibility

What is **enforced today**, from `PluginSdk`, `PluginCompatibility` and the manifest parser:

1. **The SDK version is `major.minor`** (`PluginSdk.CurrentVersionString`, currently **`2.0`**). It is
   stamped on `EDNexus.Plugins.Abstractions` as a `[PluginSdkVersion]` assembly attribute, and the
   assembly's own version (`2.0.0.0`) tracks it; a unit test fails if they drift.
2. **You declare the version you built against** in `plugin.json` as `sdkVersion`, exactly
   `major.minor` (a patch component or anything else is rejected).
3. **Compatibility rule:** a plugin loads when its **major equals the host's** and its **minor is not
   greater than the host's**. So a plugin built for `2.0` runs on a host with `2.0`, `2.1`, ...; a plugin
   for `2.1` is refused by a `2.0` host; any `1.x` or `3.x` plugin is refused by a `2.x` host. The host
   speaks exactly one major version at a time; there is no compatibility shim for older majors.
4. **The manifest is a claim, not proof.** The host also reads, without loading it, the version of the
   `EDNexus.Plugins.Abstractions` reference recorded in your entry assembly's metadata and applies the
   same rule, so a manifest cannot understate the SDK a plugin needs. Build against the SDK you declare.
5. **`minAppVersion`** (optional SemVer) separately requires the running app to be at least that
   version.
6. **Failing the check never runs plugin code**: the plugin is `Incompatible`, with a reason such as
   `plugin targets SDK 1.0, but this EDNexus provides SDK 2.0`.
7. The plugin's **own** `version` is SemVer and is for display; the host does not compare plugin
   versions to anything.

The convention the SDK follows (documented in the code, not machine-enforced):

- **Major** is bumped for a breaking contract change. Precedent: **2.0** removed
  `IJournalEvent.Deserialize<T>()` because binding plugin types pinned the load context forever. A
  plugin built for 1.x was refused outright rather than allowed to fail with a
  `MissingMethodException` inside a handler.
- **Minor** is for additive growth. New members on existing interfaces are added with default
  implementations, so neither plugins nor test doubles built against an older minor break
  (`IReadOnlyCommanderState` already works this way). No minor version has been released yet: the SDK
  has gone `1.0` -> `2.0` and sits at `2.0`.

**Not decided / not implemented** (do not assume otherwise):

- No deprecation period, long-term-support window, or promise about how long a major is accepted.
- No automated API-compatibility check (no ApiCompat / public-API analyzer is configured); "additive
  only within a major" is a review convention.
- No guarantee yet about the shape of the not-yet-existing UI contracts. Adding them is expected to be
  an additive minor, but that is not promised.
- The SDK is not published as a package; the SDK version is not tied to the app version beyond
  `minAppVersion`.
- No rule for how plugin updates are discovered or delivered.

Practical advice: declare the **lowest** minor you actually need, build against that SDK, re-test when
the host's major changes, and pin your own dependencies.

---

## 15. API reference

Everything is in namespace `EDNexus.Plugins.Abstractions`. The XML docs in
`src/EDNexus.Plugins.Abstractions` are the authoritative text.

| Type | Purpose | Members |
|---|---|---|
| `IEDNexusPlugin` | Your entry point | `void Initialize(IPluginContext)`, `void Shutdown()` |
| `IPluginContext` | Handed to `Initialize` | `State`, `Events`, `Log`, `Storage`, `Ui`, `Manifest` |
| `IPluginEvents` | Event feed (`events`) | `Subscribe(name, handler)`, `SubscribeAny(handler)`, `IDisposable On(name, handler)`, `IDisposable OnAny(handler)` |
| `IJournalEvent` | One event | `Event`, `Timestamp`, `IsHistorical`, `IsSimulated`, `GetString`, `GetInt64`, `GetDouble`, `GetBool`, `GetLocalised`, `Payload` |
| `IReadOnlyCommanderState` | Commander snapshot (`state`) | `Name`, `Balance`, `Ship`, `ShipName`, `StarSystem`, `Body`, `Docked`, `StationDisplayName`, `LastUpdated`, `Cargo`, `RawMaterials`, `ManufacturedMaterials`, `EncodedMaterials`, `Snapshot()` |
| `IPluginStorage` | Key/value store (`storage`) | `GetString(key)`, `SetString(key, value)`, `Remove(key)` |
| `PluginStorageExtensions` | JSON helpers | `ReadJson<T>(key)`, `WriteJson<T>(key, value)` |
| `IPluginLog` | Scoped logger | `Debug`, `Info`, `Warn`, `Error(message, exception?)` |
| `IUiRegistry` | UI seam (**placeholder**) | `Register(string id, object descriptor)` |
| `PluginManifest` | Your parsed manifest | `Id`, `Name`, `Version`, `SdkVersion`, `Author`, `Description`, `MinAppVersion`, `EntryAssembly`, `EntryType`, `Capabilities`, `Declares(capability)` |
| `PluginCapabilities` | Capability names | `Events`, `State`, `UiDashboard`, `UiOverlay`, `Storage`, `Network`, `All`, `IsKnown(string?)` |
| `PluginSdk` | SDK version | `CurrentVersionString`, `CurrentVersion`, `IsCompatible(Version)`, `GetDeclaredVersion(Assembly)` |
| `PluginSdkVersionAttribute` | Stamped on the SDK assembly | `Version` |

Host-side types plugin authors do not reference but may see in reports: `PluginHost`, `PluginSupervisor`,
`PluginLoadStatus` (`Loaded`, `Rejected`, `Incompatible`, `Failed`, `Denied`, `Disabled`),
`PluginState` (`Enabled`, `Disabled`, `Quarantined`, `Failed`, `Loading`), `PluginInstaller`,
`PluginPackage`.

---

## 16. Known limitations

Collected from the sections above so nothing is a surprise:

- The desktop app does not load plugins yet; no in-app plugin manager or consent screen.
- No UI injection (dashboard / overlay), no UI-thread dispatcher.
- Storage is refused by the host until a plugins root is configured; a plugin update does not preserve
  `data/`.
- No timeouts on `Initialize` / `Shutdown` / handlers; a hung one blocks the pass or marks the unload stuck.
- Not a security sandbox; no code signing; `network` is a declaration, not enforcement.
- No NuGet package for the SDK; no `dotnet new` template; no packaging command.
- The SDK compatibility policy beyond "same major, equal-or-lower minor" is undecided (section 14).
