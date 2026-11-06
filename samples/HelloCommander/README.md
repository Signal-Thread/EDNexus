# Hello Commander (sample plugin)

The smallest useful EDNexus plugin, and the copy-paste starting point for your own. It counts
hyperspace jumps and, for each `FSDJump`, logs

```
CMDR Jameson - 3 jumps, now in Wolf 359 (lifetime 3)
```

It exercises, end to end:

| SDK feature | Where |
|---|---|
| Entry point (`IEDNexusPlugin`, `Initialize` / `Shutdown`) | [`HelloCommanderPlugin.cs`](HelloCommanderPlugin.cs) |
| Event subscription (`context.Events.On("FSDJump", ...)`, removed in `Shutdown`) | `Initialize`, `Shutdown` |
| Reading a journal event (`IJournalEvent.GetString`, `IsHistorical`) | `OnJump` |
| Read-only commander state (`context.State`, `Snapshot()`) | `OnJump` |
| Logging (`context.Log`) | everywhere |
| Persistence (`context.Storage`), degrading gracefully when it is not granted | `LoadLifetimeJumps`, `SaveLifetimeJumps` |
| The manifest, with every field explained | [`plugin.json`](plugin.json) |
| A project that references only the SDK and assembles the folder the host expects | [`HelloCommander.csproj`](HelloCommander.csproj) |

The developer guide that explains all of this is [`docs/PLUGINS.md`](../../docs/PLUGINS.md).

## What is not here (yet)

The issue that asked for this sample also wanted a **dashboard widget**. That is not possible
today, and the sample does not pretend otherwise:

- There is no dashboard or overlay contribution contract (`IDashboardWidget`, `IOverlayPanel`, ...)
  and no `EDNexus.Plugins.Abstractions.Ui` assembly. `IUiRegistry.Register(id, object)` is an
  opaque placeholder, and the real host's implementation refuses every call. (The CLI harness is
  more lenient: it gives a plugin granted `ui.dashboard` or `ui.overlay` a counting stub on which
  `Register` succeeds, so do not take a clean `--plugins-strict` run as proof that such a call works in
  the app.)
- So `plugin.json` does **not** declare `ui.dashboard`. The capability name is recognised, but
  declaring it today only adds a line to the (future) consent prompt for something that does nothing.
- The desktop app loads plugins but is deny-all: it runs a plugin only if `settings.json` holds a consent
  grant matching its manifest and files, and nothing records grants until the management UI (#61)
  exists. So this plugin does **not** run inside the app today. Run it with the CLI harness (below)
  and in the test suite.

When the UI contracts land, this sample is where a widget will be added.

## Build

Requires the .NET 10 SDK. From the repository root:

```sh
dotnet build samples/HelloCommander/HelloCommander.csproj
```

(It is also part of `dotnet build EDNexus.slnx`.) A post-build step assembles the folder the host
expects, **named after the plugin id** and containing the manifest next to the entry assembly:

```
samples/HelloCommander/out/com.ednexus.samples.hellocommander/
    plugin.json
    HelloCommander.dll
```

`out/` is git-ignored. The `EDNexus.Plugins.Abstractions` assembly is deliberately **not** copied
in: the host always binds a plugin to its own copy of the SDK.

## Run it

### With the CLI harness (works today)

The harness replays a journal through the real plugin host and prints a per-plugin summary. This
repository ships a small, hand-written (synthetic, not a real journal) sample in
[`sample-journal/`](sample-journal) with a `LoadGame` and three `FSDJump` events:

```sh
dotnet run --project src/EDNexus.Cli -- --once \
  --dir samples/HelloCommander/sample-journal \
  --plugin samples/HelloCommander/out/com.ednexus.samples.hellocommander
```

Expected (the `[plugin info]` lines go to stderr, so they can appear before or after the
summary):

```
  [plugin info] Hello Commander loaded (lifetime total so far: 0).
  [plugin info] CMDR Jameson - 1 jumps, now in Alpha Centauri (lifetime 0)
  [plugin info] CMDR Jameson - 2 jumps, now in Barnard's Star (lifetime 0)
  [plugin info] CMDR Jameson - 3 jumps, now in Wolf 359 (lifetime 0)
  ...
  com.ednexus.samples.hellocommander 1.0.0  [Loaded]
      caps    : events, state, storage
      events  : 3 seen, 0 dropped
      errors  : 0
  [plugin info] Hello Commander shutting down after 3 jump(s).
```

The lifetime stays 0 because a journal replay is *historical* and the plugin deliberately does not
save replayed jumps. Omit `--dir` to replay your own latest journal instead (the harness grants every
capability the plugin declares, with no consent prompt, and runs the plugin against your real data).
Add `--plugins-strict` to make the run exit `1` on any load failure or handler error.

### Dropping it into a plugins folder

The host scans a *plugins root* in which each plugin is a folder named after its id. Copy the output
folder there:

| OS | Plugins root |
|---|---|
| Windows | `%LOCALAPPDATA%\EDNexus\plugins\` |
| Linux | `~/.local/share/EDNexus/plugins/` |
| macOS | Unverified: the code uses .NET's `LocalApplicationData` and has not been checked on a Mac (tracked in #259); set `EDNEXUS_PLUGINS_DIR` instead |

```sh
mkdir -p ~/.local/share/EDNexus/plugins
cp -r samples/HelloCommander/out/com.ednexus.samples.hellocommander ~/.local/share/EDNexus/plugins/
dotnet run --project src/EDNexus.Cli -- --once \
  --dir samples/HelloCommander/sample-journal --plugins
```

(`--dir` points the harness at the synthetic sample journal; without it, and with no real Elite
Dangerous journal on the machine, the run exits `1` with "Could not locate the Elite Dangerous journal
folder".) With `--plugins`, whatever the plugin saves lands in
`<plugins folder>/com.ednexus.samples.hellocommander/data` and persists between runs (this sample saves
only live jumps, so a journal replay writes nothing); with `--plugin <folder>` (above) it lands in a
scratch copy that is deleted on exit, so nothing persists.

Set `EDNEXUS_PLUGINS_DIR` to an absolute path to use a different root while developing.

**The desktop app will not run this copy.** The app uses the same plugins folder, but it is deny-all:
with no consent grant for `com.ednexus.samples.hellocommander` in `settings.json` the plugin is
reported `Denied` (in the app's trace log) and none of its code is loaded, and nothing records a grant
until the management UI (#61) exists. Any edit to its `plugin.json`, DLL or other files would revoke a
grant, too. Only the CLI harness, which grants every declared capability, runs it today.

### Tests

`tests/EDNexus.Samples.Tests` installs the built folder into a temporary plugins root, loads it
through `PluginHost` and `PluginBridge`, publishes real journal events and asserts on what the plugin
logs and saves:

```sh
dotnet test tests/EDNexus.Samples.Tests
```

## Using it as a template

Copy `HelloCommander.csproj`, `HelloCommanderPlugin.cs` and `plugin.json` into a new folder, then:

1. Change `id` in `plugin.json` **and** `<PluginId>` in the `.csproj` (they must match), plus the
   `entryAssembly` / `entryType` if you rename the assembly or class.
2. Fix the `ProjectReference` path to the SDK, or switch to a `<Reference>` to a built
   `EDNexus.Plugins.Abstractions.dll` if you are building outside this repository (see the comment in
   the project file and [`docs/PLUGINS.md`](../../docs/PLUGINS.md)).
3. Declare only the capabilities you use.
