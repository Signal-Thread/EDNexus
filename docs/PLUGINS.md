# EDNexus plugin developer guide

How to build, test, package and ship a plugin for EDNexus. It is written against the code in this
repository (`src/EDNexus.Plugins.Abstractions` and `src/EDNexus.Plugins.Hosting`); where something
is not implemented yet, this guide says so rather than describing the intended design.

> **Read this first: what works today.**
>
> | Works today | Not available yet |
> |---|---|
> | The SDK contract (`EDNexus.Plugins.Abstractions` 2.0): events, read-only state, storage, logging | **A plugin you drop into the plugins folder will not run in the desktop app yet.** The app does load plugins (see below), but it runs a plugin only when `settings.json` holds a consent grant for it, and nothing writes grants until the management UI (#61) exists. **Today, run plugins in the CLI harness** (which grants everything you declare) and in tests |
> | Manifest, folder layout, `.ednplugin` packaging and install (`EDNexus.Plugins.Hosting`) | **UI injection**: no dashboard widget or overlay panel contracts exist ([UI injection](#11-ui-injection-planned)) |
> | Loading in an isolated, unloadable `AssemblyLoadContext`; fault isolation; quarantine | A consent screen and in-app plugin manager (#61), a command-line installer, a NuGet package for the SDK, a plugin gallery, a trust / signing model (#66) |
> | **The desktop app loads plugins** (`PluginService`, one `PluginRuntime` per engine) with **deny-all consent**: no grant, no run | A timeout on your constructor, `Initialize` or event handlers (only `Shutdown` is time-bounded), and a stable guarantee about what `ui.*` will look like |
> | The CLI harness: `--once --plugin <folder>` / `--plugins` / `--plugins-strict` | |
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

The harness is where a plugin runs today. Copying this folder into the desktop app's plugins folder
would **not** run it: the app is deny-all until a consent grant exists for the plugin
([section 5](#5-capabilities)), and nothing records grants yet. [Section 12](#12-testing-and-running)
lists the other ways the harness differs from the app.

---

## 2. Project setup

A plugin is a normal .NET class library.

- **Target `net10.0`.** The plugin loads into the host's runtime.
- **Reference only `EDNexus.Plugins.Abstractions`** from EDNexus. Never `EDNexus.Core`,
  `EDNexus.Plugins.Hosting`, or any other `EDNexus.*` / `EliteDangerous.*` assembly: the host
  deliberately does not resolve those for plugins, and one that references them without shipping its
  own copy fails to load with a `FileLoadException` naming the assembly. `EDNexus.Plugins.Abstractions`
  itself is always bound to the **host's** copy, even if you bundle one, so mark the reference
  `Private=false`.
- **Ship your own copy of every third-party library you use, and do not depend on the host's.** Only
  the `EDNexus.*` and `EliteDangerous.*` name prefixes are blocked. A library you reference but do not
  ship (Avalonia, say) is not refused: it resolves from the host process if the host happens to have
  it. That is not a contract. The host's own third-party libraries and their versions can change or
  disappear in any update, and the CLI harness and the desktop app carry different sets of them, so a
  plugin that leans on one can work in one place and fail (or break after an update) in another.
- **Getting the SDK.** There is no NuGet package. Either keep a `ProjectReference` to
  `src/EDNexus.Plugins.Abstractions/EDNexus.Plugins.Abstractions.csproj` (what the sample does, if you
  work inside a clone) or build that project and reference the DLL with a `HintPath` as above. The
  DLL's assembly version is the SDK version (`2.0.0.0`), which the host reads from your compiled
  plugin (see [section 14](#14-sdk-versioning-and-compatibility)); build against the SDK you declare.
- **Your own dependencies** go in the plugin folder next to the entry assembly. They resolve from
  there (through `<name>.deps.json` if present, otherwise any assembly beside the entry assembly), in
  an isolated load context, so two plugins can ship different versions of the same library. Anything you
  do not ship resolves from the host process: the .NET base class library, and also whatever other
  libraries the host process has loaded (see the previous point; do not rely on those).
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
| Consent | Reads the entry assembly (at most 64 MiB) into memory and hashes it and the folder's other files, then asks the consent callback whether the plugin may run and with which capabilities. In the app that means a matching persisted grant ([section 5](#5-capabilities)); the CLI harness grants everything you declare | `Denied`; no plugin code ran. A folder that contains a symbolic link / junction, or is over the size limits, is `Rejected` before consent is even asked |
| Load | Loads those same bytes into a collectible load context | `Failed` / `Rejected` (entry type missing, wrong shape) |
| Construct + `Initialize(context)` | On the thread that runs the load pass (a background thread in the app, not the UI thread). A new instance of your class is constructed on every load | `Failed`: the host calls `Shutdown()` best-effort (time-bounded, as below), disposes the context, unloads the context. The reason records the exception type and message |
| Run | Events are delivered to your handlers ([section 7](#7-events)) | Handler exceptions are reported, delivery continues; too many get you [quarantined](#6-threading-and-fault-isolation) |
| `Shutdown()` | On unload: app exit, the plugin being disabled, a reload, or the app **rebuilding its engine** (leaving developer mode, reset to live). On a dedicated worker thread; the host waits up to `PluginHost.ShutdownTimeout` (default 5 s). A throw is reported, not propagated | Reported in the unload result. If it has not returned in time the host reports a **stuck shutdown**, treats the unload as stuck, and carries on without it (your code is still running) |
| Teardown | The context is disposed (event delivery stops) whether or not `Shutdown` returned, the host waits up to `PluginHost.UnloadTimeout` (default 5 s, separate from the shutdown timeout) for running handlers, then drops all references and unloads the load context | A handler still running at that point is reported as **stuck** and the unload is not clean |

The whole sequence can run **several times in one process**. In the app, the plugin runtime is torn
down and rebuilt each time the engine is rebuilt (leaving developer mode, reset to live), so your
`Initialize` and `Shutdown` can run again in the same session, on a new instance of your class.

Rules for your class:

- Subscribe and register everything in `Initialize`. The context is valid from `Initialize` until
  `Shutdown` returns; **do not use it afterwards**. (If `Shutdown` overruns the timeout, the host
  disposes the context while your `Shutdown` is still running.)
- **Keep `Shutdown` short; it must return promptly.** It runs on a worker thread (not the UI thread
  and not the thread that called `Initialize`). If it has not returned within the host's timeout (5 s
  by default) the host stops waiting, reports the plugin as **stuck**, and moves on: your code is left
  running and its load context cannot finish unloading until it returns. A late return or a late
  exception is discarded.
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
  // and ending with a letter or digit; at most 128 characters. Unique, and also the install folder name,
  // so it must also be a legal folder name everywhere: an id whose text before its first '.' is a
  // Windows reserved device name (con., prn., aux., nul., com1. ... com9., lpt1. ... lpt9.) is rejected.
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
| `id` | yes | Lowercase reverse-DNS as above, and not starting with a Windows reserved device name (`con.`, `nul.`, `lpt1.`, ...). The folder holding the plugin must have exactly this name |
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
(anything granted but undeclared is ignored).

**A plugin dropped into the plugins folder will not run in the desktop app yet.** The app is
**deny-all**: it runs a plugin only if `settings.json` holds an entry `Plugins.Grants[<id>]` whose three
hashes all match what is on disk right now, and then only with the capabilities in that entry. The
consent screen and plugin manager (#61) are not built, and nothing else writes grants, so on a fresh
install no plugin runs. Use the CLI harness, which grants everything you declare and says so
(the trust and signing model is #66).

What a grant is bound to, so you know when your users will be asked again:

| Hash | Covers |
|---|---|
| `ManifestHash` | A fingerprint of every field of `plugin.json` (capabilities order-insensitively) |
| `EntryHash` | SHA-256 of the bytes of the entry assembly the host actually loads |
| `FilesHash` | A hash over every other file in the plugin folder (dependency DLLs, native libraries, `.deps.json`, resources), **except** the top-level `data/` folder (your storage, which changes at run time) |

- **Any change revokes the grant.** Change any field of `plugin.json` (even the version or the
  description; formatting and comments are not fields), rebuild the DLL, or change any other file
  outside `data/`, and the plugin comes back `Denied` until the user approves it again. A reload re-asks, because the host re-reads the files from disk each time.
- **Updating over an existing id needs re-consent**, however you do it (`PluginInstaller` with
  `replaceExisting`, or copying files over by hand): the installer knows nothing about grants, and the
  changed hashes are what trigger the new approval.
- **Links make a folder unhashable.** A symbolic link, junction or other reparse point anywhere in the
  plugin folder (outside `data/`, which is not entered) gets the plugin `Rejected` before consent is
  asked. So do a folder with more than 2,000 files and folders, a file over 128 MiB, more than 256 MiB
  in total, or nesting deeper than 16 levels. Do not ship links.
- **Stale grants are pruned.** After each load pass the app forgets the grants (and disable flags) of
  ids that have no folder under the plugins root, so removing a plugin folder by hand and reinstalling
  starts from "not approved". It skips this when the plugins root is missing, unreadable or holds no
  plugin folder at all.

An ungranted capability does not silently do nothing: the matching part of the context throws
**`UnauthorizedAccessException`** on every call, with a message naming your plugin and the capability.

| Capability | Gives you | If not granted | Example |
|---|---|---|---|
| `events` | `context.Events`: subscribe to journal events. The feed carries everything the game logs, **including the commander's name and credit balance** (`LoadGame`, `Commander`, `Statistics`), location, ship, cargo, materials, missions. Data-equivalent to `state` | `Subscribe`, `SubscribeAny`, `On`, `OnAny` throw | `"capabilities": ["events"]` |
| `state` | `context.State`: the commander's **in-game name and credit balance**, system, body, station, ship, cargo and materials, as of the last completed event. No narrower grant exists for the name or balance | Every member, including `Snapshot()`, throws | `["state"]` |
| `storage` | `context.Storage`: per-plugin key/value storage in `<plugins root>/<id>/data/` ([section 9](#9-storage)) | `GetString`, `SetString`, `Remove` throw. (A bridge built with no plugins root would also refuse them even when granted; the app and the CLI harness both configure a root, so this does not happen there) | `["storage"]` |
| `network` | Nothing in the API. It is a **declaration** that your plugin makes network calls. Cannot be enforced in-process, so declare it honestly; the user is warned. With `events` or `state` the plugin can send everything above off the machine, and consent text must say so | n/a. See also *developer mode* below | `["events", "network"]` |
| `ui.dashboard` | **Reserved.** Intended for dashboard widgets ([UI injection](#11-ui-injection-planned)). The name is valid in a manifest, but in the real host `context.Ui.Register(...)` refuses every call, whatever is granted. (The CLI harness is different: see [section 12](#12-testing-and-running)) | `Register` throws (in the real host it always does today) | `["ui.dashboard"]` |
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
| Constructor, `Initialize` | The thread running the host's load pass | Not the UI thread: the supervisor documents that lifecycle calls must come from a background thread (the app loads on a thread-pool thread). A constructor or `Initialize` that **never returns blocks the whole load pass**. There is no timeout for these yet |
| `Shutdown` | A **dedicated background worker thread** the host starts for the call (`EDNexus plugin Shutdown`), never the caller's thread | The host waits up to `PluginHost.ShutdownTimeout` (default 5 s). If it has not returned by then, the unload is reported as **stuck** (`stuck shutdown`) and goes on without it; your code keeps running, the context is still disposed, and a late return or exception is discarded. A `Shutdown` that never returns therefore no longer blocks the unload pass, but it does leave the load context unable to unload |
| Event handlers | A dedicated background thread **per plugin** (named `EDNexus plugin: <id>`), **never the journal thread and never the UI thread** | One event at a time, in journal order, handlers for one event in registration order. A slow handler delays only your plugin's later events |
| Anything you start (threads, timers, tasks) | Wherever you put it | Yours to stop in `Shutdown`; the host cannot |

Because handlers are off the UI thread, **never touch UI objects from a handler**. A way for plugins to
hand work to the UI thread does not exist yet (no `PostToUi`); that, and timeouts for your code, belong
to the threading / dispatch contract tracked in #62.

### What the host contains

- **Handler exceptions.** Each handler call is wrapped. A throw is reported to the host as text (plugin
  id, event name, exception type, message; never the exception object, which would pin your load
  context) and delivery to your other handlers and to other plugins continues.
- **Quarantine.** The app's `PluginSupervisor` counts failed calls per plugin. More than **10 failures
  within 1 minute** (the default `PluginFaultPolicy`) unloads the plugin and marks it *Quarantined*.
  Quarantine is in memory only and not persisted. The supervisor can clear it (enable / reload), but
  there is no UI to do so yet, so today it lasts until the app rebuilds its engine (which builds a
  fresh runtime) or restarts. A plugin quarantined this way is unloaded through the normal `Shutdown`
  path. Do not throw from a handler as control flow. (The CLI harness uses the same supervisor and
  default policy, so it quarantines too, and lists a quarantined plugin as a problem that fails
  `--plugins-strict`.)
- **`Initialize` / constructor failures** mark the plugin `Failed` with the reason; other plugins are
  unaffected.
- **Backpressure.** Each plugin has its own queue holding only events some handler of that plugin
  matches, bounded by **8192 events and about 16 MiB**. When either is exceeded the **oldest** events
  are dropped (counted by the host; your plugin is not told), and a single event larger than 16 MiB is
  dropped on arrival. A plugin that is slow, or stalled in a handler, loses its oldest events; that
  includes a slow catch-all (`SubscribeAny` / `OnAny`) handler during a fast journal replay. Do not
  assume every historical event reaches you in the harness.
- **Unload.** After `Shutdown` (bounded by `PluginHost.ShutdownTimeout`, see above) and context
  disposal the host waits up to `PluginHost.UnloadTimeout` (default 5 s, separate from the shutdown
  timeout, so a plugin stuck in both can take about 10 s) for a running handler to return. If it does not, the result reports a **stuck handler**
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
| `IsHistorical` | `true` when replayed from an existing journal at startup, not seen live. Suppress alerts and do not double-count in persistent totals for these. **Where you see them differs:** the CLI harness loads plugins *before* the replay, so you receive the replayed events (flagged historical). The desktop app loads plugins on a background thread *after* the journal replay has finished, so in the app you do **not** receive replayed events at startup, and `Initialize` already finds `context.State` populated; you receive only events from then on. Handle both |
| `IsSimulated` | `true` for events fabricated by developer mode |
| `GetString / GetInt64 / GetDouble / GetBool(field)` | The value, or `null` when the field is missing or the wrong type. For ordinary journal data they do not throw, but that is not a hard guarantee: `GetString` is built on `JsonElement.GetString`, which can throw `InvalidOperationException` for a string holding a lone-surrogate escape (the manifest parser has to work around the same thing). A throw from a handler is contained and counted as a fault, like any other |
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
the plugin folder does not yet preserve `data/`.**

Storage is the real sandboxed backend in both hosts: the desktop app and the CLI harness each give the
bridge the plugins root, and the key rules and quotas above are enforced for real in both. Where the
data lands:

| Run | Data folder |
|---|---|
| Desktop app | `<plugins root>/<id>/data/` |
| `--once --plugins` | `<plugins folder>/<id>/data/` (persists between runs, so use this to test persistence) |
| `--once --plugin <folder>` | the harness's scratch **copy** of your folder, which is deleted on exit, so nothing persists between `--plugin` runs |

Storage can still be refused (not granted, or a read / write error), so write storage code that
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
`1`). The desktop app writes your log lines to its own trace log file, one line each, tagged
`Plugin <id> [level]: ...`, in `ednexus-<timestamp>.log` under `<local app data>/EDNexus/logs/` (beside the
default plugins folder; one file per launch, the newest few are kept). There is no log viewer in the app
yet.

Do not log secrets or the commander's data unnecessarily; logs may be shared in bug reports.

---

## 11. UI injection (planned)

> **Not available yet.** There is no dashboard widget or overlay panel contract, no
> `EDNexus.Plugins.Abstractions.Ui` assembly, no `IDashboardWidget` / `IOverlayPanel`, and no way to
> post work to the UI thread. The UI registries are tracked by #58 (dashboard) and #59 (overlay); the
> threading / dispatch contract (timeouts, `PostToUi`) is #62; the consent and management UI is #61 and
> the trust / signing model is #66. All are open. Do not build against names you may have seen in
> planning discussions.

What exists is a placeholder:

```csharp
public interface IUiRegistry { void Register(string id, object descriptor); }   // context.Ui
```

The SDK keeps this assembly free of Avalonia on purpose, so plugins and the host can load and validate
without a UI. `ui.dashboard` and `ui.overlay` are valid capability names that the manifest parser
accepts, but the real host's registry (`DeniedUiRegistry`) refuses every `Register` call with
`UnauthorizedAccessException`, whatever is granted. (The CLI harness is the exception: it hands a plugin
granted `ui.dashboard` or `ui.overlay` a counting stub on which `Register` *succeeds*, so a plugin that
calls `Ui.Register` passes `--plugins-strict` but fails in the app. See
[section 12](#12-testing-and-running).) So today:

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

# CI: as --plugins / --plugin, but exit 1 on a non-Loaded plugin (a quarantined one included),
# handler/log error, drain timeout, or unload problem.
# Exit: 0 ok, 1 no journal / plugin problems, 2 usage error, harness cannot start, or (with
# --plugins-strict) no plugins folder could be determined
dotnet run --project src/EDNexus.Cli -- --once --plugins-strict --plugin path/to/com.acme.jumpcounter

# Use a specific journal folder
... -- --once --dir path/to/journals --plugin ...
```

The harness uses the same `PluginRuntime` / `PluginHost` / `PluginSupervisor` as the desktop app, so
loading, fault isolation, quarantine and the shutdown timeout behave the same. What it stands in for,
and where it differs from the app:

| | CLI harness | Desktop app |
|---|---|---|
| Consent | Every declared capability is granted without a prompt (a stderr warning appears for plugins declaring `network` together with `events`/`state`) | Deny-all unless a matching persisted grant exists; none can be written yet, so **no plugin runs** |
| Load time | **Before** the journal replay: you receive the replayed (`IsHistorical`) events | **After** the replay, on a background thread: no replayed events, `State` already populated |
| `Ui.Register` | A plugin granted `ui.dashboard` or `ui.overlay` gets a *counting stub* on which `Register` **succeeds** (counted, not rendered). Without either grant it throws like the app | `Register` **always throws** `UnauthorizedAccessException`, whatever is granted. A plugin that calls it passes `--plugins-strict` but fails in the app |
| Storage | The real sandboxed storage (key rules, 256 keys / 8 MiB quota, 1 MiB values), wrapped in a call counter. `--plugins`: `<plugins folder>/<id>/data`. `--plugin <folder>`: the scratch copy, deleted on exit | The real sandboxed storage in `<plugins root>/<id>/data` |
| Quarantine | Yes: default policy (more than 10 faults in 60 s). A quarantined plugin is listed as a problem and fails `--plugins-strict` | Yes, same policy; cleared when the engine is rebuilt |
| Lifecycle | One load and one unload per run | `Initialize` / `Shutdown` can run several times per process (engine rebuilds) |
| Logs | `[plugin info] ...` lines on stderr, counted | The app's trace log file |

Plugins are gated against the CLI's own version; override with `EDNEXUS_APP_VERSION`. The summary
reports, per plugin: status and reasons, capabilities, events seen/dropped, UI registrations, storage
writes and removes, log lines, errors.

**Plugin code runs in-process with full trust against the journal you point it at, including the
commander name and balance. Only run plugins you trust.**

### The plugins folder

The default root is `<.NET LocalApplicationData>/EDNexus/plugins` (`PluginPaths.DefaultRoot`; falling
back to `~/.local/share` if the OS reports no such folder, and to no plugins at all if there is no home
folder either):

| OS | Plugins root |
|---|---|
| Windows | `%LOCALAPPDATA%\EDNexus\plugins\` |
| Linux | `$XDG_DATA_HOME/EDNexus/plugins/`, i.e. `~/.local/share/EDNexus/plugins/` by default (Flatpak: `~/.var/app/<app-id>/data/EDNexus/plugins/`) |
| macOS | **Unverified.** The code uses whatever .NET reports as `LocalApplicationData` and does not special-case macOS. `~/Library/Application Support/EDNexus/plugins/` is what the code comments say is intended, but this has not been checked on a Mac (tracked in #259). Set `EDNEXUS_PLUGINS_DIR` rather than guessing |

Set `EDNEXUS_PLUGINS_DIR` to an **absolute** path to use another folder while developing (relative
values are ignored). Each plugin is one folder named by its id. Hidden folders (names starting with `.`)
are ignored. `--plugins` runs install recovery on that folder, so it is not strictly read-only.
With no usable per-user folder (and no override) nothing is loaded, and `--plugins-strict` exits `2`.

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
| `Rejected` | `plugin.json` missing or invalid (the reasons are listed); folder name is not exactly the `id`; duplicate `id`; `entryAssembly` missing; the plugin folder contains a symbolic link / junction or is over the size limits (so it cannot be hashed for consent); `entryType` not found, not concrete / not generic-free, no public parameterless constructor, or does not implement the host's `IEDNexusPlugin` |
| `Incompatible` | `sdkVersion` major differs from the host's, or its minor is higher; or your assembly *references* a newer minor (or a different major) of `EDNexus.Plugins.Abstractions` than this host provides (the host compares the version recorded in your DLL's metadata with its own SDK version, not with your manifest); or `minAppVersion` is above the running app |
| `Denied` | The consent callback refused it. **In the desktop app every plugin without a matching grant is `Denied`**, which today is every plugin, since nothing records grants yet (the app logs `Plugins: <id>: Denied` to its trace log). Not an issue in the CLI harness, which grants what you declare |
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
`.replaced-<id>.<guid>` first and moves it back if the swap fails. `RecoverInterrupted` does not
*finish* a replace that a crash cut short: the next time the host starts it **restores the previous
version** (moves `.replaced-<id>.<guid>` back to `<id>` if that folder is missing), deletes backups
whose replacement completed, and deletes leftover `.staging-*` and `.extract-*` folders. Version numbers
are **not compared**: `replaceExisting` replaces whatever is there, older or newer. A replaced plugin
needs a **fresh consent grant** in the app, because its files changed
([section 5](#5-capabilities)), and its `data/` folder is not preserved.

**There is no end-user install UI or command yet**; the way to install by hand is to unzip the package
into the plugins root as a folder named after the id, so that `plugin.json` sits directly inside it
(`unzip com.acme.jumpcounter-1.0.0.ednplugin -d ~/.local/share/EDNexus/plugins/com.acme.jumpcounter`).
**A plugin installed this way will not run in the desktop app yet**: the app is deny-all and nothing
records a consent grant until the management UI (#61) exists, so it shows up `Denied`. Test it with
the CLI harness (`--once --plugins`, or `--plugin <folder>`), which loads it with the capabilities it
declares.

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
   `plugin targets SDK 1.0, but this EDNexus provides SDK 2.0` (from the manifest) or `the entry assembly
   was built against SDK 2.1 (whatever its manifest declares), but this EDNexus provides SDK 2.0` (from
   the assembly reference).
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

Host-side types plugin authors do not reference but may see in reports or logs: `PluginHost`,
`PluginRuntime` (builds the host, bridge and supervisor for one engine; used by the app and the CLI
harness), `PluginSupervisor`, `PluginLoadStatus` (`Loaded`, `Rejected`, `Incompatible`, `Failed`,
`Denied`, `Disabled`), `PluginState` (`Enabled`, `Disabled`, `Quarantined`, `Failed`, `Loading`),
`PluginConsent` / `PluginConsentRequest` / `PluginContentHash` (the consent callback, the request it
receives, and the entry-assembly and folder hashes a grant is bound to), `PluginInstaller`,
`PluginPackage`, and, in the desktop app, `PluginService` (owns the persisted choices and one
`PluginRuntime` per engine).

---

## 16. Known limitations

Collected from the sections above so nothing is a surprise:

- **A plugin dropped into the plugins folder will not run in the desktop app yet.** The app does load
  plugins but is deny-all: no in-app plugin manager or consent screen (#61), so nothing records the
  grant a plugin needs. Run plugins in the CLI harness. Any edit to a plugin's files revokes its grant.
- No UI injection (dashboard #58 / overlay #59), no UI-thread dispatcher (#62). `Ui.Register` always
  throws in the app but succeeds in the CLI harness for a plugin granted a `ui.*` capability.
- A plugin update does not preserve `data/`.
- Timeouts: `Shutdown` is bounded (5 s by default, then reported stuck and left running), and so is the
  wait for a running handler at unload. There is no timeout on the constructor, `Initialize` or an event
  handler while it runs: a constructor or `Initialize` that never returns blocks the load pass, and a
  slow handler only delays (and eventually loses events of) its own plugin.
- Quarantine is not persisted and has no UI: it lasts until the engine is rebuilt or the app restarts.
- A library you reference but do not ship resolves from the host process if it can; that is not a
  contract (section 2).
- Not a security sandbox; no code signing; `network` is a declaration, not enforcement.
- No NuGet package for the SDK; no `dotnet new` template; no packaging command.
- The SDK compatibility policy beyond "same major, equal-or-lower minor" is undecided (section 14).
