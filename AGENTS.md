# Agent Instructions

## Build and test

```bash
dotnet build WasteRegulatorCaseManagement.sln
dotnet test
dotnet format style --verify-no-changes
```

Tests run against an ephemeral MongoDB replica set and need no Docker. CI runs
the format check and the tests on every pull request.

## Where to start

- [README.md](README.md): what the service does, how to run it, configuration.
- [docs/cases.md](docs/cases.md): the case engine, intake, actions, callback
  and timeline.
- [docs/cdp-deployment.md](docs/cdp-deployment.md): environment variables,
  secrets and the signed-header contract.
- [docs/adr/](docs/adr/): architecture decisions.

## Non-interactive shell commands

Shell commands like `cp`, `mv` and `rm` may be aliased to interactive mode on
some systems, which hangs an agent waiting for y/n input. Use `cp -f`, `mv -f`
and `rm -f` (`-rf` for directories), `-o BatchMode=yes` for `ssh` and `scp`,
and `-y` for `apt-get`.
