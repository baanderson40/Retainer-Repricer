using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerRepricer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    #region Dalamud config versioning

    public int Version { get; set; } = 2;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
        => _pluginInterface = pi;

    public void Save()
        => _pluginInterface?.SavePluginConfig(this);

    #endregion

    #region Global toggles

    // Master enable switch. When off: no runs, no context menu injection, no overlays.
    // Config UI stays usable so it’s easy to turn back on.
    public bool PluginEnabled { get; set; } = true;

    #endregion

    #region Run behavior

    // If enabled, the plugin will close the RetainerList window when a full run finishes.
    public bool CloseRetainerListAddon { get; set; } = true;

    #endregion

    #region Retainer enable/disable

    public Dictionary<string, bool> RetainersEnabled { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRetainerEnabled(string name)
        => RetainersEnabled.TryGetValue(name, out var enabled) ? enabled : true;

    public void SetRetainerEnabled(string name, bool enabled)
        => RetainersEnabled[name] = enabled;

    #endregion

    #region Overlay (visual only)

    public bool OverlayEnabled { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;

    #endregion

    #region Sell list

    [Serializable]
    public sealed class SellListEntry
    {
        public uint ItemId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int MinCountToSell { get; set; } = 1;
    }

    public Dictionary<uint, SellListEntry> SellList { get; set; } = new();

    public bool HasSellItem(uint itemId)
        => SellList.ContainsKey(itemId);

    public bool TryAddSellItem(uint itemId, string name)
    {
        if (itemId == 0) return false;
        if (SellList.ContainsKey(itemId)) return false;

        SellList[itemId] = new SellListEntry
        {
            ItemId = itemId,
            Name = (name ?? string.Empty).Trim(),
            MinCountToSell = 1,
        };

        return true;
    }

    public bool RemoveSellItem(uint itemId)
        => SellList.Remove(itemId);

    public void ClearSellList()
        => SellList.Clear();

    public IEnumerable<SellListEntry> GetSellListSorted()
        => SellList.Values
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId);

    #endregion
}
