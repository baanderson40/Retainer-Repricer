# AGENTS.md - Retainer Repricer Development Guide

## Project Overview

Retainer Repricer is a Dalamud plugin for Final Fantasy XIV that automates retainer market activity (repricing and selling). It's a C# .NET 10 project using the Dalamud.NET.Sdk.

## Build Commands

```bash
# Build the project
dotnet build RetainerRepricer/RetainerRepricer.csproj

# Build in Release mode
dotnet build RetainerRepricer/RetainerRepricer.csproj -c Release

# Clean and rebuild
dotnet clean RetainerRepricer/RetainerRepricer.csproj && dotnet build RetainerRepricer/RetainerRepricer.csproj
```

There is **no test framework** in this project. Testing is done manually in-game.

## Code Style Guidelines

### Formatting (enforced by .editorconfig)

- **Indentation**: 4 spaces (no tabs)
- **Line endings**: LF
- **Charset**: UTF-8
- **Trailing newline**: Required
- **Braces**: Allman style - open brace on new line
- **Modifier order**: `public, private, protected, internal, new, abstract, virtual, sealed, override, static, readonly, extern, unsafe, volatile, async`

### Naming Conventions

| Symbol Type | Convention | Example |
|-------------|------------|---------|
| Private fields | `_camelCase` | `_runPhase`, `_lastActionUtc` |
| Private constants | PascalCase | `CommandName`, `UndercutAmount` |
| Public members | PascalCase | `Configuration`, `IsRunning` |
| Events | PascalCase with On prefix | `OnClick`, `OnOpen` |
| Methods | PascalCase | `GetVisibleUnitBase`, `SaveConfig` |
| Properties | PascalCase | `PluginEnabled`, `RetainersEnabled` |

### Language Features

- **Nullable**: Enabled (`<Nullable>enable</Nullable>`)
- **Unsafe code**: Allowed (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`)
- **Prefer `var`**: Use `var` everywhere except when type is not apparent

```csharp
// Good
var addon = _gui.GetAddonByName("RetainerList", 1);
var names = _config.RetainersEnabled.Keys.OrderBy(x => x).ToList();

// Avoid
IDalamudPluginInterface addon = _gui.GetAddonByName("RetainerList", 1);
```

### Class Design

- **Seal classes by default**: Use `public sealed class`
- **Use region blocks**: Organize code with `#region` for logical groupings
- **Dependency injection**: Use `[PluginService]` attributes for Dalamud services

```csharp
public unsafe sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    #region Constants
    private const double ActionIntervalSeconds = 0.15;
    #endregion

    #region Run state
    private RunPhase _runPhase = RunPhase.Idle;
    #endregion
}
```

### Error Handling

- **Null checks**: Use nullable reference types and null-forgiving operator `!` where safe
- **Return null for not found**: Rather than throwing, return null from finder methods
- **Logging**: Use `Log` service for debugging - prefix log messages with context

```csharp
private AtkUnitBase* GetVisibleUnitBase(string addonName, int index = 1)
{
    var addon = _gui.GetAddonByName(addonName, index);
    if (addon.IsNull) return null;

    var unit = (AtkUnitBase*)addon.Address;
    if (unit == null || !unit->IsVisible) return null;

    return unit;
}
```

### UI Code (ImGui)

- Use `ImGui.TextUnformatted()` for static strings (faster)
- Use `##` for unique control IDs
- Set `SizeConstraints` for windows
- Use tab-based layouts in config windows

```csharp
public ConfigWindow(Plugin plugin)
    : base("Retainer Repricer Configuration##Config")
{
    Flags = ImGuiWindowFlags.NoCollapse;
    SizeConstraints = new WindowSizeConstraints
    {
        MinimumSize = new(360, 180),
        MaximumSize = new(800, 600),
    };
}
```

### Unsafe Code Patterns

This project frequently uses unsafe pointers for game memory access. Follow these patterns:

```csharp
// Pointer access pattern
private unsafe void Example(AtkUnitBase* unit)
{
    if (unit == null || !unit->IsVisible) return;
    var nodes = unit->UldManager.NodeList;
    // ...
}
```

### Import Organization

Order imports alphabetically within groups:

1. System namespaces
2. Dalamud namespaces
3. ECommons namespaces
4. FFXIVClientStructs namespaces
5. Project-local namespaces

```csharp
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Windows;
using System;
using System.Collections.Generic;
```

### Key Libraries

- **ECommons**: Utility library for Dalamud plugins (automation, config, UI helpers)
- **FFXIVClientStructs**: Interop bindings for FFXIV game memory
- **Dalamud**: Plugin framework

### Debugging Tips

- Use `Log.Info()`, `Log.Warning()`, `Log.Error()` for logging
- The `UiReader.DumpAddonNodeList()` method helps inspect addon structures
- UI state changes are visible via `IsVisible` checks on `AtkUnitBase`
