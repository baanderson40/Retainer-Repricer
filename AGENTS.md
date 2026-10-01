# Retainer Repricer

## Workspace and commands

- This workspace is editor-only: do not run build, test, lint, format, publish, or release commands here without an explicitly prepared environment. Current target is Windows/Dalamud.
- Dependency setup: `git submodule update --init --recursive`. The only nested dependency is `external/AutoRetainerAPI`; do not modify it except for an explicit submodule update.
- Reference builds: `dotnet build RetainerRepricer.sln --configuration Release -p:Platform=x64` or `dotnet build RetainerRepricer/RetainerRepricer.csproj --configuration Release --arch x64`.
- No test project exists; `dotnet test` and test filters are invalid. No repository lint or formatter command is configured.

The plugin uses `Dalamud.NET.Sdk/15.0.0`, targets `net10.0-windows7.0`, enables unsafe code, and depends on NuGet ECommons `3.2.1.21`. Linux can only compile when `DALAMUD_HOME` provides the required Dalamud assemblies; it cannot run the plugin.

## Architecture

- `RetainerRepricer/Plugin.cs` owns composition and lifecycle; partials under `Plugin/` split framework ticks, repricing/listing phases, AutoRetainer handoff, Universalis gating, UI navigation, commands, and debug behavior.
- `Services/` owns Universalis HTTP/cache, sorting, inventory pruning, and logging. `Ui/` is the unsafe addon/node boundary; `Windows/` owns ImGui UI.
- `Configuration.cs` is persisted schema; call `Configuration.Save()` for configuration mutations.

Keep framework-tick automation deterministic, guarded, and idempotent. Preserve HQ/NQ separation, readiness checks, bounded/rate-limited logging, reverse-order UI cleanup, and intentional market-board jitter. Use UTC timestamps and existing timing constants; never add sleeps or busy waits. Validate native UI changes in-game.

## Release and files

- Preserve `RetainerRepricer/packages.lock.json` and manually keep the project version synchronized with `repo.json`.
- CI builds the project on Windows and packages `RetainerRepricer/bin/Release/win-x64/*`; it does not use the old `dotnet publish ... /p:DalamudPlugin=true` command.
- `bin/release.sh` and `bin/post-release.sh` can update branches, manifests, commit, or push. Maintainer-only; never run casually.
- Do not edit `bin/`, `obj/`, `.vs/`, `.csproj.user`, ignored publish profiles, or the AutoRetainerAPI submodule.
