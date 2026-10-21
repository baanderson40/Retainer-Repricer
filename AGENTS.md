# Retainer Repricer

## Workspace and commands

- Build using a prepared Windows/Dalamud environment. The required local build artifact is `D:\Coding\C#\Retainer Repricer\RetainerRepricer\bin\x64\Debug\RetainerRepricer.dll`. Do not run test, lint, format, publish, or release commands without an explicitly prepared environment.
- Dependency setup: `git submodule update --init --recursive`. The only nested dependency is `external/AutoRetainerAPI`; do not modify it except for an explicit submodule update.
- Local build (from the repository root in Windows): `dotnet build RetainerRepricer/RetainerRepricer.csproj --configuration Debug -p:Platform=x64 -p:OutputPath="D:\Coding\C#\Retainer Repricer\RetainerRepricer\bin\x64\Debug" -p:AppendTargetFrameworkToOutputPath=false -p:AppendRuntimeIdentifierToOutputPath=false`. Verify the DLL exists at the required path after building; do not substitute a Release output.
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
