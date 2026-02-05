using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace RetainerRepricer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    // =========================================================
    // Dalamud config versioning
    // =========================================================
    public int Version { get; set; } = 1;

    [NonSerialized]
    private IDalamudPluginInterface? _pi;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;

    // =========================================================
    // Retainer enable/disable flags
    // =========================================================
    public Dictionary<string, bool> RetainersEnabled { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRetainerEnabled(string name)
        => RetainersEnabled.TryGetValue(name, out var enabled) ? enabled : true; // default enabled

    public void SetRetainerEnabled(string name, bool enabled)
        => RetainersEnabled[name] = enabled;

    // =========================================================
    // Overlay settings
    // =========================================================
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayWantsOpen { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;

    // =========================================================
    // Save
    // =========================================================
    public void Save() => _pi?.SavePluginConfig(this);
}
