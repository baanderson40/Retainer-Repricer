using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace RetainerRepricer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    [NonSerialized]
    private IDalamudPluginInterface? _pi;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;

    // --- settings ---
    public bool OverlayEnabled { get; set; } = true;

    // Persist user intent for the overlay to show when RetainerList is open.
    public bool OverlayWantsOpen { get; set; } = true;

    // Anchor offset tuning
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;

    public void Save()
    {
        if (_pi == null) return;
        _pi.SavePluginConfig(this);
    }
}
