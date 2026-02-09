using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerRepricer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    // =========================================================
    // Dalamud config versioning
    // =========================================================
    // PASTE: replace your Version line with this
    public int Version { get; set; } = 2;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
        => _pluginInterface = pi;

    // =========================================================
    // Master enable/disable
    // =========================================================
    // PASTE: add this block right after Initialize(...)
    // When false: no running, no context menu injection, no overlay drawing.
    // Config window still works.
    public bool PluginEnabled { get; set; } = true;

    // =========================================================
    // Retainer enable/disable flags
    // =========================================================
    public Dictionary<string, bool> RetainersEnabled { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRetainerEnabled(string name)
        => RetainersEnabled.TryGetValue(name, out var enabled) ? enabled : true;

    public void SetRetainerEnabled(string name, bool enabled)
        => RetainersEnabled[name] = enabled;

    // =========================================================
    // Overlay settings (visual only)
    // =========================================================
    // PASTE: replace your existing Overlay settings block with this
    public bool OverlayEnabled { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;

    // =========================================================
    // Sell list (v1)
    // =========================================================
    [Serializable]
    public sealed class SellListEntry
    {
        public uint ItemId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public Dictionary<uint, SellListEntry> SellList { get; set; } = new();

    public bool HasSellItem(uint itemId) => SellList.ContainsKey(itemId);

    public bool AddSellItem(uint itemId, string name)
    {
        if (itemId == 0) return false;
        if (SellList.ContainsKey(itemId)) return false;

        var n = (name ?? string.Empty).Trim();

        SellList[itemId] = new SellListEntry
        {
            ItemId = itemId,
            Name = n,
        };
        return true;
    }

    public bool RemoveSellItem(uint itemId) => SellList.Remove(itemId);

    public IEnumerable<SellListEntry> GetSellListSorted()
        => SellList.Values
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId);

    public void ClearSellList() => SellList.Clear();

    // =========================================================
    // Save
    // =========================================================
    public void Save()
        => _pluginInterface?.SavePluginConfig(this);
}
