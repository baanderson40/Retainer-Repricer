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

    // Dalamud serialization schema version; bump when config layout changes.
    public int Version { get; set; } = 3;

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
    // Keeps UI tidy by exiting the retainer bell automatically after each run.
    public bool CloseRetainerListAddon { get; set; } = true;

    #endregion

    #region Retainer enable/disable

    // Tracks per-retainer enablement (case-insensitive) synced from the RetainerList UI.
    public Dictionary<string, bool> RetainersEnabled { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRetainerEnabled(string name)
        => RetainersEnabled.TryGetValue(name, out var enabled) ? enabled : true;

    public void SetRetainerEnabled(string name, bool enabled)
        => RetainersEnabled[name] = enabled;

    #endregion

    #region Overlay (visual only)

    // Controls overlay rendering on top of RetainerList plus stored offsets.
    public bool OverlayEnabled { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;
    // Remembers whether the offset sliders are expanded in the config UI.
    public bool ShowOverlayOffsetControls { get; set; } = false;

    #endregion

    #region Pricing gate

    // Enables the Universalis-based price floor gate for repricing existing listings.
    public bool EnableUndercutPreventionGate { get; set; } = true;
    // Allows HTTP calls to Universalis for both gate logic and empty-market fallback.
    public bool UseUniversalisApi { get; set; } = true;
    // Percentage of Universalis average that becomes the minimum acceptable price floor (10–90%).
    public float UndercutPreventionPercent { get; set; } = 0.5f;

    /// <summary>
    /// When market board returns "No items found", use Universalis average sale price
    /// to determine listing price instead of skipping the item.
    /// </summary>
    // When the market board shows no listings, fall back to Universalis averages instead of skipping.
    public bool UseUniversalisForEmptyMarket { get; set; } = true;

    #endregion

    #region Sell list

    // HQ glyph suffix (matches in-game suffix style).
    public const char HqGlyphChar = '\uE03C';

    [Serializable]
    public sealed class SellListEntry
    {
        // Base (Lumina) item id (NQ row id).
        public uint ItemId { get; set; }

        // Quality key for this SellList entry.
        public bool IsHq { get; set; }

        // Display name (we will bake HQ glyph in as suffix for HQ entries).
        public string Name { get; set; } = string.Empty;

        // Per-quality threshold (count only that quality).
        public int MinCountToSell { get; set; } = 1;
    }

    // Key = (baseItemId << 1) | (isHq ? 1 : 0)
    private static ulong MakeSellKey(uint baseItemId, bool isHq)
        => ((ulong)baseItemId << 1) | (isHq ? 1UL : 0UL);

    // Stored as a single dictionary so HQ/NQ don’t collide.
    // Persisted sell list keyed by (itemId, HQ flag) for deterministic lookups.
    public Dictionary<ulong, SellListEntry> SellList { get; set; } = new();

    // Convenience: display name builder (HQ suffix).
    private static string BuildDisplayName(string name, bool isHq)
    {
        var n = (name ?? string.Empty).Trim();
        if (isHq)
            return (n + " " + HqGlyphChar).Trim(); // suffix (matches game convention)
        return n;
    }

    public bool HasSellItem(uint baseItemId, bool isHq)
        => SellList.ContainsKey(MakeSellKey(baseItemId, isHq));

    public bool TryAddSellItem(uint baseItemId, bool isHq, string name)
    {
        if (baseItemId == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.ContainsKey(key)) return false;

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = 1,
        };

        return true;
    }

    public bool TryAddSellItemWithMinCount(uint baseItemId, bool isHq, string name, int minCount)
    {
        if (baseItemId == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.ContainsKey(key)) return false;

        var clampedMinCount = Math.Clamp(minCount, 1, 999);

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = clampedMinCount,
        };

        return true;
    }

    public bool RemoveSellItem(uint baseItemId, bool isHq)
        => SellList.Remove(MakeSellKey(baseItemId, isHq));

    public void ClearSellList()
        => SellList.Clear();

    public IEnumerable<SellListEntry> GetSellListSorted()
        => SellList.Values
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0);

    #endregion
}
