using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RetainerRepricer;

public enum ContextMenuQuickAddModifier
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 3,
    LeftShift = 4,
    RightShift = 5,
    LeftCtrl = 6,
    RightCtrl = 7,
    LeftAlt = 8,
    RightAlt = 9,
}

public enum ContextMenuQuickListModifier
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 3,
    LeftShift = 4,
    RightShift = 5,
    LeftCtrl = 6,
    RightCtrl = 7,
    LeftAlt = 8,
    RightAlt = 9,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    #region Dalamud config versioning

    // Dalamud serialization schema version; bump when config layout changes.
    public int Version { get; set; } = 4;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        _pluginInterface = pi;
    }

    public void Save()
        => _pluginInterface?.SavePluginConfig(this);

    #endregion

    #region Global toggles

    // Master enable switch. When off: no runs, no context menu injection, no overlays.
    // Config UI stays usable so it’s easy to turn back on.
    public bool PluginEnabled { get; set; } = true;

    // Optional: when enabled, per-retainer sell caps become available in the Sell List.
    public bool EnablePerRetainerCaps { get; set; } = false;

    // When enabled, a completed eligible vendor preview may start an NPC sale automatically.
    public bool AutoSellVendorPreviewCandidates { get; set; } = false;

    // Controls whether ImGui tooltips are shown while hovering configuration controls.
    public bool ShowTooltips { get; set; } = true;

    // Shows the Min Count popup when adding via context menu; when off, items add instantly with defaults.
    public bool EnableContextMenuPopup { get; set; } = true;

    // Optional modifier that turns inventory right-click into an immediate add-to-sell-list action.
    public ContextMenuQuickAddModifier ContextMenuQuickAddModifier { get; set; } = ContextMenuQuickAddModifier.None;

    // Optional modifier that turns inventory right-click into an immediate listing action.
    public ContextMenuQuickListModifier ContextMenuQuickListModifier { get; set; } = ContextMenuQuickListModifier.None;

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

    public Dictionary<string, RetainerBehavior> RetainerSettings { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> GetAllRetainerNames()
    {
        EnsureRetainerDictionaries();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in RetainersEnabled.Keys)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name))
                yield return name;
        }

        foreach (var name in RetainerSettings.Keys)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name))
                yield return name;
        }
    }

    public IEnumerable<string> GetAllRetainerNames(ulong cid)
    {
        EnsureRetainerDictionaries();
        var prefix = MakeRetainerKeyPrefix(cid);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in RetainersEnabled.Keys.Concat(RetainerSettings.Keys))
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = key[prefix.Length..];
            if (name.Length > 0 && seen.Add(name)) yield return name;
        }
    }

    public bool HasScopedRetainerConfiguration(ulong cid, string name)
    {
        EnsureRetainerDictionaries();
        var key = MakeRetainerKey(cid, name);
        return RetainersEnabled.ContainsKey(key) || RetainerSettings.ContainsKey(key);
    }

    public bool IsRetainerEnabled(string name)
        => GetRetainerBehavior(name).Enabled;

    public bool IsRetainerEnabled(ulong cid, string name)
        => GetRetainerBehavior(cid, name).Enabled;

    public void SetRetainerEnabled(string name, bool enabled)
    {
        EnsureRetainerDictionaries();
        var key = NormalizeRetainerName(name);
        RetainersEnabled[key] = enabled;
        var behavior = EnsureRetainerBehavior(key);
        behavior.Enabled = enabled;
    }

    public void SetRetainerEnabled(ulong cid, string name, bool enabled)
    {
        EnsureRetainerDictionaries();
        var key = MakeRetainerKey(cid, name);
        RetainersEnabled[key] = enabled;
        var behavior = EnsureRetainerBehavior(cid, name);
        behavior.Enabled = enabled;
    }

    public bool RetainerAllowsSell(string name)
    {
        var behavior = GetRetainerBehavior(name);
        return behavior.Enabled && behavior.AllowSell;
    }

    public bool RetainerAllowsSell(ulong cid, string name)
    {
        var behavior = GetRetainerBehavior(cid, name);
        return behavior.Enabled && behavior.AllowSell;
    }

    public bool RetainerAllowsReprice(string name)
    {
        var behavior = GetRetainerBehavior(name);
        return behavior.Enabled && behavior.AllowReprice;
    }

    public bool RetainerAllowsReprice(ulong cid, string name)
    {
        var behavior = GetRetainerBehavior(cid, name);
        return behavior.Enabled && behavior.AllowReprice;
    }

    public void SetRetainerSellEnabled(string name, bool allowSell)
    {
        var behavior = EnsureRetainerBehavior(name);
        behavior.AllowSell = allowSell;
    }

    public void SetRetainerSellEnabled(ulong cid, string name, bool allowSell)
        => EnsureRetainerBehavior(cid, name).AllowSell = allowSell;

    public void SetRetainerRepriceEnabled(string name, bool allowReprice)
    {
        var behavior = EnsureRetainerBehavior(name);
        behavior.AllowReprice = allowReprice;
    }

    public void SetRetainerRepriceEnabled(ulong cid, string name, bool allowReprice)
        => EnsureRetainerBehavior(cid, name).AllowReprice = allowReprice;

    public RetainerBehavior GetRetainerBehavior(string name)
        => EnsureRetainerBehavior(name);

    public RetainerBehavior GetRetainerBehavior(ulong cid, string name)
        => EnsureRetainerBehavior(cid, name);

    private RetainerBehavior EnsureRetainerBehavior(string name)
    {
        EnsureRetainerDictionaries();
        var key = NormalizeRetainerName(name);
        if (!RetainerSettings.TryGetValue(key, out var behavior) || behavior == null)
        {
            var enabled = RetainersEnabled.TryGetValue(key, out var existing)
                ? existing
                : true;

            behavior = new RetainerBehavior
            {
                Enabled = enabled,
                AllowSell = true,
                AllowReprice = true,
            };

            RetainerSettings[key] = behavior;
        }

        behavior.Normalize();
        return behavior;
    }

    private RetainerBehavior EnsureRetainerBehavior(ulong cid, string name)
    {
        EnsureRetainerDictionaries();
        var key = MakeRetainerKey(cid, name);
        if (!RetainerSettings.TryGetValue(key, out var behavior) || behavior == null)
        {
            var legacyKey = NormalizeRetainerName(name);
            var legacyBehavior = RetainerSettings.TryGetValue(legacyKey, out var existingBehavior)
                ? existingBehavior
                : null;
            var enabled = RetainersEnabled.TryGetValue(key, out var scopedEnabled)
                ? scopedEnabled
                : RetainersEnabled.TryGetValue(legacyKey, out var legacyEnabled)
                    ? legacyEnabled
                    : legacyBehavior?.Enabled ?? true;
            behavior = new RetainerBehavior
            {
                Enabled = enabled,
                AllowSell = legacyBehavior?.AllowSell ?? true,
                AllowReprice = legacyBehavior?.AllowReprice ?? true,
            };
            RetainerSettings[key] = behavior;
        }

        behavior.Normalize();
        return behavior;
    }

    internal static string NormalizeRetainerName(string name)
        => (name ?? string.Empty).Trim();

    private void EnsureRetainerDictionaries()
    {
        RetainersEnabled ??= new(StringComparer.OrdinalIgnoreCase);
        RetainerSettings ??= new(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Overlay (visual only)

    // Controls overlay rendering on top of RetainerList plus stored offsets.
    public bool OverlayEnabled { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;
    // Remembers whether the offset sliders are expanded in the config UI.
    public bool ShowOverlayOffsetControls { get; set; } = false;

    // Controls whether the Advanced tab is visible in the config UI.
    public bool ShowAdvancedSettingsTab { get; set; } = false;

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

    // Smart sort controls (Universalis-backed sell order weighting)
    public bool EnableUniversalisSmartSort { get; set; } = false;
    public float SmartSortVelocityWeight { get; set; } = 0.555f;
    public float SmartSortPriceWeight { get; set; } = 0.445f;
    public int SmartSortRefreshMinutes { get; set; } = 30;
    public long SmartSortLastRunTicks { get; set; } = 0;

    // Inventory pruning (removes stale sell entries before sorting/selling)
    public bool AutoPruneMissingInventory { get; set; } = false;

    public (float velocityWeight, float priceWeight) GetSmartSortWeights()
    {
        var v = SmartSortVelocityWeight;
        var p = SmartSortPriceWeight;
        if (v < 0f) v = 0f;
        if (p < 0f) p = 0f;
        var sum = v + p;
        if (sum <= 0f)
            return (0.555f, 0.445f);
        return (v / sum, p / sum);
    }

    public DateTime GetSmartSortLastRunUtc()
        => SmartSortLastRunTicks <= 0 ? DateTime.MinValue : new DateTime(SmartSortLastRunTicks, DateTimeKind.Utc);

    public void SetSmartSortLastRunUtc(DateTime utc)
        => SmartSortLastRunTicks = utc <= DateTime.MinValue ? 0 : utc.ToUniversalTime().Ticks;

    #endregion

    #region Advanced automation tuning

    // Pricing aggression / validation controls.
    public int UndercutAmount { get; set; } = 1;
    public float MarketValidationThreshold { get; set; } = 2.0f;

    // Automation pacing (seconds unless noted).
    public double ActionIntervalSeconds { get; set; } = 0.15d;
    public double RetainerSyncIntervalSeconds { get; set; } = 2.0d;
    public double ItemSearchResultThrottleBackoffSeconds { get; set; } = 1.0d;
    public double MbBaseIntervalSeconds { get; set; } = 1.5d;
    public double MbIntervalMinSeconds { get; set; } = 1.0d;
    public double MbIntervalMaxSeconds { get; set; } = 2.0d;
    public double MbJitterMaxSeconds { get; set; } = 0.10d;
    public double IsrNoItemsSettleSeconds { get; set; } = 0.5d;
    public double IsrHqFilterInitialDelaySeconds { get; set; } = 1.25d;
    public double IsrHqFilterUiDebounceSeconds { get; set; } = 0.20d;
    public double IsrHqFilterOpenRetrySeconds { get; set; } = 0.30d;
    public double IsrHqFilterPostOpenSeconds { get; set; } = 0.15d;
    public double FrameworkTickIntervalSeconds { get; set; } = 0.075d;
    public double AutoRetainerHandoffTimeoutSeconds { get; set; } = 10.0d;
    public int AutoRetainerNearVentureWaitMinutes { get; set; } = 5;

    // Smart sort scoring constants.
    public double SmartSortVelocityCap { get; set; } = 100.0d;
    public double SmartSortVelocityLogBase { get; set; } = 101.0d;
    public double SmartSortPriceLogBase { get; set; } = 100001.0d;

    #endregion

    #region Sell list

    // HQ glyph suffix (matches in-game suffix style).
    public const char HqGlyphChar = '\uE03C';
    public const int RetainerCapDefault = 1;
    public const int RetainerCapMax = 20;

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

        // Explicit ordering (1..N). Lower numbers run earlier.
        public int SortOrder { get; set; }

        // Optional per-retainer caps for new listings; defaults to 1 per retainer when unset.
        public Dictionary<string, int> RetainerCaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Optional per-retainer stack sizes (0 = any available amount).
        public Dictionary<string, int> RetainerStackSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Protect this entry from automatic pruning even if it is missing from inventory.
        public bool PreserveFromAutoPrune { get; set; }

        // Null denotes an entry created before character-scoped sell lists existed.
        // Legacy entries stay shared and are excluded from automatic pruning.
        public HashSet<ulong>? CharacterIds { get; set; }

        public bool IsAssignedToCharacter(ulong cid)
            => CharacterIds is null || (cid != 0 && CharacterIds.Contains(cid));

        public bool IsExplicitlyAssignedToCharacter(ulong cid)
            => cid != 0 && CharacterIds?.Contains(cid) == true;

        public bool AddCharacterAssignment(ulong cid)
        {
            if (cid == 0)
                return false;

            CharacterIds ??= new();
            return CharacterIds.Add(cid);
        }

        public bool RemoveCharacterAssignment(ulong cid)
            => cid != 0 && CharacterIds?.Remove(cid) == true;

        public bool HasNoCharacterAssignments
            => CharacterIds is { Count: 0 };

        public void EnsureRetainerCaps()
        {
            RetainerCaps ??= new(StringComparer.OrdinalIgnoreCase);
        }

        public void EnsureRetainerStackSizes()
        {
            RetainerStackSizes ??= new(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetRetainerCap(string name, out int cap)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
            {
                cap = RetainerCapDefault;
                return false;
            }

            if (RetainerCaps.TryGetValue(key, out cap))
                return true;

            cap = RetainerCapDefault;
            return false;
        }

        public bool TryGetRetainerCap(ulong cid, string name, out int cap)
        {
            EnsureRetainerCaps();
            var key = MakeRetainerKey(cid, name);
            if (RetainerCaps.TryGetValue(key, out cap))
                return true;

            if (RetainerCaps.TryGetValue(NormalizeRetainerName(name), out cap))
                return true;

            cap = RetainerCapDefault;
            return false;
        }

        public int GetRetainerCapOrDefault(string name)
        {
            TryGetRetainerCap(name, out var cap);
            return cap;
        }

        public int GetRetainerCapOrDefault(ulong cid, string name)
        {
            TryGetRetainerCap(cid, name, out var cap);
            return cap;
        }

        public void SetRetainerCap(string name, int value)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            var clamped = Math.Clamp(value, 0, RetainerCapMax);

            if (clamped == RetainerCapDefault)
            {
                RetainerCaps.Remove(key);
                return;
            }

            RetainerCaps[key] = clamped;
        }

        public void SetRetainerCap(ulong cid, string name, int value)
        {
            EnsureRetainerCaps();
            var key = MakeRetainerKey(cid, name);
            var clamped = Math.Clamp(value, 0, RetainerCapMax);
            RetainerCaps[key] = clamped;
        }

        public void ClearRetainerCap(string name)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            RetainerCaps.Remove(key);
        }

        public int GetRetainerStackSize(string name)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return 0;

            return RetainerStackSizes.TryGetValue(key, out var value) ? value : 0;
        }

        public int GetRetainerStackSize(ulong cid, string name)
        {
            EnsureRetainerStackSizes();
            if (RetainerStackSizes.TryGetValue(MakeRetainerKey(cid, name), out var value))
                return value;
            return RetainerStackSizes.TryGetValue(NormalizeRetainerName(name), out value) ? value : 0;
        }

        public void SetRetainerStackSize(string name, int value, int stackCap)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            var clamped = Math.Clamp(value, 0, stackCap);

            if (clamped == 0)
                RetainerStackSizes.Remove(key);
            else
                RetainerStackSizes[key] = clamped;

            RefreshMinCountFromStackSizes();
        }

        public void SetRetainerStackSize(ulong cid, string name, int value, int stackCap)
        {
            EnsureRetainerStackSizes();
            var key = MakeRetainerKey(cid, name);
            var clamped = Math.Clamp(value, 0, stackCap);
            RetainerStackSizes[key] = clamped;
            RefreshMinCountFromStackSizes();
        }

        public void ClearRetainerStackSize(string name)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            if (RetainerStackSizes.Remove(key))
                RefreshMinCountFromStackSizes();
        }

        public void RefreshMinCountFromStackSizes()
        {
            EnsureRetainerStackSizes();
            if (RetainerStackSizes.Count == 0)
                return;

            var minPositive = int.MaxValue;
            foreach (var kvp in RetainerStackSizes)
            {
                if (kvp.Value <= 0)
                    continue;

                if (kvp.Value < minPositive)
                    minPositive = kvp.Value;
            }

            if (minPositive != int.MaxValue)
                MinCountToSell = Math.Clamp(minPositive, 1, 999);
        }
    }

    // Key = (baseItemId << 1) | (isHq ? 1 : 0)
    internal static ulong MakeSellKey(uint baseItemId, bool isHq)
        => ((ulong)baseItemId << 1) | (isHq ? 1UL : 0UL);

    // Stored as a single dictionary so HQ/NQ don’t collide.
    // Persisted sell list keyed by (itemId, HQ flag) for deterministic lookups.
    public Dictionary<ulong, SellListEntry> SellList { get; set; } = new();

    // Convenience: display name builder (HQ suffix).
    internal static string BuildDisplayName(string name, bool isHq)
    {
        var n = (name ?? string.Empty).Trim();
        if (isHq)
            return (n + " " + HqGlyphChar).Trim(); // suffix (matches game convention)
        return n;
    }

    public bool TryGetSellEntryKeyByDisplayName(string displayName, ulong cid, out ulong key)
    {
        key = 0;
        if (SellList == null || SellList.Count == 0)
            return false;

        foreach (var entry in SellList.Values)
        {
            if (entry == null)
                continue;

            if (!entry.IsAssignedToCharacter(cid))
                continue;

            if (string.Equals(entry.Name, displayName, StringComparison.OrdinalIgnoreCase))
            {
                key = MakeSellKey(entry.ItemId, entry.IsHq);
                return true;
            }
        }

        return false;
    }

    public bool HasSellItem(uint baseItemId, bool isHq, ulong cid)
        => SellList.TryGetValue(MakeSellKey(baseItemId, isHq), out var entry) &&
           entry?.IsAssignedToCharacter(cid) == true;

    public bool TryAddSellItem(uint baseItemId, bool isHq, string name, ulong cid, int? priorityOverride = null)
    {
        if (baseItemId == 0 || cid == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.TryGetValue(key, out var existing))
        {
            if (existing is null || existing.CharacterIds is null)
                return false;

            return existing.AddCharacterAssignment(cid);
        }

        var nextOrder = GetNextSortOrder();

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = 1,
            SortOrder = nextOrder,
            PreserveFromAutoPrune = false,
            CharacterIds = new() { cid },
        };

        if (priorityOverride.HasValue)
            TrySetSellItemOrder(baseItemId, isHq, priorityOverride.Value);

        return true;
    }

    public bool TryAddSellItemWithMinCount(
        uint baseItemId,
        bool isHq,
        string name,
        int minCount,
        ulong cid,
        int? priorityOverride = null)
    {
        if (baseItemId == 0 || cid == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.TryGetValue(key, out var existing))
        {
            if (existing is null || existing.CharacterIds is null)
                return false;

            return existing.AddCharacterAssignment(cid);
        }

        var clampedMinCount = Math.Clamp(minCount, 1, 999);
        var nextOrder = GetNextSortOrder();

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = clampedMinCount,
            SortOrder = nextOrder,
            PreserveFromAutoPrune = false,
            CharacterIds = new() { cid },
        };

        if (priorityOverride.HasValue)
            TrySetSellItemOrder(baseItemId, isHq, priorityOverride.Value);

        return true;
    }

    public bool RemoveSellItem(uint baseItemId, bool isHq, ulong cid)
    {
        var key = MakeSellKey(baseItemId, isHq);
        if (!SellList.TryGetValue(key, out var entry) || entry == null)
            return false;

        // Legacy entries were intentionally global; manual removal retains that behavior.
        var removed = entry.CharacterIds is null
            ? SellList.Remove(key)
            : entry.RemoveCharacterAssignment(cid);

        if (entry.HasNoCharacterAssignments)
            SellList.Remove(key);

        if (!SellList.ContainsKey(key))
            NormalizeSellListOrder(saveChanges: false);

        return removed;
    }

    public void ClearSellList(ulong cid)
    {
        if (cid == 0)
            return;

        var keysToRemove = new List<ulong>();
        foreach (var kvp in SellList)
        {
            var entry = kvp.Value;
            if (entry is null)
                continue;

            if (entry.CharacterIds is null ||
                (entry.RemoveCharacterAssignment(cid) && entry.HasNoCharacterAssignments))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
            SellList.Remove(key);

        NormalizeSellListOrder(saveChanges: false);
    }

    public void SetSellItemPreserveFlag(uint baseItemId, bool isHq, bool preserve)
    {
        var key = MakeSellKey(baseItemId, isHq);
        if (!SellList.TryGetValue(key, out var entry) || entry == null)
            return;

        entry.PreserveFromAutoPrune = preserve;
    }

    public int RemoveSellEntries(Func<SellListEntry, bool> predicate)
    {
        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        if (SellList == null || SellList.Count == 0)
            return 0;

        var keysToRemove = new List<ulong>();

        foreach (var kvp in SellList)
        {
            var entry = kvp.Value;
            if (entry == null)
                continue;

            if (predicate(entry))
                keysToRemove.Add(kvp.Key);
        }

        if (keysToRemove.Count == 0)
            return 0;

        foreach (var key in keysToRemove)
            SellList.Remove(key);

        NormalizeSellListOrder(saveChanges: false);
        return keysToRemove.Count;
    }

    public IEnumerable<SellListEntry> GetSellListOrdered(ulong cid)
    {
        NormalizeSellListOrder(saveChanges: false);
        return SellList.Values
            .Where(e => e != null && e.IsAssignedToCharacter(cid))
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0);
    }

    public bool TrySetSellItemOrder(uint baseItemId, bool isHq, int requestedOrder)
    {
        var key = MakeSellKey(baseItemId, isHq);
        if (!SellList.TryGetValue(key, out var entry))
            return false;

        NormalizeSellListOrder(saveChanges: false);

        var ordered = SellList.Values
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0)
            .ToList();

        ordered.Remove(entry);

        var maxPosition = ordered.Count + 1;
        var clamped = Math.Clamp(requestedOrder, 1, maxPosition);
        ordered.Insert(clamped - 1, entry);

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var desired = i + 1;
            if (ordered[i].SortOrder != desired)
            {
                ordered[i].SortOrder = desired;
                changed = true;
            }
        }

        return changed;
    }

    public bool NormalizeSellListOrder(bool saveChanges = true)
    {
        if (SellList.Count == 0)
            return false;

        var ordered = SellList.Values
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0)
            .ToList();

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var desired = i + 1;
            if (ordered[i].SortOrder != desired)
            {
                ordered[i].SortOrder = desired;
                changed = true;
            }
        }

        if (changed && saveChanges)
            Save();

        return changed;
    }

    public int GetAppendSortOrder()
    {
        NormalizeSellListOrder(saveChanges: false);
        return SellList.Count + 1;
    }

    private int GetNextSortOrder()
    {
        NormalizeSellListOrder(saveChanges: false);

        if (SellList.Count == 0)
            return 1;

        var maxOrder = SellList.Values
            .Select(e => e.SortOrder)
            .DefaultIfEmpty(0)
            .Max();

        return maxOrder < 1 ? SellList.Count + 1 : maxOrder + 1;
    }

    #endregion

    #region Retainer behavior model

    [Serializable]
    public sealed class CharacterSettings
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public bool ProcessAfterAutoRetainer { get; set; }
        public bool AutoListInventoryItems { get; set; }
        public int AutoListMinimumStackValue { get; set; } = 1000;
        public bool ReturnLowValueListingsToInventory { get; set; }
        public int MinimumListedStackValueToKeep { get; set; } = 1000;
        public int VendorPreviewMaximumStackMarketValue { get; set; }
        public HashSet<ulong> AutoListItemBlacklist { get; set; } = new();
        public HashSet<string> AutoListBlocklistIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VendorAllowlistIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // Character settings are keyed by AutoRetainer's stable content ID.
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();
    public Dictionary<string, AutoListBlocklist> AutoListBlocklists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VendorAllowlist> VendorAllowlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Serializable]
    public sealed class AutoListBlocklist
    {
        public string Name { get; set; } = string.Empty;
        public HashSet<ulong> ItemKeys { get; set; } = new();
    }

    [Serializable]
    public sealed class VendorAllowlist
    {
        public string Name { get; set; } = string.Empty;
        public HashSet<ulong> ItemKeys { get; set; } = new();
        public int MaximumStackMarketValue { get; set; }
    }

    public bool MigrateLegacyAutoListBlocklists()
    {
        AutoListBlocklists ??= new(StringComparer.OrdinalIgnoreCase);
        VendorAllowlists ??= new(StringComparer.OrdinalIgnoreCase);
        Characters ??= new();
        var changed = false;
        if (Version < 4)
        {
            foreach (var (cid, settings) in Characters)
            {
                if (settings?.AutoListItemBlacklist is not { Count: > 0 } legacy)
                    continue;
                var id = $"legacy-{cid:X16}";
                if (!AutoListBlocklists.ContainsKey(id))
                {
                    var displayName = string.IsNullOrWhiteSpace(settings.Name) ? cid.ToString("X16", CultureInfo.InvariantCulture)
                        : string.IsNullOrWhiteSpace(settings.World) ? settings.Name : $"{settings.Name}@{settings.World}";
                    AutoListBlocklists[id] = new AutoListBlocklist
                    {
                        Name = $"{displayName} exclusions",
                        ItemKeys = new HashSet<ulong>(legacy),
                    };
                }
                settings.AutoListBlocklistIds ??= new(StringComparer.OrdinalIgnoreCase);
                settings.AutoListBlocklistIds.Add(id);
                settings.AutoListItemBlacklist.Clear();
                changed = true;
            }
            Version = 4;
            changed = true;
        }
        foreach (var list in AutoListBlocklists.Values)
        {
            if (list == null) continue;
            list.ItemKeys ??= new();
            list.Name ??= string.Empty;
        }
        foreach (var settings in Characters.Values)
        {
            if (settings == null) continue;
            settings.AutoListBlocklistIds ??= new(StringComparer.OrdinalIgnoreCase);
            settings.VendorAllowlistIds ??= new(StringComparer.OrdinalIgnoreCase);
            settings.VendorPreviewMaximumStackMarketValue = Math.Clamp(settings.VendorPreviewMaximumStackMarketValue, 0, 999_999_999);
        }
        foreach (var list in VendorAllowlists.Values)
        {
            if (list == null) continue;
            list.Name ??= string.Empty;
            list.ItemKeys ??= new();
            list.MaximumStackMarketValue = Math.Clamp(list.MaximumStackMarketValue, 0, 999_999_999);
        }
        if (Version < 6)
        {
            // Carry existing per-character limits into previously assigned shared lists,
            // choosing the lowest legacy limit when multiple characters shared a list.
            foreach (var (listId, list) in VendorAllowlists)
            {
                if (list == null || list.MaximumStackMarketValue > 0) continue;
                var legacyThresholds = Characters
                    .Where(pair => pair.Value?.VendorAllowlistIds?.Contains(listId) == true)
                    .Select(pair => pair.Value!.VendorPreviewMaximumStackMarketValue)
                    .ToList();
                if (legacyThresholds.Count > 0)
                    list.MaximumStackMarketValue = legacyThresholds.Min();
            }
            Version = 6;
            changed = true;
        }
        return changed;
    }

    public HashSet<ulong> GetEffectiveAutoListBlocklist(ulong cid)
    {
        var result = new HashSet<ulong>();
        if (cid == 0 || Characters?.TryGetValue(cid, out var settings) != true || settings == null) return result;
        foreach (var id in settings.AutoListBlocklistIds ?? new(StringComparer.OrdinalIgnoreCase))
            if (AutoListBlocklists?.TryGetValue(id, out var list) == true && list?.ItemKeys != null) result.UnionWith(list.ItemKeys);
        return result;
    }

    public List<string> GetAutoListBlocklistSources(ulong cid, ulong itemKey)
    {
        var sources = new List<string>();
        if (Characters?.TryGetValue(cid, out var settings) != true || settings == null) return sources;
        foreach (var id in settings.AutoListBlocklistIds ?? new(StringComparer.OrdinalIgnoreCase))
            if (AutoListBlocklists?.TryGetValue(id, out var list) == true && list?.ItemKeys.Contains(itemKey) == true) sources.Add(list.Name);
        return sources;
    }

    public bool TryCreateAutoListBlocklist(string id, string name, IEnumerable<ulong>? keys = null)
    {
        AutoListBlocklists ??= new(StringComparer.OrdinalIgnoreCase);
        name = name.Trim();
        if (name.Length == 0 || AutoListBlocklists.ContainsKey(id) || AutoListBlocklists.Values.Any(list => string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        AutoListBlocklists[id] = new AutoListBlocklist { Name = name, ItemKeys = keys?.ToHashSet() ?? new() };
        return true;
    }

    public bool TryRenameAutoListBlocklist(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || AutoListBlocklists?.TryGetValue(id, out var list) != true || list == null || AutoListBlocklists.Values.Any(other => !ReferenceEquals(other, list) && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        list.Name = name;
        return true;
    }

    public int SetBlocklistKeys(string id, IEnumerable<ulong> keys, bool blocked)
    {
        if (AutoListBlocklists?.TryGetValue(id, out var list) != true || list == null) return 0;
        list.ItemKeys ??= new();
        var changed = 0;
        foreach (var key in keys) changed += blocked ? (list.ItemKeys.Add(key) ? 1 : 0) : (list.ItemKeys.Remove(key) ? 1 : 0);
        return changed;
    }

    public HashSet<ulong> GetEffectiveVendorAllowlist(ulong cid)
    {
        var result = new HashSet<ulong>();
        if (cid == 0 || Characters?.TryGetValue(cid, out var settings) != true || settings == null) return result;
        foreach (var id in settings.VendorAllowlistIds ?? new(StringComparer.OrdinalIgnoreCase))
            if (VendorAllowlists?.TryGetValue(id, out var list) == true && list?.ItemKeys != null)
                result.UnionWith(list.ItemKeys);
        return result;
    }

    public int GetEffectiveVendorThreshold(ulong cid, ulong itemKey)
    {
        if (cid == 0 || Characters?.TryGetValue(cid, out var settings) != true || settings == null ||
            VendorAllowlists == null || settings.VendorAllowlistIds == null)
            return 0;
        var thresholds = settings.VendorAllowlistIds
            .Where(id => VendorAllowlists.TryGetValue(id, out var list) && list?.ItemKeys?.Contains(itemKey) == true)
            .Select(id => VendorAllowlists[id].MaximumStackMarketValue)
            .ToList();
        if (thresholds.Count == 0) return 0;
        return Math.Min(thresholds.Min(), settings.AutoListMinimumStackValue);
    }

    public bool TryCreateVendorAllowlist(string id, string name, IEnumerable<ulong>? keys = null)
    {
        VendorAllowlists ??= new(StringComparer.OrdinalIgnoreCase);
        name = name.Trim();
        if (name.Length == 0 || VendorAllowlists.ContainsKey(id) ||
            VendorAllowlists.Values.Any(list => string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        VendorAllowlists[id] = new VendorAllowlist { Name = name, ItemKeys = keys?.ToHashSet() ?? new() };
        return true;
    }

    public bool TryRenameVendorAllowlist(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || VendorAllowlists?.TryGetValue(id, out var list) != true || list == null ||
            VendorAllowlists.Values.Any(other => !ReferenceEquals(other, list) &&
                string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        list.Name = name;
        return true;
    }

    public int SetVendorAllowlistKeys(string id, IEnumerable<ulong> keys, bool allowed)
    {
        if (VendorAllowlists?.TryGetValue(id, out var list) != true || list == null) return 0;
        list.ItemKeys ??= new();
        var changed = 0;
        foreach (var key in keys) changed += allowed ? (list.ItemKeys.Add(key) ? 1 : 0) : (list.ItemKeys.Remove(key) ? 1 : 0);
        return changed;
    }
    // UI order only; never changes AutoRetainer's processing order.
    public List<ulong> CharacterDisplayOrder { get; set; } = new();
    public HashSet<ulong> BlacklistedCharacters { get; set; } = new();
    public HashSet<string> BlacklistedRetainers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool NormalizeCharacterDisplayOrder(IEnumerable<ulong> knownCharacterIds)
    {
        var ordered = (CharacterDisplayOrder ?? new List<ulong>())
            .Concat(knownCharacterIds)
            .Where(cid => cid != 0)
            .Distinct()
            .ToList();
        if (CharacterDisplayOrder != null && CharacterDisplayOrder.SequenceEqual(ordered))
            return false;

        // Retain missing/blacklisted IDs so they return to their previous position.
        CharacterDisplayOrder = ordered;
        return true;
    }

    public bool TryMoveCharacterDisplay(ulong cid, int direction, IReadOnlyCollection<ulong> visibleCharacterIds)
    {
        if (direction is not (-1 or 1) || CharacterDisplayOrder == null)
            return false;

        var visible = visibleCharacterIds.ToHashSet();
        var orderedVisible = CharacterDisplayOrder.Where(visible.Contains).ToList();
        var index = orderedVisible.IndexOf(cid);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= orderedVisible.Count)
            return false;

        var sourcePosition = CharacterDisplayOrder.IndexOf(cid);
        var targetPosition = CharacterDisplayOrder.IndexOf(orderedVisible[target]);
        (CharacterDisplayOrder[sourcePosition], CharacterDisplayOrder[targetPosition]) =
            (CharacterDisplayOrder[targetPosition], CharacterDisplayOrder[sourcePosition]);
        return true;
    }

    public CharacterSettings GetCharacterSettings(ulong cid, string? name = null, string? world = null)
    {
        Characters ??= new();
        if (!Characters.TryGetValue(cid, out var settings) || settings == null)
        {
            settings = new CharacterSettings();
            Characters[cid] = settings;
        }

        settings.AutoListItemBlacklist ??= new();
        settings.AutoListBlocklistIds ??= new(StringComparer.OrdinalIgnoreCase);
        settings.VendorAllowlistIds ??= new(StringComparer.OrdinalIgnoreCase);
        settings.AutoListMinimumStackValue = Math.Clamp(settings.AutoListMinimumStackValue, 1, 999_999_999);
        settings.MinimumListedStackValueToKeep = Math.Clamp(settings.MinimumListedStackValueToKeep, 1, 999_999_999);
        settings.VendorPreviewMaximumStackMarketValue = Math.Clamp(settings.VendorPreviewMaximumStackMarketValue, 0, settings.AutoListMinimumStackValue);

        if (!string.IsNullOrWhiteSpace(name)) settings.Name = name.Trim();
        if (!string.IsNullOrWhiteSpace(world)) settings.World = world.Trim();
        return settings;
    }

    public bool IsAutoListItemBlacklisted(ulong cid, uint itemId, bool isHq)
    {
        if (cid == 0 || itemId == 0)
            return false;

        return GetCharacterSettings(cid).AutoListItemBlacklist.Contains(MakeSellKey(itemId, isHq));
    }

    public void SetAutoListItemBlacklisted(ulong cid, uint itemId, bool isHq, bool blacklisted)
    {
        if (cid == 0 || itemId == 0)
            return;

        var settings = GetCharacterSettings(cid);
        var key = MakeSellKey(itemId, isHq);
        if (blacklisted)
            settings.AutoListItemBlacklist.Add(key);
        else
            settings.AutoListItemBlacklist.Remove(key);
    }

    public bool IsCharacterBlacklisted(ulong cid)
        => BlacklistedCharacters?.Contains(cid) == true;

    public void SetCharacterBlacklisted(ulong cid, bool blacklisted)
    {
        BlacklistedCharacters ??= new();
        if (blacklisted) BlacklistedCharacters.Add(cid);
        else BlacklistedCharacters.Remove(cid);
    }

    public static string MakeRetainerKey(ulong cid, string name)
        => $"{cid.ToString("X16", CultureInfo.InvariantCulture)}:{NormalizeRetainerName(name)}";

    public static string MakeRetainerKeyPrefix(ulong cid)
        => $"{cid.ToString("X16", CultureInfo.InvariantCulture)}:";

    public bool IsRetainerBlacklisted(ulong cid, string name)
        => BlacklistedRetainers?.Contains(MakeRetainerKey(cid, name)) == true;

    public void SetRetainerBlacklisted(ulong cid, string name, bool blacklisted)
    {
        BlacklistedRetainers ??= new(StringComparer.OrdinalIgnoreCase);
        var key = MakeRetainerKey(cid, name);
        if (blacklisted) BlacklistedRetainers.Add(key);
        else BlacklistedRetainers.Remove(key);
    }

    public void RemoveCharacterConfiguration(ulong cid)
    {
        Characters?.Remove(cid);
        CharacterDisplayOrder?.RemoveAll(id => id == cid);
        BlacklistedCharacters?.Remove(cid);
        var prefix = MakeRetainerKeyPrefix(cid);
        RetainersEnabled?.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()
            .ForEach(x => RetainersEnabled.Remove(x));
        RetainerSettings?.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()
            .ForEach(x => RetainerSettings.Remove(x));
        BlacklistedRetainers?.RemoveWhere(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (SellList != null)
        {
            var unassignedEntries = new List<ulong>();
            foreach (var kvp in SellList)
            {
                var entry = kvp.Value;
                if (entry?.RemoveCharacterAssignment(cid) == true && entry.HasNoCharacterAssignments)
                    unassignedEntries.Add(kvp.Key);

                if (entry?.RetainerCaps != null)
                    entry.RetainerCaps.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()
                        .ForEach(x => entry.RetainerCaps.Remove(x));
                if (entry?.RetainerStackSizes != null)
                    entry.RetainerStackSizes.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()
                        .ForEach(x => entry.RetainerStackSizes.Remove(x));
            }

            foreach (var key in unassignedEntries)
                SellList.Remove(key);

            if (unassignedEntries.Count > 0)
                NormalizeSellListOrder(saveChanges: false);
        }
    }

    public void RemoveRetainerConfiguration(ulong cid, string name)
    {
        var key = MakeRetainerKey(cid, name);
        RetainersEnabled?.Remove(key);
        RetainerSettings?.Remove(key);
        BlacklistedRetainers?.Remove(key);

        if (SellList != null)
        {
            foreach (var entry in SellList.Values)
            {
                entry?.RetainerCaps?.Remove(key);
                entry?.RetainerStackSizes?.Remove(key);
            }
        }
    }

    public bool MigrateLegacyRetainerConfiguration(ulong cid, string name)
    {
        var normalizedName = NormalizeRetainerName(name);
        if (normalizedName.Length == 0)
            return false;

        var scopedKey = MakeRetainerKey(cid, normalizedName);
        var changed = false;
        EnsureRetainerDictionaries();

        if (!RetainersEnabled.ContainsKey(scopedKey) && RetainersEnabled.Remove(normalizedName, out var enabled))
        {
            RetainersEnabled[scopedKey] = enabled;
            changed = true;
        }
        else if (RetainersEnabled.Remove(normalizedName))
        {
            changed = true;
        }

        if (!RetainerSettings.ContainsKey(scopedKey) && RetainerSettings.Remove(normalizedName, out var behavior))
        {
            if (behavior != null)
            {
                RetainerSettings[scopedKey] = new RetainerBehavior
                {
                    Enabled = behavior.Enabled,
                    AllowSell = behavior.AllowSell,
                    AllowReprice = behavior.AllowReprice,
                };
            }
            changed = true;
        }
        else if (RetainerSettings.Remove(normalizedName))
        {
            changed = true;
        }

        var blacklisted = BlacklistedRetainers;
        if (blacklisted != null &&
            !blacklisted.Contains(scopedKey) && blacklisted.Remove(normalizedName))
        {
            blacklisted.Add(scopedKey);
            changed = true;
        }
        else if (blacklisted != null && blacklisted.Remove(normalizedName))
        {
            changed = true;
        }

        if (SellList != null)
        {
            foreach (var entry in SellList.Values)
            {
                if (entry?.RetainerCaps != null &&
                    !entry.RetainerCaps.ContainsKey(scopedKey) &&
                    entry.RetainerCaps.Remove(normalizedName, out var cap))
                {
                    entry.RetainerCaps[scopedKey] = cap;
                    changed = true;
                }
                else if (entry?.RetainerCaps?.Remove(normalizedName) == true)
                {
                    changed = true;
                }

                if (entry?.RetainerStackSizes != null &&
                    !entry.RetainerStackSizes.ContainsKey(scopedKey) &&
                    entry.RetainerStackSizes.Remove(normalizedName, out var stackSize))
                {
                    entry.RetainerStackSizes[scopedKey] = stackSize;
                    changed = true;
                }
                else if (entry?.RetainerStackSizes?.Remove(normalizedName) == true)
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    public bool MigrateRetainerConfiguration(ulong cid, string name, IEnumerable<string> roster)
    {
        var canonicalName = NormalizeRetainerName(name);
        if (canonicalName.Length == 0 || !canonicalName.EndsWith('\''))
            return false;

        var legacyName = canonicalName.TrimEnd('\'');
        if (legacyName.Length == 0 || string.Equals(canonicalName, legacyName, StringComparison.OrdinalIgnoreCase))
            return false;

        var rosterNames = roster
            .Select(NormalizeRetainerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!rosterNames.Contains(canonicalName) || rosterNames.Contains(legacyName))
            return false;

        var canonicalKey = MakeRetainerKey(cid, canonicalName);
        var malformedScopedKey = MakeRetainerKey(cid, legacyName);
        var changed = false;

        EnsureRetainerDictionaries();
        if (!RetainersEnabled.ContainsKey(canonicalKey))
        {
            if (RetainersEnabled.Remove(malformedScopedKey, out var scopedEnabled))
            {
                RetainersEnabled[canonicalKey] = scopedEnabled;
                changed = true;
            }
            else if (RetainersEnabled.TryGetValue(legacyName, out var legacyEnabled))
            {
                RetainersEnabled[canonicalKey] = legacyEnabled;
                RetainersEnabled.Remove(legacyName);
                changed = true;
            }
        }
        else if (RetainersEnabled.Remove(malformedScopedKey))
        {
            changed = true;
        }

        if (!RetainerSettings.ContainsKey(canonicalKey))
        {
            if (RetainerSettings.Remove(malformedScopedKey, out var scopedBehavior) && scopedBehavior != null)
            {
                RetainerSettings[canonicalKey] = new RetainerBehavior
                {
                    Enabled = scopedBehavior.Enabled,
                    AllowSell = scopedBehavior.AllowSell,
                    AllowReprice = scopedBehavior.AllowReprice,
                };
                changed = true;
            }
            else if (RetainerSettings.TryGetValue(legacyName, out var legacyBehavior) && legacyBehavior != null)
            {
                RetainerSettings[canonicalKey] = new RetainerBehavior
                {
                    Enabled = legacyBehavior.Enabled,
                    AllowSell = legacyBehavior.AllowSell,
                    AllowReprice = legacyBehavior.AllowReprice,
                };
                RetainerSettings.Remove(legacyName);
                changed = true;
            }
        }
        else if (RetainerSettings.Remove(malformedScopedKey))
        {
            changed = true;
        }

        if (RetainersEnabled.Remove(legacyName))
            changed = true;
        if (RetainerSettings.Remove(legacyName))
            changed = true;

        if (BlacklistedRetainers?.Contains(canonicalKey) != true)
        {
            if (BlacklistedRetainers is { } blacklistedRetainers && blacklistedRetainers.Remove(malformedScopedKey))
            {
                blacklistedRetainers.Add(canonicalKey);
                changed = true;
            }
            else if (BlacklistedRetainers?.Contains(legacyName) == true)
            {
                BlacklistedRetainers ??= new(StringComparer.OrdinalIgnoreCase);
                BlacklistedRetainers.Add(canonicalKey);
                BlacklistedRetainers.Remove(legacyName);
                changed = true;
            }
        }
        else if (BlacklistedRetainers?.Remove(legacyName) == true)
        {
            changed = true;
        }

        if (SellList != null)
        {
            foreach (var entry in SellList.Values)
            {
                if (entry?.RetainerCaps != null)
                {
                    if (entry.RetainerCaps.ContainsKey(canonicalKey))
                    {
                        if (entry.RetainerCaps.Remove(malformedScopedKey))
                            changed = true;
                    }
                    else if (entry.RetainerCaps.Remove(malformedScopedKey, out var scopedCap))
                    {
                        entry.RetainerCaps[canonicalKey] = scopedCap;
                        changed = true;
                    }
                    else if (entry.RetainerCaps.TryGetValue(legacyName, out var legacyCap))
                    {
                        entry.RetainerCaps[canonicalKey] = legacyCap;
                        entry.RetainerCaps.Remove(legacyName);
                        changed = true;
                    }
                }

                if (entry?.RetainerStackSizes != null)
                {
                    if (entry.RetainerStackSizes.ContainsKey(canonicalKey))
                    {
                        if (entry.RetainerStackSizes.Remove(malformedScopedKey))
                            changed = true;
                    }
                    else if (entry.RetainerStackSizes.Remove(malformedScopedKey, out var scopedStack))
                    {
                        entry.RetainerStackSizes[canonicalKey] = scopedStack;
                        changed = true;
                    }
                    else if (entry.RetainerStackSizes.TryGetValue(legacyName, out var legacyStack))
                    {
                        entry.RetainerStackSizes[canonicalKey] = legacyStack;
                        entry.RetainerStackSizes.Remove(legacyName);
                        changed = true;
                    }
                }

                if (entry?.RetainerCaps?.Remove(legacyName) == true)
                    changed = true;
                if (entry?.RetainerStackSizes?.Remove(legacyName) == true)
                    changed = true;
            }
        }

        return changed;
    }

    [Serializable]
    public sealed class RetainerBehavior
    {
        public bool Enabled { get; set; } = true;
        public bool AllowSell { get; set; } = true;
        public bool AllowReprice { get; set; } = true;

        public void Normalize()
        {
            AllowSell = AllowSell;
            AllowReprice = AllowReprice;
        }
    }

    #endregion
}
