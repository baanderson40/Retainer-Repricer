using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace RetainerRepricer.Services;

internal unsafe sealed class SellListInventoryPruner
{
    private static readonly InventoryType[] SellScanContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    private readonly Configuration _config;
    private readonly PluginLogger _log;

    public SellListInventoryPruner(Configuration config, PluginLogger log)
    {
        _config = config;
        _log = log;
    }

    public SellListInventoryPruneResult Run(string reason, ulong cid, bool saveConfig = true)
    {
        if (cid == 0)
        {
            _log.Warning("[RR][Prune] Skipped: current character is unavailable ({Reason}).", reason);
            return SellListInventoryPruneResult.Skipped(reason);
        }

        var snapshot = CaptureBagCounts(out var inventoryReady);
        if (!inventoryReady)
        {
            _log.Warning("[RR][Prune] Skipped: inventory containers are not fully loaded ({Reason}).", reason);
            return SellListInventoryPruneResult.Skipped(reason, snapshot);
        }

        return Run(reason, cid, snapshot, saveConfig);
    }

    public SellListInventoryPruneResult Run(
        string reason,
        ulong cid,
        IReadOnlyDictionary<ulong, int> bagCounts,
        bool saveConfig = true)
    {
        var removedEntries = new List<SellListInventoryPruneResult.PrunedEntry>();

        foreach (var entry in _config.GetSellListOrdered(cid).ToList())
        {
            if (entry.PreserveFromAutoPrune)
                continue;

            // Entries created before character memberships existed remain shared until manually changed.
            if (!entry.IsExplicitlyAssignedToCharacter(cid))
                continue;

            var key = Configuration.MakeSellKey(entry.ItemId, entry.IsHq);
            var inventoryCount = bagCounts.TryGetValue(key, out var count) ? count : 0;

            if (inventoryCount > 0)
                continue;

            if (_config.RemoveSellItem(entry.ItemId, entry.IsHq, cid))
            {
                removedEntries.Add(new SellListInventoryPruneResult.PrunedEntry
                {
                    ItemId = entry.ItemId,
                    IsHq = entry.IsHq,
                    Name = entry.Name ?? string.Empty,
                });
            }
        }

        if (removedEntries.Count > 0 && saveConfig)
            _config.Save();

        var result = new SellListInventoryPruneResult(
            reason,
            DateTime.UtcNow,
            removedEntries,
            bagCounts);

        if (removedEntries.Count > 0)
        {
            _log.Information(
                "[RR][Prune] Removed {Removed} character assignment(s) missing from bags ({Reason}, cid={Cid}).",
                removedEntries.Count,
                reason,
                cid);
        }
        else
        {
            _log.Verbose("[RR][Prune] No sell-list entries removed ({Reason}).", reason);
        }

        return result;
    }

    private static Dictionary<ulong, int> CaptureBagCounts(out bool inventoryReady)
    {
        inventoryReady = false;
        var counts = new Dictionary<ulong, int>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return counts;

        foreach (var type in SellScanContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return counts;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null)
                    continue;

                if (slot->Quantity <= 0)
                    continue;

                var baseItemId = slot->GetBaseItemId();
                if (baseItemId == 0)
                    continue;

                var isHq = slot->IsHighQuality();
                var key = Configuration.MakeSellKey(baseItemId, isHq);
                var slotQuantity = (int)slot->Quantity;

                if (counts.TryGetValue(key, out var existing))
                {
                    var total = existing + slotQuantity;
                    counts[key] = total >= 999999 ? 999999 : total;
                }
                else
                {
                    counts[key] = slotQuantity;
                }
            }
        }

        inventoryReady = true;
        return counts;
    }

    internal sealed class SellListInventoryPruneResult
    {
        public SellListInventoryPruneResult(
            string reason,
            DateTime completedUtc,
            IReadOnlyList<PrunedEntry> removedEntries,
            IReadOnlyDictionary<ulong, int> inventorySnapshot)
        {
            Reason = reason;
            CompletedUtc = completedUtc;
            RemovedEntries = removedEntries ?? Array.Empty<PrunedEntry>();
            InventorySnapshot = inventorySnapshot ?? new Dictionary<ulong, int>();
        }

        public string Reason { get; }
        public DateTime CompletedUtc { get; }
        public IReadOnlyList<PrunedEntry> RemovedEntries { get; }
        public IReadOnlyDictionary<ulong, int> InventorySnapshot { get; }
        public bool ItemsRemoved => RemovedEntries.Count > 0;
        public int RemovedCount => RemovedEntries.Count;
        public bool IsSkipped { get; private init; }

        public static SellListInventoryPruneResult Skipped(
            string reason,
            IReadOnlyDictionary<ulong, int>? inventorySnapshot = null)
            => new(reason, DateTime.UtcNow, Array.Empty<PrunedEntry>(), inventorySnapshot ?? new Dictionary<ulong, int>())
            {
                IsSkipped = true,
            };

        internal sealed class PrunedEntry
        {
            public uint ItemId { get; init; }
            public bool IsHq { get; init; }
            public string Name { get; init; } = string.Empty;
        }
    }
}
