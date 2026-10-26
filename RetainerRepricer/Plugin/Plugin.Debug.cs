using System;
using System.Linq;

using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Debug helpers invoked by the button-driven Debug Window.
/// </summary>
public unsafe sealed partial class Plugin
{
    private const int ContextMenuEntryValueOffset = 8;
    private const int MaxContextMenuEntriesToDump = 64;
    private const int MaxContextMenuValuesToDump = 64;
    private static readonly TimeSpan ContextMenuCaptureTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContextMenuOpenTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ContextMenuTargetFreshness = TimeSpan.FromSeconds(5);

    private readonly record struct ContextMenuDebugTarget(
        string MenuType,
        string TargetType,
        uint ItemId,
        bool IsHq,
        int ContainerType,
        int Slot,
        DateTime SeenUtc);

    private ContextMenuDebugTarget? _lastContextMenuDebugTarget;
    private DateTime _contextMenuDebugCaptureExpiresUtc;
    private DateTime _contextMenuDebugOpenExpiresUtc;
    private bool _contextMenuDebugCaptureArmed;
    private bool _contextMenuDebugDumpPending;
    private string _contextMenuDebugDumpStatus = "Idle";

    internal string ContextMenuDebugDumpStatus => _contextMenuDebugDumpStatus;
    internal bool CanCancelContextMenuDebugDump => _contextMenuDebugCaptureArmed || _contextMenuDebugDumpPending;

    internal void RequestOpenContextMenuDebugDump()
    {
        _contextMenuDebugCaptureArmed = false;
        _contextMenuDebugDumpPending = true;
        _contextMenuDebugOpenExpiresUtc = DateTime.UtcNow + ContextMenuOpenTimeout;
        _contextMenuDebugDumpStatus = "Waiting for open ContextMenu...";
    }

    internal void ArmNextContextMenuDebugDump()
    {
        _contextMenuDebugDumpPending = false;
        _contextMenuDebugCaptureArmed = true;
        _contextMenuDebugCaptureExpiresUtc = DateTime.UtcNow + ContextMenuCaptureTimeout;
        _contextMenuDebugDumpStatus = "Armed — right-click an inventory item (30s timeout).";
        Log.Information("[RR][ContextDump] Armed one-shot context-menu capture for 30 seconds.");
    }

    internal void CancelContextMenuDebugDump()
    {
        _contextMenuDebugCaptureArmed = false;
        _contextMenuDebugDumpPending = false;
        _contextMenuDebugDumpStatus = "Capture cancelled.";
        Log.Information("[RR][ContextDump] Capture cancelled.");
    }

    internal void NotifyContextMenuOpened(
        string menuType,
        string targetType,
        uint itemId,
        bool isHq,
        int containerType,
        int slot)
    {
        var now = DateTime.UtcNow;
        _lastContextMenuDebugTarget = new ContextMenuDebugTarget(
            menuType, targetType, itemId, isHq, containerType, slot, now);
        if (!_contextMenuDebugCaptureArmed || itemId == 0)
            return;

        _contextMenuDebugCaptureArmed = false;
        _contextMenuDebugDumpPending = true;
        _contextMenuDebugOpenExpiresUtc = now + ContextMenuOpenTimeout;
        _contextMenuDebugDumpStatus = "Context menu opened; capturing...";
    }

    internal void TickContextMenuDebugDump()
    {
        var now = DateTime.UtcNow;
        if (_contextMenuDebugCaptureArmed && now >= _contextMenuDebugCaptureExpiresUtc)
        {
            _contextMenuDebugCaptureArmed = false;
            _contextMenuDebugDumpStatus = "Capture timed out.";
            Log.Warning("[RR][ContextDump] Armed capture timed out before a context menu opened.");
        }

        if (!_contextMenuDebugDumpPending)
            return;

        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull)
        {
            if (now < _contextMenuDebugOpenExpiresUtc)
                return;

            FinishContextMenuDebugDumpUnavailable("ContextMenu addon not found.");
            return;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
        {
            if (now < _contextMenuDebugOpenExpiresUtc)
                return;

            FinishContextMenuDebugDumpUnavailable("ContextMenu addon is not visible.");
            return;
        }

        if (unit->AtkValues == null || unit->AtkValuesCount <= 0 ||
            unit->AtkValues[0].Type != AtkValueType.UInt || unit->AtkValues[0].UInt == 0)
        {
            if (now < _contextMenuDebugOpenExpiresUtc)
                return;

            FinishContextMenuDebugDumpUnavailable("ContextMenu opened but did not populate any entries.");
            return;
        }

        _contextMenuDebugDumpPending = false;
        try
        {
            DumpContextMenuSnapshot(unit, addon.Address);
            _contextMenuDebugDumpStatus = "Captured; see Logs.";
        }
        catch (Exception ex)
        {
            _contextMenuDebugDumpStatus = "Capture failed; see Logs.";
            Log.Warning(ex, "[RR][ContextDump] Failed reading ContextMenu snapshot.");
        }
    }

    private void FinishContextMenuDebugDumpUnavailable(string reason)
    {
        _contextMenuDebugDumpPending = false;
        _contextMenuDebugDumpStatus = "Unavailable; see Logs.";
        Log.Warning("[RR][ContextDump] Capture unavailable: {Reason}", reason);
    }

    private void DumpContextMenuSnapshot(AtkUnitBase* unit, nint addonAddress)
    {
        var target = TryGetContextMenuTargetFromAgent(out var agentTarget)
            ? agentTarget
            : _lastContextMenuDebugTarget is { } latest &&
              DateTime.UtcNow - latest.SeenUtc <= ContextMenuTargetFreshness
                ? latest
                : default;

        var slotResolved = false;
        var quantity = 0;
        var containerLabel = "unknown";
        if (target.ItemId != 0 && target.Slot >= 0 && Enum.IsDefined((InventoryType)target.ContainerType) &&
            _uiReader.TryGetInventorySlot((InventoryType)target.ContainerType, target.Slot, out var inventorySlot))
        {
            slotResolved = true;
            quantity = (int)inventorySlot->Quantity;
            containerLabel = ((InventoryType)target.ContainerType).ToString();
        }

        Log.Information(
            "[RR][ContextDump] item={ItemId} hq={IsHq} container={Container} slot={Slot} quantity={Quantity} slotResolved={SlotResolved} menuType={MenuType} targetType={TargetType}",
            target.ItemId, target.IsHq, containerLabel, target.ItemId == 0 ? -1 : target.Slot, quantity, slotResolved,
            string.IsNullOrEmpty(target.MenuType) ? "unknown" : target.MenuType,
            string.IsNullOrEmpty(target.TargetType) ? "unknown" : target.TargetType);

        var valueCount = (int)unit->AtkValuesCount;
        var values = unit->AtkValues;
        if (values == null || valueCount <= 0)
        {
            Log.Warning("[RR][ContextDump] ContextMenu has no readable AtkValues (count={ValueCount}).", valueCount);
            return;
        }

        if (values[0].Type != AtkValueType.UInt)
        {
            Log.Warning("[RR][ContextDump] ContextMenu entry count has unexpected type {Type}; action mapping unresolved.", values[0].Type);
            return;
        }
        var count = values[0].UInt;
        Log.Information("[RR][ContextDump] addon=ContextMenu visible={Visible} valueCount={ValueCount} menuEntries={EntryCount}",
            unit->IsVisible, valueCount, count);

        var boundedRawCount = Math.Min(valueCount, MaxContextMenuValuesToDump);
        for (var i = 0; i < boundedRawCount; i++)
        {
            var value = values[i];
            switch (value.Type)
            {
                case AtkValueType.Bool:
                    Log.Information("[RR][ContextDump] rawValue index={Index} type={Type} value={Value}", i, value.Type, value.Bool);
                    break;
                case AtkValueType.Int:
                    Log.Information("[RR][ContextDump] rawValue index={Index} type={Type} value={Value}", i, value.Type, value.Int);
                    break;
                case AtkValueType.UInt:
                    Log.Information("[RR][ContextDump] rawValue index={Index} type={Type} value={Value}", i, value.Type, value.UInt);
                    break;
                case AtkValueType.Float:
                    Log.Information("[RR][ContextDump] rawValue index={Index} type={Type} value={Value}", i, value.Type, value.Float);
                    break;
                default:
                    Log.Information("[RR][ContextDump] rawValue index={Index} type={Type} value=omitted", i, value.Type);
                    break;
            }
        }
        if (valueCount > boundedRawCount)
            Log.Information("[RR][ContextDump] raw values truncated: {Shown}/{Total}", boundedRawCount, valueCount);

        var availableEntries = Math.Max(0, valueCount - ContextMenuEntryValueOffset);
        if (count > availableEntries)
        {
            Log.Warning("[RR][ContextDump] Entry count does not fit AtkValues: entries={EntryCount} available={Available}; mapping unresolved.",
                count, availableEntries);
            return;
        }

        var entriesToRead = (int)Math.Min(count, MaxContextMenuEntriesToDump);
        try
        {
            var contextMenu = new AddonMaster.ContextMenu(addonAddress);
            var entries = contextMenu.Entries;
            var rowCount = contextMenu.ListComponent == null ? 0 : contextMenu.ListItems.Count;
            Log.Information("[RR][ContextDump] renderedRows={RowCount} reportedEntries={EntryCount}", rowCount, count);
            for (var i = 0; i < entriesToRead && i < entries.Length; i++)
            {
                var entry = entries[i];
                var text = entry.Text ?? string.Empty;
                text = text.Replace('\r', ' ').Replace('\n', ' ');
                if (text.Length > 160) text = text[..160] + "…";
                var enabled = i < rowCount ? entry.Enabled.ToString() : "unavailable";
                Log.Information(
                    "[RR][ContextDump] entry displayIndex={DisplayIndex} atkValueIndex={AtkValueIndex} text=\"{Text}\" enabled={Enabled} actionMapping=unresolved",
                    i, entry.ListIndex, text, enabled);
            }
            if (count > entriesToRead)
                Log.Information("[RR][ContextDump] entries truncated: {Shown}/{Total}", entriesToRead, count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][ContextDump] Could not decode menu entry text; raw values logged and action mapping remains unresolved.");
        }
    }

    private bool TryGetContextMenuTargetFromAgent(out ContextMenuDebugTarget target)
    {
        target = default;
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        var baseAgent = agentModule->GetAgentByInternalId(AgentId.InventoryContext);
        if (baseAgent == null)
            return false;

        var agent = (AgentInventoryContext*)baseAgent;
        if (agent->IsAgentActive() == false || agent->TargetInventorySlot == null || agent->TargetInventorySlotId < 0)
            return false;

        var slot = agent->TargetInventorySlot;
        var itemId = slot->GetBaseItemId();
        if (itemId == 0 || !Enum.IsDefined(agent->TargetInventoryId))
            return false;

        target = new ContextMenuDebugTarget(
            "InventoryAgent",
            "AgentInventoryContext",
            itemId,
            slot->GetItemId() != itemId,
            (int)agent->TargetInventoryId,
            agent->TargetInventorySlotId,
            DateTime.UtcNow);
        return true;
    }

    internal void TestUniversalisGateForDebug()
    {
        if (!IsAddonOpen("ItemSearchResult"))
        {
            Log.Information("[RR][TestGate] ItemSearchResult is not open. Open market board listings first.");
            ChatGui.Print("[RetainerRepricer] Open ItemSearchResult first (/mbdump to verify).");
            return;
        }

        if (!TryGetCurrentMarketItemId(out var itemId))
        {
            Log.Warning("[RR][TestGate] Could not resolve current item id.");
            return;
        }

        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            Log.Warning("[RR][TestGate] Could not resolve world/DC region.");
            return;
        }

        if (!Configuration.EnableUndercutPreventionGate)
        {
            Log.Information("[RR][TestGate] Gate is disabled in config. Using legacy behavior.");
        }
        else if (!Configuration.UseUniversalisApi)
        {
            Log.Information("[RR][TestGate] Universalis API is disabled in config. Using legacy behavior.");
        }

        var baseUrl = UniversalisApiClient.AggregatedBaseUrl;

        Log.Information($"[RR][TestGate] Fetching Universalis data for item {itemId}, region='{region}', HQ={_currentIsHq}");

        decimal avg;
        try
        {
            var result = _universalisClient.GetAveragePriceAsync(
                baseUrl,
                region,
                itemId,
                _currentIsHq,
                source: "test_gate");
            avg = result.GetAwaiter().GetResult() ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][TestGate] Universalis API call failed.");
            avg = 0;
        }

        if (avg <= 0)
        {
            Log.Information("[RR][TestGate] Universalis returned no data (or gate disabled). Using legacy row logic.");
            avg = 0;
        }

        var (marketAvg, marketCount) = TryGetMarketAverage();
        if (marketCount > 0 && avg > 0)
        {
            var threshold = marketAvg * (decimal)MarketValidationThreshold;
            if (avg > threshold)
            {
                Log.Information($"[RR][TestGate] API avg {avg:N0} > market avg {marketAvg:N0} * {MarketValidationThreshold:F1} = {threshold:N0}; using market avg instead.");
                avg = marketAvg;
            }
        }

        var floor = ComputeUniversalisFloor(avg, Configuration.UndercutPreventionPercent);
        var percentDisplay = Configuration.UndercutPreventionPercent * 100f;

        Log.Information("[RR][TestGate] === Universalis Results ===");
        Log.Information($"[RR][TestGate] Item ID: {itemId}");
        Log.Information($"[RR][TestGate] Region: {region}");
        Log.Information($"[RR][TestGate] API Average: {(avg > 0 ? avg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Market Average ({marketCount} listings): {(marketAvg > 0 ? marketAvg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Config Percent: {percentDisplay:0}%");
        Log.Information($"[RR][TestGate] Floor Price: {(floor > 0 ? floor.ToString("N0") : "N/A")}");
        Log.Information("[RR][TestGate] ========================");

        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Warning("[RR][TestGate] Could not get market list.");
            return;
        }

        var count = list->GetItemCount();
        if (count <= 0)
        {
            Log.Information("[RR][TestGate] No listings in market.");
            return;
        }

        var maxRows = Math.Min(count, 10);
        var selectedRow = -1;
        var selectedPrice = 0;

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out var seller, out var isHq))
                continue;

            bool passes = floor <= 0 || price >= floor;
            var status = passes ? "PASS" : "SKIP (below floor)";

            Log.Information($"[RR][TestGate] Row {i}: price={price:N0} hq={isHq} seller='{GetSellerLabelForLog(seller)}' => {status}");

            if (selectedRow < 0 && passes)
            {
                selectedRow = i;
                selectedPrice = price;
            }
        }

        if (selectedRow < 0 && floor > 0)
        {
            selectedRow = -1;
            selectedPrice = floor;
            Log.Information($"[RR][TestGate] No rows passed floor. Using floor price: {floor:N0}");
        }
        else if (selectedRow < 0)
        {
            if (TryReadMarketRow(0, out var p0, out var s0, out _))
            {
                selectedRow = 0;
                selectedPrice = p0;
                Log.Information($"[RR][TestGate] No floor set, using row 0: {p0:N0}");
            }
            else
            {
                Log.Information("[RR][TestGate] Could not read any market rows.");
                return;
            }
        }

        var undercutPrice = Math.Max(1, selectedPrice - 1);
        Log.Information("[RR][TestGate] === Final Decision ===");
        Log.Information($"[RR][TestGate] Selected Row: {(selectedRow >= 0 ? selectedRow.ToString() : "FLOOR")}");
        Log.Information($"[RR][TestGate] Selected Price: {selectedPrice:N0}");
        Log.Information($"[RR][TestGate] Undercut Price: {undercutPrice:N0}");
        Log.Information("[RR][TestGate] ========================");

        ChatGui.Print("[RetainerRepricer] TestGate complete - check log for details.");
    }

    internal void DumpMarketRowsForDebug()
    {
        if (!IsAddonOpen("ItemSearchResult"))
        {
            Log.Information("[MB] ItemSearchResult is not open. Open market board listings first.");
            return;
        }

        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Information("[MB] Market list not found. Open ItemSearchResult first.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[MB] renderer count = {count}");

        int visibleCount = 0;
        int hiddenCount = 0;
        int drawZeroCount = 0;
        int drawHundredCount = 0;
        int drawHundredTwoCount = 0;

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var seller = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
            var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
            var qtyRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

            var hqState = _uiReader.DumpHqIconState(r, i, s => Log.Information(s));
            var isHq = _uiReader.RowIsHq(r);

            if (hqState.HasValue)
            {
                var state = hqState.Value;
                if (state.Visible)
                    visibleCount++;
                else
                    hiddenCount++;

                if (state.DrawFlags == 0)
                    drawZeroCount++;

                if (state.DrawFlags == 0x100)
                    drawHundredCount++;

                if (state.DrawFlags == 0x102)
                    drawHundredTwoCount++;
            }

            var unit = Ui.UiReader.ParseGil(unitRaw);
            var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

            Log.Information($"[MB] row {i}: seller={GetSellerLabelForLog(seller ?? string.Empty)} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
        }

        Log.Information($"[MB][HQ] summary: visible={visibleCount} hidden={hiddenCount} draw0={drawZeroCount} draw100={drawHundredCount} draw102={drawHundredTwoCount}");
    }

    internal void DumpRetainerRowsForDebug()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            Log.Information("[RL] RetainerList not found. Open the summoning bell RetainerList.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[RL] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            Log.Information($"[RL] row {i}: {GetRetainerLabelForLog(i)}");
        }
    }

    internal void DumpInventoryByItemIdForDebug(uint itemId)
    {
        var itemRow = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        Log.Information("[INV][Dump] Searching all inventory slots for itemId={ItemId}.", itemId);

        if (!itemRow.HasValue)
        {
            Log.Warning("[INV][Dump] Item sheet row not found for itemId={ItemId}.", itemId);
            return;
        }

        Log.Information("[INV][Dump] Sheet: name='{Name}' isUntradable={IsUntradable}.",
            itemRow.Value.Name.ToString(), itemRow.Value.IsUntradable);

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Log.Warning("[INV][Dump] InventoryManager is unavailable.");
            return;
        }

        var containers = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.Crystals,
        };

        var matchCount = 0;
        foreach (var containerType in containers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->Quantity <= 0 || slot->GetBaseItemId() != itemId)
                    continue;

                matchCount++;
                var flags = slot->GetFlags();
                var spiritbondOrCollectability = slot->GetSpiritbondOrCollectability();
                var currentSellable = Ui.UiReader.IsInventorySlotSellable(slot, itemRow);
                var reason = GetInventorySellabilityReason(slot, itemRow);

                Log.Information(
                    "[INV][Dump] container={Container}({ContainerId}) slot={Slot} rawItemId={RawItemId} baseItemId={BaseItemId} fullItemId={FullItemId} quantity={Quantity} flags=0x{Flags:X2}({FlagNames}) hq={Hq} collectable={Collectable} collectability={Collectability} spiritbondOrCollectability={SpiritbondOrCollectability} condition={Condition} conditionPercent={ConditionPercent} symbolic={Symbolic} linkedContainer={LinkedContainer} linkedSlot={LinkedSlot} glamourId={GlamourId} crafterContentId={CrafterContentId} eventId={EventId} pluginSellable={Sellable} reason={Reason}",
                    containerType,
                    (int)containerType,
                    slotIndex,
                    slot->ItemId,
                    slot->GetBaseItemId(),
                    slot->GetItemId(),
                    slot->Quantity,
                    (byte)flags,
                    flags,
                    slot->IsHighQuality(),
                    slot->IsCollectable(),
                    slot->GetCollectability(),
                    spiritbondOrCollectability,
                    slot->Condition,
                    slot->GetConditionPercentage(),
                    slot->IsSymbolic,
                    slot->LinkedInventoryType,
                    slot->LinkedItemSlot,
                    slot->GetGlamourId(),
                    slot->GetCrafterContentId(),
                    slot->EventId,
                    currentSellable,
                    reason);

                for (byte materiaIndex = 0; materiaIndex < 5; materiaIndex++)
                {
                    Log.Information("[INV][Dump] container={Container} slot={Slot} materia[{Index}] id={MateriaId} grade={Grade}",
                        containerType,
                        slotIndex,
                        materiaIndex,
                        slot->GetMateriaId(materiaIndex),
                        slot->GetMateriaGrade(materiaIndex));
                }

                Log.Information("[INV][Dump] container={Container} slot={Slot} stain[0]={Stain0} stain[1]={Stain1}",
                    containerType,
                    slotIndex,
                    slot->GetStain(0),
                    slot->GetStain(1));
            }
        }

        Log.Information("[INV][Dump] Completed itemId={ItemId}; matchingSlots={MatchCount}.", itemId, matchCount);
    }

    internal void DumpInventoryGridForDebug(uint itemId)
    {
        Log.Information("[INV][Grid] Dumping visible renderer candidates for itemId={ItemId}.", itemId);
        _uiReader.DumpInventoryGridState(itemId, message => Log.Information(message));
        Log.Information("[INV][Grid] Targeted InventoryGrid dump complete for itemId={ItemId}.", itemId);
    }

    private static string GetInventorySellabilityReason(InventoryItem* slot, Item? itemRow)
    {
        if (slot == null)
            return "null_slot";

        if (!itemRow.HasValue)
            return "missing_item_sheet";

        if (itemRow.Value.IsUntradable)
            return "item_sheet_untradable";

        if (!slot->IsCollectable() && slot->GetSpiritbondOrCollectability() != 0)
            return "spiritbond_or_collectability_nonzero";

        return "passes_current_filter";
    }
}
