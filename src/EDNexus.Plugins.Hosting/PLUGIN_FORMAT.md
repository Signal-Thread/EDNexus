# EDNexus plugin format

This describes the on-disk format of an EDNexus plugin: the `plugin.json` manifest, the folder
layout, and the `.ednplugin` package. It is implemented by `PluginManifestParser`,
`PluginPackage`, `PluginInstaller` and `PluginPaths` in this project (issue #54), and loaded by
`PluginHost` (issue #55; see [Discovery and loading](#discovery-and-loading)). Consent, the
capability bridge and the threading contract are covered under [Consent and capabilities](#consent-and-capabilities)
and [Wiring the host](#wiring-the-host).

## Where plugins live

Each installed plugin is a folder named after its id under the per-user plugins root, alongside
`settings.json` — **never** in the (read-only) install directory:

| OS | Plugins root |
|---|---|
| Windows | `%LOCALAPPDATA%\EDNexus\plugins\` |
| Linux | `~/.local/share/EDNexus/plugins/` (Flatpak: `~/.var/app/<app-id>/data/EDNexus/plugins/`) |
| macOS | `~/Library/Application Support/EDNexus/plugins/` |

Set `EDNEXUS_PLUGINS_DIR` to an **absolute** path to use a different folder while developing
(mirrors `EDNEXUS_JOURNAL_DIR`). It does not need to exist yet; relative values are ignored so the
root can never depend on the current directory.

```
plugins/
  com.acme.jumpcounter/
    plugin.json
    Acme.JumpCounter.dll        <- entryAssembly
    SomeDependency.dll
```

## `plugin.json`

UTF-8 JSON (a BOM, `//` comments and trailing commas are tolerated), at most 64 KiB. Property
names are matched case-insensitively; **unknown properties are ignored** so newer manifests load
on older hosts. A property given more than once is an error.
See [`plugin.sample.json`](plugin.sample.json) for a complete example.

| Field | Required | Rules |
|---|---|---|
| `id` | yes | Lowercase reverse-DNS, ≥ 2 dot-separated segments of `[a-z0-9_-]` (each starting and ending with a letter/digit), ≤ 128 chars. Must be unique; it is also the install folder name. e.g. `com.acme.jumpcounter` |
| `name` | yes | Display name, ≤ 100 chars, no control or invisible formatting characters (bidi overrides, zero-width spaces/joiners). |
| `version` | yes | The plugin's own version, strict [SemVer 2.0.0](https://semver.org) (`1.2.0`, `2.0.0-beta.1`). |
| `author` | no | ≤ 200 chars; same character rules as `name`. |
| `description` | no | ≤ 2000 chars; newlines and tabs allowed, otherwise the same character rules as `name`. |
| `sdkVersion` | yes | The `EDNexus.Plugins.Abstractions` contract version built against, `"major.minor"` (currently `"2.0"`; plugins built against `"1.x"` are rejected: 2.0 removed `IJournalEvent.Deserialize<T>`). Compatible with any host SDK of the **same major** and an **equal or newer minor** (`PluginSdk.IsCompatible`). The manifest is a claim, not proof: the host also reads the version of the `EDNexus.Plugins.Abstractions` reference recorded in the entry assembly (the SDK assembly's version tracks the SDK's `major.minor`) and rejects a plugin built against a newer SDK than it declares. |
| `minAppVersion` | no | Minimum EDNexus app version, SemVer. |
| `entryAssembly` | yes | Path of the entry `.dll` relative to the plugin folder, `/`-separated (safe-path rules below). |
| `entryType` | yes | Full CLR type name implementing `IEDNexusPlugin`, e.g. `Acme.JumpCounter.JumpCounterPlugin` (nested types use `+`). |
| `capabilities` | no | Array drawn from `events`, `state`, `ui.dashboard`, `ui.overlay`, `storage`, `network` (exact, lowercase). Any other value rejects the manifest; duplicates are collapsed. Plugins that make network calls must declare `network`. A declaration is a request: the user decides what is granted ([Consent and capabilities](#consent-and-capabilities)). |

An invalid manifest is rejected with every reason listed (e.g. `'version' "1.0" is not valid
SemVer 2.0.0`). Wrong types, missing required fields, and malformed JSON never throw.

When several plugins are discovered, `PluginManifestParser.FindDuplicateIds` reports ids that
occur more than once (case-insensitively); none of the duplicates should be loaded.
`PluginCompatibility.Check` gates `sdkVersion` / `minAppVersion` against the running host.

## `.ednplugin` packages

A `.ednplugin` is a zip of the plugin folder. `plugin.json` must be at the archive root, or inside
a single top-level folder that wraps everything (what "compress folder" tools produce).
Installing validates the whole package, extracts it to a staging folder (`.staging-<guid>`), and
moves it to `<plugins root>/<id>/`; an already-installed id is rejected unless replacement is
requested. A replace first renames the old version to `.replaced-<id>.<guid>`; if the swap fails
it is moved back (or, if that also fails, the error names where it was preserved). The host calls
`PluginInstaller.RecoverInterrupted(root)` at startup to finish any replace cut short by a crash:
it restores a backup whose `<id>` folder is missing, deletes backups that were superseded, and
deletes stale `.staging-*` and `.extract-*` folders. (`PluginPackage.ExtractTo` writes into a
sibling `.extract-<guid>` folder and renames it onto its destination only when complete, so it
never claims or deletes a destination folder it did not create.) Folder names starting with `.` are never valid plugin ids, so
discovery ignores them.

A package is **rejected** if any entry:

- is absolute (`/x`, `C:\x`, `\\server\x`), contains `..` or `.` segments, empty segments, `:`,
  `<>"|?*`, control characters, invisible formatting characters (e.g. `evil` + U+202E + `lld.exe`,
  which displays as `evilexe.dll`), unpaired surrogates, or a backslash-normalised form of any of these
  (zip-slip);
- has a segment that is a Windows device name (`CON`, `nul.txt`, `COM1`…) or ends in a dot/space;
- is longer than 240 characters or nested deeper than 16 levels;
- duplicates another entry case-insensitively after Unicode NFC normalisation (so `café` spelled
  precomposed and decomposed collide, as they do on macOS), or nests under a path that is a file;
- is a symbolic link or encrypted;

or if the package exceeds the size limits (`PluginPackageLimits`; defaults: 64 MiB package,
2,000 entries, 128 MiB per file, 256 MiB total uncompressed, 100:1 compression ratio for files over
1 MiB). The entry count is read from the End Of Central Directory record before any entries are
parsed. During extraction, sizes are re-checked against the actual decompressed bytes and every
file's CRC-32 is verified. The manifest must be named exactly `plugin.json`, be valid, and its
`entryAssembly` must be in the package with the same casing.

## Discovery and loading

`PluginHost.LoadAll()` (issue #55) turns the plugins root into running plugins:

1. On a host's **first** pass, `PluginInstaller.RecoverInterrupted(root)` runs before anything
   else; its errors are surfaced in `PluginDiscoveryReport.Errors` and the result is kept on
   `PluginHost.Recovery`. It runs once per host (a later `UnloadAll()` + `LoadAll()` does not
   repeat it), and no install may run on the same root while that first pass is in progress.
2. Every folder directly under the root whose name does not start with `.` is a plugin candidate
   (`.staging-*`, `.replaced-*`, `.extract-*` and other hidden folders are skipped). Folders are
   processed in ordinal name order.
3. A candidate is **Rejected** (no plugin code runs) when `plugin.json` is missing or invalid, the
   folder name is not exactly the manifest `id`, or its `id` is also declared by another folder
   (every claimant is rejected, since there is no safe way to pick one).
4. It is **Incompatible** when `PluginCompatibility.Check` fails (SDK major/minor or `minAppVersion`),
   or when the entry assembly's own `EDNexus.Plugins.Abstractions` reference (read from its metadata
   without loading it) is for a newer minor or a different major than the host provides.
5. `entryAssembly` is resolved inside the plugin folder with the same (lexical) path rules as
   extraction and must exist. The host reads the assembly once and hashes it and the folder's other
   files (a folder containing a link, or over the size limits, is **Rejected**). The host's **consent callback** is then asked, with those hashes, whether the plugin may
   run, and with which capabilities; if not, the plugin is **Denied** and none of its code is
   loaded (see [Consent and capabilities](#consent-and-capabilities)). The plugin gets its own **collectible `AssemblyLoadContext`**: its
   private dependencies resolve from its folder via `AssemblyDependencyResolver` (its `.deps.json`,
   or every assembly beside the entry assembly when there is none), so two plugins can ship
   different versions of the same library. `EDNexus.Plugins.Abstractions` always binds to the
   **host's** copy, even if the plugin bundles one, so `IEDNexusPlugin` is the same type in host and
   plugin. Anything else the plugin does not ship resolves from the host process (the default
   context): the framework (BCL). **The host's own assemblies (`EDNexus.*` such as `EDNexus.Core`,
   and `EliteDangerous.*`) do not**: a plugin that references one without shipping its own copy
   fails to load with a `FileLoadException` naming it, instead of silently reaching into host
   internals. (A plugin may ship its own library with such a name; it then gets that isolated copy.)
6. `entryType` must exist, implement the host's `IEDNexusPlugin`, be concrete and non-generic, and
   have a public parameterless constructor (otherwise **Rejected**, without constructing it).
7. The host constructs it and calls `Initialize(context)` with a context from its factory (which is
   handed the capabilities that were granted). If
   loading, the constructor, the factory or `Initialize` throws, the plugin is **Failed**: the
   reason records the exception type and message (sanitised, and read defensively, since a plugin
   exception's `Message` can itself throw; the exception object is not kept, since it could pin the
   plugin in memory). After a failed `Initialize` the host calls `Shutdown()` (best effort); in
   every case it disposes the context if it is `IDisposable` and unloads the load context. Other
   plugins are unaffected.

`PluginHost.Unload(id)` / `UnloadAll()` call `Shutdown()` (a throw is reported, not propagated),
dispose the context (which stops event delivery), wait up to `PluginHost.UnloadTimeout` (default
5 s) for event handlers already running to return (when the context is an
`IPluginSessionControl`, which a bridge session is), drop every reference to the plugin and unload
its load context, which the GC then collects. Every plugin is unloaded even if another
misbehaves. While loaded, a plugin's dependency assemblies are locked on Windows; they are
released after unload and collection.

**The entry assembly is loaded from memory.** The host reads the `entryAssembly` file once (at most
64 MiB; larger is rejected), inspects those bytes and loads those same bytes, so the file is not
held open and a rebuilt plugin can be copied over a running one and picked up by a reload.
Consequences for plugin authors: `Assembly.GetExecutingAssembly().Location` is an empty string
(do not locate sibling files through it; use `context.Storage` for data), and there is no PDB, so stack traces carry no line
numbers. Dependency DLLs beside the entry assembly are still loaded by path.

A handler that is still running when the wait expires is reported in the unload result's `Errors`
as a **stuck handler** and `PluginUnloadResult.Stuck` is `true`: the plugin's code is still on a
thread's stack, so the unload is not clean and its files stay locked until that handler returns.
The app should say so and refuse to replace or delete the plugin's folder while `Stuck`. Handlers
run on the plugin's own delivery thread, which stops only after `Shutdown` returns, so a handler
can be running **concurrently with `Shutdown`**; plugins are told so in `IEDNexusPlugin.Shutdown`.

If the host is disposed during a load pass (app shutdown), the pass stops: plugins not yet
constructed are not constructed, and the report says so in `Errors`.

**Limits.** A load context isolates dependency *versions*; it is **not a security boundary** —
plugin code runs in-process with the host's full trust (the consent UI is #61; the trust and signing model is #66). "Failed" and
"unloaded" mean the host has released everything it holds; they do not guarantee plugin code has
stopped. Threads or timers the plugin started, or references held outside the host (for example
a handler the plugin added to a static event such as `AppDomain.ProcessExit`), keep running and
keep the load context alive. A stack overflow or `Environment.FailFast` in plugin code ends the
process, and a constructor, `Initialize` or `Shutdown` that never returns blocks the load or unload
pass that called it (timeouts belong to the threading contract, #62). `LoadAll()` throws
`InvalidOperationException` rather than waiting if called while another load pass is in progress
(from another thread, or from plugin code or the context factory during that load pass).

## Consent and capabilities

**The host never decides on its own that a plugin may run.** `PluginHost`'s constructor takes a
required `consent` callback (`Func<PluginConsentRequest, IReadOnlyCollection<string>?>`; the request
carries the manifest and SHA-256 hashes of the entry assembly and the other files, see
[Consent in the desktop app](#consent-in-the-desktop-app)), and there is no allow-all default. It is
called once per plugin that passed the static checks, before any plugin code loads:

- return `null` to deny: the plugin is `PluginLoadStatus.Denied` and nothing of it runs;
- return a collection to allow it with exactly those capabilities (an empty collection is allowed
  to run with nothing; names the manifest does not declare are dropped);
- throwing denies it (`Failed`).

The granted set is passed to the context factory, which must pass it to `PluginBridge.Attach`
(whose `grantedCapabilities` parameter is likewise required and has no "everything declared"
default). A capability that is not granted makes the matching part of the context refuse every
call with `UnauthorizedAccessException`, rather than silently doing nothing.

What the consent screen has to say, per capability:

| Capability | What it gives the plugin |
|---|---|
| `events` | Every journal event, as it happens or is replayed, **including the commander's name and credit balance** (`LoadGame`, `Commander`, `Statistics`) and everything else the game logs (location, ship, cargo, materials, missions). Data-equivalent to `state`. |
| `state` | The commander's **in-game name and credit balance**, current system, body, station, ship, cargo and materials, as of the last completed event. There is no narrower grant for the name or the balance. |
| `network` | Not enforceable in-process. It is a declaration that the plugin phones home; **with `events` or `state` it can send all of the above off the machine.** Say so when the two are requested together. |
| `storage` | Lets the plugin save its own key/value data in `<plugins root>/<id>/data/` (see below). Live in the app and the CLI harness: `PluginRuntime` hands the bridge the same plugins root the host scans (`PluginBridgeOptions.PluginsRoot`). A bridge built without a root still refuses every storage call. |
| `ui.dashboard`, `ui.overlay` | Reserved: the bridge has no UI contribution points yet, so they currently refuse every call (`PluginBridgeSession.Ui`). |

**Storage contract** (`IPluginStorage`). Keys are 1-100 characters from `A-Z a-z 0-9 . _ -` and are
**case-insensitive** (`Foo` and `foo` are the same key). Values are strings of valid Unicode, at most
1 MiB of UTF-8 each (a lone surrogate is rejected, not substituted). Per plugin: at most 256 keys and
8 MiB in total. Failures: an invalid key or value throws `ArgumentException`; exceeding the quota or any
IO error throws `IOException`; an unset key reads as `null`, but a value that exists and cannot be read
throws (`IOException`/`UnauthorizedAccessException`) instead of reading as unset. `ReadJson` returns
`default` for missing or malformed JSON but lets those IO errors through. Storage stops working once
the plugin is unloaded, and a plugin update that replaces the plugin folder does not yet preserve
`data/`.

**Developer mode.** Developer mode feeds the engine fabricated events. A plugin that **declares**
`network` (whether or not it is granted: it cannot be enforced) never sees them: its events are
withheld and its state reads as an unknown commander. Every other plugin sees them, with
`IJournalEvent.IsSimulated` set. The state flag is sticky for the life of a bridge: once a snapshot
was built in developer mode the bridge keeps treating the commander as simulated, because the
fabricated balance, cargo and materials are still in `CommanderState`. **The app must therefore
rebuild its engine host (bus and `CommanderState`) and the bridge when it leaves developer mode**,
and should dispose the developer-mode sources before it turns the `IsSimulated` predicate off
(each event is stamped from the predicate as it is queued, so one published concurrently with the
flip can be stamped live).

**Event payloads.** `IJournalEvent` exposes the payload as a detached `JsonElement` (`Payload`),
and deliberately has no `Deserialize<T>`. Binding a plugin-defined type with reflection-based
`System.Text.Json` caches it in process-wide state that pins the plugin's load context, so
the plugin could never be unloaded or updated without restarting. Plugins should navigate the
element, or use a source-generated `JsonSerializerContext` defined in the plugin, which does not
use that cache.

**Delivery limits.** Each plugin has its own queue, bounded by event count
(`PluginBridgeOptions.QueueCapacity`, 8192) *and* by approximate memory
(`PluginBridgeOptions.QueueByteCapacity`, 16 MiB); when either is exceeded the oldest events are
dropped and counted (`PluginBridgeSession.DroppedEventCount`). `PluginHandlerError` (reported for
every throwing handler) carries the exception's type name and message as text only, never the
exception object, so a log sink that keeps it cannot pin the plugin in memory.

## Wiring the host

```csharp
var bridge = new PluginBridge(engine.Bus, engine.State,
    new PluginBridgeOptions { IsSimulated = () => devMode, HandlerError = log.Plugin });

using var host = new PluginHost(PluginPaths.Resolve(), appVersion,
    consent: PluginConsent.FromGrants(userGrantStore),            // null = not allowed to run
    contextFactory: (manifest, granted) =>
        new AppPluginContext(manifest, bridge.Attach(manifest, granted)));

// AppPluginContext : IPluginContext, IDisposable, IPluginSessionControl
//   State/Events/Storage/Ui come from the session; Dispose() disposes the session;
//   WaitForExit(t) returns session.WaitForExit(t).
```

Implement `IPluginSessionControl` on the context (a bridge session already does): without it the
host cannot tell that a handler is still running at unload.

### Use `PluginRuntime` rather than wiring by hand

The snippet above is what `PluginRuntime.Create` does for you, and the app and the CLI harness both
use it. It is the single owner of the plugins root (the same string goes to `PluginHost` and to
`PluginBridgeOptions.PluginsRoot`, which is what makes storage work), builds the host, the bridge and
a `PluginSupervisor` together, binds `PluginBridgeOptions.HandlerError` to
`PluginSupervisor.ReportHandlerError`, and builds each plugin's context (`PluginContext`) from a
bridge session. A bridge is bound to one engine's bus, so build a new runtime when the engine is
rebuilt. `Start`, `Uninstalled`, `Dispose` and the supervisor's lifecycle calls run plugin code and
write `settings.json`: call them off the UI thread.

**Which load a fault belongs to.** Every `PluginBridge.Attach` takes the next session number
(`PluginBridgeSession.Generation`), and every `PluginHandlerError` carries the number of the session
it came from. The runtime tells the supervisor each new session's number
(`PluginSupervisor.RecordAttach`), so a copy of a plugin that was reloaded but is still running (stuck
in a handler) cannot have its late faults charged to, and quarantine, the copy that replaced it.

**Uninstalling.** There is no uninstall command yet; whoever removes a plugin folder calls
`PluginRuntime.Uninstalled(id)`, which unloads the plugin, drops the supervisor's entry for it
(`PluginSupervisor.Forget`), clears its persisted disable flag and revokes its consent.

### Consent in the desktop app

The app denies by default. The consent callback receives a `PluginConsentRequest`: the parsed
manifest plus two content hashes. `PluginConsent.FromGrants` allows a plugin only if the grant store
holds a grant for its id whose **three** hashes all equal the request's, and then only with the
capabilities granted:

| Hash | What it covers |
|---|---|
| `ManifestHash` | `PluginManifestFingerprint`: SHA-256 over every manifest field (capabilities order-insensitively). A manifest is public, so this alone proves nothing about the code. |
| `EntryHash` | Plain SHA-256 (lowercase hex; reproducible with `sha256sum`) of the exact bytes of the entry assembly. The host reads the assembly into memory once, hashes that `byte[]` and loads those same bytes (`LoadFromStream`), so there is **no time-of-check/time-of-use gap** on the entry assembly: what was approved is what runs. |
| `FilesHash` | `PluginContentHash.OfFolder`: SHA-256 over every *other* file in the plugin folder (dependency DLLs, native libraries, `.deps.json`, resources). Format: tag `EDNexus.PluginFiles.v1`, file count, then per file in ordinal order of its `/`-separated relative path: length-prefixed UTF-8 path, 64-bit content length, SHA-256 of the content (integers little-endian, files streamed). Excluded: the entry assembly and `plugin.json` (bound by the two hashes above) and the top-level `data/` folder (the plugin's own storage, which changes at run time; a `data` folder nested deeper is hashed like anything else). |

**Fail closed.** No grant, a missing or empty hash of any kind (so every grant written before the
content hashes existed counts as no grant and must be approved again), any mismatch, or a store that
throws, all deny. A grant for one id never applies to another.

**Links.** The folder hash never reads through a symbolic link, junction or other reparse point: a
plugin whose folder contains one (outside `data/`, which is not entered) is `Rejected` before
consent is even asked, because the loader would follow the link to content that no hash of the
folder can vouch for. The package installer already refuses links. **Bounds.** The folder hash
refuses (`Rejected`) a folder over the `PluginPackageLimits` defaults (2,000 files and folders, 128 MiB
per file, 256 MiB in total) or nested deeper than `PluginPathRules.MaxDepth`, so anything the
installer accepts can be hashed.

**What this does not protect.** The entry assembly's hash is race-free, but a plugin's *other*
files are still loaded **by path**, lazily, after the check (`AssemblyDependencyResolver`). `FilesHash`
therefore protects against a package being swapped or updated between approvals (uninstall by hand,
unzip a trojanised package with a copy of the manifest; replace a dependency under an unchanged
manifest) but not against a local actor with write access to the plugin folder who races the load.
That actor can already run code as the user, so this crosses no trust boundary. Nothing here defends
against a plugin that is approved and then misbehaves: consent is not a sandbox.

Grants are persisted in `settings.json` under `Plugins.Grants`:
`{ "<id>": { "ManifestHash": "...", "EntryHash": "...", "FilesHash": "...", "Capabilities": [...] } }`
through `AppSettingsPluginGrantStore`. A missing or `null` hash reads as empty (denied). The management
UI (#61) records consent with `IPluginGrantStore.Grant(request, capabilities)` using the
`PluginConsentRequest` it showed (`PluginConsentRequest.TryRead(folder, ...)` builds the same request
the host would; `Grant(folder, capabilities)` does both steps). There is deliberately no overload
that takes only a manifest. Until a UI does, no plugin runs in the desktop app (the CLI harness is
different: it grants everything declared, and says so). The trust and signing model is #66.

Consent is applied in one place, `PluginHost`, on every load, **including a reload**: the host
re-reads `plugin.json`, the entry assembly and the folder from disk, then asks the consent callback
about them before any plugin code loads. A reload therefore never reuses an approval for different
files: change the version, a capability, the DLL or any other file outside `data/` and the plugin
comes back `Denied` until it is approved again. This includes an update through `PluginInstaller`
(same id, new content): the installer itself knows nothing about grants, and this check is what makes
the update need a fresh approval.

**Stale grants.** Nothing in the app calls `PluginRuntime.Uninstalled` when a folder is removed by hand,
so after each load pass the app (`PluginChoicePruner`) forgets the grants and disable flags of ids
that have no folder under the plugins root. It does nothing when the root is missing, cannot be
listed, or holds no plugin folder at all (all indistinguishable from an unplugged drive or unmounted
share); a stale entry is harmless (it can no longer match different code) and is removed on a later
run.

In the app, plugins load once the engine has finished replaying the journal, on a background thread,
so they do not receive the startup replay (read `context.State` for what the replay established);
they receive every event after it. A failing plugin never delays or stops the app.
