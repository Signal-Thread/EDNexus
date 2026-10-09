# Contributing to EDNexus

## Getting started

```sh
dotnet build EDNexus.slnx
dotnet test EDNexus.slnx                    # unit tests (xUnit) — keep them green
dotnet run --project src/EDNexus.App        # the desktop app
dotnet run --project src/EDNexus.Cli -- --once   # headless validation
```

Requires the **.NET 10 SDK**. The journal folder is auto-detected; override with
`EDNEXUS_JOURNAL_DIR` if needed.

## Workflow

1. Pick or open an issue. Roadmap work lives under the [Epic](https://github.com/Signal-Thread/EDNexus/issues/7) and per-phase issues.
2. Branch from `main` (e.g. `phase-3-colonisation`).
3. Make the change, following the conventions in [`AGENTS.md`](AGENTS.md).
4. Validate against real journal data, and run `dotnet test EDNexus.slnx`. Warnings are build
   errors (see `Directory.Build.props`), so the build must be warning-free.
5. Open a PR (the template is applied automatically), reference `Closes #<issue>`.

## Conventions

The authoritative list is in [`AGENTS.md`](AGENTS.md). The load-bearing ones:

- Only `StateTracker` mutates `CommanderState`; everything else reads it.
- Parse journal events defensively — never assume a field exists.
- OS-specific code goes behind an interface with a graceful fallback.
- Never commit journal files, secrets, or build output.

## Handing work to an agent

Tasks labelled `agent-ready` are scoped for AI agents. Use the **🤖 Agent task** issue form to
write new ones, and the agent definitions in [`.claude/agents/`](.claude/agents/) to run them.
