using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Windows;
using System;
using System.Collections.Generic;

namespace RetainerRepricer;

public sealed class Plugin : IDalamudPlugin
{
    // =========================================================
    // Dalamud Services
    // =========================================================
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    // =========================================================
    // Constants / Fields
    // =========================================================
    private const string CommandName = "/repricer";
    private const double ActionIntervalSeconds = 0.25;     // pacing between actions (safe while testing)
    private const double RetainerSyncIntervalSeconds = 2.0;
    private const int UndercutAmount = 1;

    // Throttle handling (ItemSearchResult: "Please wait and try your search again")
    private const double ItemSearchResultThrottleBackoffSeconds = 5.0;

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("RetainerRepricer");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }
    private ContextMenuManager ContextMenu { get; }

    private readonly Ui.UiReader _ui;

    // =========================================================
    // Run State (Retainer cycling)
    // =========================================================
    internal bool IsRunning;

    private enum RunPhase
    {
        Idle,

        // Retainer navigation
        NeedOpen,                         // ready to click next retainer row in RetainerList
        WaitingTalk,                      // waiting for Talk after clicking a retainer; click to advance
        WaitingSelectString,              // waiting for SelectString after Talk closes
        WaitingRetainerSellList,          // waiting for RetainerSellList after choosing Sell items (first entry for retainer)

        // Selling pipeline (NEW listings from player inventory) - now runs AFTER repricing
        Sell_FindNextItemInInventory,      // iterate SellList -> find item in inventory (capacity-bounded)
        Sell_OpenRetainerSellFromInventory,// open RetainerSell by selecting inventory slot

        // Repricing listed items
        OpeningSellItem,                  // open current slot in RetainerSellList
        WaitingRetainerSell,              // wait for RetainerSell to appear (and clear ContextMenu if needed)

        // Repricing pipeline
        CaptureSellContext,               // read HQ flag + current asking price from RetainerSell
        OpenComparePrices,                // click Compare Prices (opens ItemSearchResult)
        WaitingItemSearchResult,          // wait until ItemSearchResult is visible/ready
        ReadMarketAndApplyPrice,          // read market -> stage desired price -> close market windows
        CloseMarketThenApply,             // wait for market close -> apply staged price via callback
        ConfirmAfterApply,                // wait for UI reflect -> confirm via callback

        CleanupAfterItem,                 // exit RetainerSell
        WaitingRetainerSellListAfterItem, // wait until back in RetainerSellList, then advance slot or move phases

        ExitToRetainerList,               // auto unwind: RetainerSellList -> SelectString -> Talk -> RetainerList
    }

    private RunPhase _phase = RunPhase.Idle;

    private readonly List<int> _runOrder = new(); // row indices (0-based)
    private int _runPos = -1;                     // current position into _runOrder

    private DateTime _lastActionUtc = DateTime.MinValue;

    // Per-retainer item loop state
    private bool _sellListCountCaptured;
    private int _listedCountThisRetainer;
    private int _slotIndexToOpen; // 0-based slot cursor

    // =========================================================
    // Selling (NEW listings) state (AFTER repricing)
    // =========================================================
    private readonly List<uint> _sellQueue = new(); // SellList ItemIds (per-retainer pass)
    private int _sellQueuePos = 0;

    // Capacity limiting for selling
    private int _sellCapacityThisRetainer = 0;      // max new listings allowed this retainer (20 - listedCount at entry)
    private int _soldThisRetainer = 0;              // number of new listings completed this retainer

    private bool _processingListedItem = true; // true = repricing existing listing; false = selling new listing
    private uint _currentSellItemId = 0;

    // The inventory slot we intend to click to open RetainerSell for a new listing
    private InventorySlotRef _pendingSellSlot;
    private bool _hasPendingSellSlot;

    // Store retainer names as a set for fast lookup
    private readonly HashSet<string> _myRetainers = new(StringComparer.Ordinal);

    // Per-item context captured from RetainerSell
    private bool _currentIsHq;

    // "Staged" action (decided from market, applied only after market windows close)
    private int? _stagedDesiredPrice;
    private string _stagedReferenceSeller = string.Empty;
    private bool _stagedReferenceIsMine;
    private bool _hasAppliedStagedPrice;

    // ItemSearchResult throttle handling (one retry policy)
    private bool _isrThrottleRetried;
    private DateTime _isrThrottleUntilUtc = DateTime.MinValue;

    // Retainer name sync -> Configuration
    private DateTime _lastRetainerSyncUtc = DateTime.MinValue;

    // Framework tick
    private DateTime _lastFrameworkTickUtc = DateTime.MinValue;
    private const double FrameworkTickIntervalSeconds = 0.10; // outer throttle; TickRun has its own pacing too

    // =========================================================
    // Lifecycle
    // =========================================================
    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commandManager,
        IPluginLog log,
        IGameGui gameGui)
    {
        PluginInterface = pi;
        CommandManager = commandManager;
        Log = log;
        GameGui = gameGui;

        ECommonsMain.Init(pi, this);

        Configuration = pi.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pi);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        ContextMenu = new ContextMenuManager(Configuration);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        _ui = new Ui.UiReader(GameGui);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "help | start | stop | config"
        });

        pi.UiBuilder.Draw += WindowSystem.Draw;
        pi.UiBuilder.OpenConfigUi += ToggleConfigUi;
        pi.UiBuilder.OpenMainUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"[{pi.Manifest.Name}] loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Framework.Update -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        ContextMenu.Dispose();

        CommandManager.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }

    // =========================================================
    // Commands
    // =========================================================
    private unsafe void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        switch (a)
        {
            case "":
                PrintHelp();
                return;

            case "start":
                StartRun();
                return;

            case "stop":
                if (IsRunning)
                    StopRun();
                return;

            case "config":
                ToggleConfigUi();
                return;

            case "count":
                {
                    var raw = _ui.ReadRetainerSellListCountText();
                    var count = _ui.ReadRetainerSellListListedCount();
                    if (count == null)
                    {
                        ChatGui.Print("[RetainerRepricer] Open RetainerSellList first.");
                        return;
                    }

                    ChatGui.Print($"[RetainerRepricer] Listed = {count} (raw='{raw}')");
                    return;
                }

            case "mbdump":
                DumpMarketRows();
                return;

            case "rldump":
                DumpRetainerRows();
                return;

            case "help":
                PrintHelp();
                return;

            default:
                PrintHelp();
                return;
        }
    }


    // =========================================================
    // UI navigation (ECommons AddonMaster wrappers)
    // =========================================================
    private unsafe bool ClickRetainerByIndex(int index)
    {
        var addon = GameGui.GetAddonByName("RetainerList", 1);
        if (addon.IsNull) return false;

        try
        {
            var rl = new AddonMaster.RetainerList(addon.Address);
            var retainers = rl.Retainers;

            if (index < 0 || index >= retainers.Length) return false;

            var ok = retainers[index].Select();
            Log.Information(ok
                ? $"[RL] ECommons RetainerList select index={index}"
                : $"[RL] ECommons RetainerList entry inactive index={index}");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RL] ECommons RetainerList select failed.");
            return false;
        }
    }

    private unsafe bool ClickSelectStringSellItems()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        const int sellItemsIndex = 2; // 0-based: third entry

        try
        {
            var ss = new AddonMaster.SelectString(addon.Address);
            if (sellItemsIndex < 0 || sellItemsIndex >= ss.EntryCount) return false;

            ss.Entries[sellItemsIndex].Select();
            Log.Information($"[SS] ECommons SelectString select index={sellItemsIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SS] ECommons SelectString select failed.");
            return false;
        }
    }

    private unsafe bool ClickTalkECommons()
    {
        var addon = GameGui.GetAddonByName("Talk", 1);
        if (addon.IsNull)
        {
            Log.Warning("[Talk] Talk addon not open.");
            return false;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
        {
            Log.Warning("[Talk] Talk addon not visible.");
            return false;
        }

        new AddonMaster.Talk(addon.Address).Click();
        Log.Information("[Talk] ECommons Talk click");
        return true;
    }

    private unsafe bool FireRetainerSellList_OpenItem(int slotIndex0)
    {
        var addon = GameGui.GetAddonByName("RetainerSellList", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, slotIndex0, 1);
        Log.Information($"[RSL] FireCallback open item: (0, {slotIndex0}, 1)");
        return true;
    }

    private unsafe bool FireContextMenuDismiss()
    {
        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, 0);
        Callback.Fire(unit, updateState: true, 1, 0);

        Log.Information("[CTX] FireCallback dismiss tried: (0,0) then (1,0)");
        return true;
    }

    // =========================================================
    // Helpers
    // =========================================================
    private static int DecideNewPrice(int lowestPrice, bool sellerIsMine)
    {
        if (sellerIsMine) return lowestPrice;
        var v = lowestPrice - UndercutAmount;
        return v < 1 ? 1 : v;
    }

    // =========================================================
    // Inventory selling helpers (wired to UiReader)
    // =========================================================
    private struct InventorySlotRef
    {
        public int Container;
        public int Slot;
    }

    /// <summary>
    /// Find an itemId in player inventory and return (container, slot).
    /// Uses UiReader inventory scan (InventoryManager).
    /// </summary>
    private bool TryFindItemInInventory(uint itemId, out InventorySlotRef slotRef)
    {
        slotRef = default;

        // UiReader returns container/slot as ints
        if (_ui.TryFindItemInInventory(itemId, out var container, out var slot))
        {
            slotRef = new InventorySlotRef
            {
                Container = container,
                Slot = slot,
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Click InventoryGrid slot to open RetainerSell for a NEW listing.
    /// Uses UiReader callback fire against InventoryGrid.
    /// </summary>
    private unsafe bool TryOpenRetainerSellFromInventory(InventorySlotRef slotRef)
    {
        // IMPORTANT: InventoryGrid must be visible/open for this to work.
        // If it isn't, this will return false and we skip this sell attempt.
        return _ui.TryOpenRetainerSellFromInventory(slotRef.Container, slotRef.Slot);
    }

    private unsafe bool IsAddonVisible(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return false;
        var u = (AtkUnitBase*)a.Address;
        return u != null && u->IsVisible;
    }

    private unsafe bool IsAddonOpen(string name)
        => !GameGui.GetAddonByName(name, 1).IsNull;

    private unsafe void CloseAddonIfOpen(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return;

        var u = (AtkUnitBase*)a.Address;
        if (u == null || !u->IsVisible) return;

        Callback.Fire(u, updateState: true, -1);
    }

    private unsafe void CloseMarketWindows()
    {
        CloseAddonIfOpen("ItemHistory");
        CloseAddonIfOpen("ItemSearchResult");
    }

    private unsafe bool MarketWindowsStillOpen()
        => IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemHistory");

    private unsafe void CloseRetainerSellIfOpen()
    {
        var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
        if (!sellAddon.IsNull)
        {
            new AddonMaster.RetainerSell(sellAddon.Address).Cancel();
            return;
        }

        CloseAddonIfOpen("RetainerSell");
    }

    private unsafe bool TryReadMarketRow(int rowIndex, out int unitPrice, out string seller, out bool isHq)
    {
        unitPrice = 0;
        seller = string.Empty;
        isHq = false;

        var list = _ui.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0 || rowIndex < 0 || rowIndex >= count) return false;

        var r = list->GetItemRenderer(rowIndex);
        if (r == null) return false;

        isHq = _ui.RowIsHq(r);

        var unitRaw = _ui.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
        var sellerRaw = _ui.ReadRendererText(r, Ui.NodePaths.SellerNodeId);

        var parsed = Ui.UiReader.ParseGil(unitRaw);
        if (parsed == null || parsed.Value <= 0) return false;

        unitPrice = parsed.Value;
        seller = (sellerRaw ?? string.Empty).Trim('\'', ' ');

        return true;
    }

    private unsafe bool TryPickReferenceListing(out int lowestPrice, out string lowestSeller)
    {
        lowestPrice = 0;
        lowestSeller = string.Empty;

        var list = _ui.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        if (_currentIsHq)
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryReadMarketRow(i, out var price, out var seller, out var isHq)) continue;
                if (!isHq) continue;

                Log.Information($"[RR] Market HQ match row={i} price={price} seller='{seller}'");
                lowestPrice = price;
                lowestSeller = seller;
                return true;
            }

            Log.Warning("[RR] HQ item but no HQ rows found; skipping repricing.");
            return false;
        }

        // NQ: always row 0
        if (!TryReadMarketRow(0, out var p0, out var s0, out _)) return false;

        Log.Information($"[RR] Market NQ row0 price={p0} seller='{s0}'");
        lowestPrice = p0;
        lowestSeller = s0;
        return true;
    }

    // =========================================================
    // Debug: dump market/retainer list rows
    // =========================================================
    private unsafe void DumpMarketRows()
    {
        var list = _ui.GetMarketList();
        if (list == null)
        {
            Log.Warning("[MB] Market list not found. Make sure ItemSearchResult is open and populated.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[MB] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var seller = _ui.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
            var unitRaw = _ui.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
            var qtyRaw = _ui.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

            _ui.DumpHqIconState(r, i, s => Log.Information(s));
            var isHq = _ui.RowIsHq(r);

            var unit = Ui.UiReader.ParseGil(unitRaw);
            var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

            Log.Information($"[MB] row {i}: seller={seller} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
        }
    }

    private unsafe void DumpRetainerRows()
    {
        var list = _ui.GetRetainerList();
        if (list == null)
        {
            Log.Warning("[RL] RetainerList not found. Open the summoning bell RetainerList.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[RL] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _ui.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            Log.Information($"[RL] row {i}: name='{name}'");
        }
    }

    // =========================================================
    // Retainer list
    // =========================================================
    internal unsafe List<string> ReadRetainerNames()
    {
        var names = new List<string>();

        var list = _ui.GetRetainerList();
        if (list == null) return names;

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _ui.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            names.Add(name.Trim('\'', ' '));
        }

        return names;
    }

    internal void SyncRetainersIntoConfig(IEnumerable<string> names)
    {
        var changed = false;

        foreach (var n in names)
        {
            if (!Configuration.RetainersEnabled.ContainsKey(n))
            {
                Configuration.RetainersEnabled[n] = true;
                changed = true;
            }
        }

        if (changed) Configuration.Save();
    }

    internal unsafe void TrySyncRetainersThrottled()
    {
        var rl = GameGui.GetAddonByName("RetainerList", 1);
        if (rl.IsNull) return;

        var now = DateTime.UtcNow;
        if ((now - _lastRetainerSyncUtc).TotalSeconds < RetainerSyncIntervalSeconds)
            return;

        var names = ReadRetainerNames();
        SyncRetainersIntoConfig(names);

        _lastRetainerSyncUtc = now;
    }

    internal void RebuildMyRetainersSet()
    {
        _myRetainers.Clear();
        foreach (var kvp in Configuration.RetainersEnabled)
            _myRetainers.Add(kvp.Key);
    }

    // =========================================================
    // Run Controls
    // =========================================================
    internal unsafe void StartRunFromRetainerList()
    {
        var list = _ui.GetRetainerList();
        if (list == null)
        {
            Log.Warning("[RR] Can't start: RetainerList not open.");
            return;
        }

        RebuildMyRetainersSet();

        _runOrder.Clear();
        _runPos = -1;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        // selling state
        _sellQueue.Clear();
        _sellQueuePos = 0;
        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;

        _processingListedItem = true;
        _currentSellItemId = 0;
        _hasPendingSellSlot = false;

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _ui.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            if (Configuration.IsRetainerEnabled(name))
                _runOrder.Add(i);
        }

        if (_runOrder.Count == 0)
        {
            Log.Warning("[RR] No enabled retainers found.");
            StopRun();
            return;
        }

        IsRunning = true;
        _phase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Start: enabled rows = {string.Join(",", _runOrder)}");
    }

    internal void StopRun()
    {
        IsRunning = false;
        _phase = RunPhase.Idle;

        _runOrder.Clear();
        _runPos = -1;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        _sellQueuePos = 0;
        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;

        _processingListedItem = true;
        _currentSellItemId = 0;
        _hasPendingSellSlot = false;

        Log.Information("[RR] Stopped.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastFrameworkTickUtc).TotalSeconds < FrameworkTickIntervalSeconds)
            return;

        TickRun();
        _lastFrameworkTickUtc = now;
    }
    private unsafe void StartRun()
    {
        if (IsRunning)
        {
            Log.Warning("[RR] Already running.");
            return;
        }

        if (!IsAddonVisible("RetainerList"))
        {
            Log.Warning("[RR] Cannot start: RetainerList is not visible. Open a summoning bell first.");
            return;
        }

        RebuildMyRetainersSet();

        _runOrder.Clear();
        _runPos = -1;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        _sellQueuePos = 0;
        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;

        _processingListedItem = true;
        _currentSellItemId = 0;
        _hasPendingSellSlot = false;

        var list = _ui.GetRetainerList();
        if (list == null)
        {
            Log.Warning("[RR] RetainerList not readable.");
            return;
        }

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _ui.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            if (Configuration.IsRetainerEnabled(name))
                _runOrder.Add(i);
        }

        if (_runOrder.Count == 0)
        {
            Log.Warning("[RR] No enabled retainers found.");
            return;
        }

        IsRunning = true;
        _phase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Started. Enabled retainers: {string.Join(",", _runOrder)}");
    }


    // =========================================================
    // Run State Machine Tick
    // =========================================================
    internal unsafe void TickRun()
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastActionUtc).TotalSeconds < ActionIntervalSeconds)
            return;

        // Snapshot addon visibility once per tick (cheaper + consistent)
        var retainerListVisible = IsAddonVisible("RetainerList");
        var talkVisible = IsAddonVisible("Talk");
        var selectStringVisible = IsAddonVisible("SelectString");
        var retainerSellListVisible = IsAddonVisible("RetainerSellList");
        var retainerSellVisible = IsAddonVisible("RetainerSell");
        var marketOpen = IsAddonOpen("ItemSearchResult");
        var itemHistoryOpen = IsAddonOpen("ItemHistory");
        var contextMenuVisible = IsAddonVisible("ContextMenu");

        // ---------------------------------------------------------
        // Local: SelectString "ready gate" (Option A)
        // Require: addon visible + EntryCount > 2 (Sell items is index 2)
        // ---------------------------------------------------------
        bool IsSelectStringReady()
        {
            var addon = GameGui.GetAddonByName("SelectString", 1);
            if (addon.IsNull) return false;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible) return false;

            try
            {
                var ss = new AddonMaster.SelectString(addon.Address);
                return ss.EntryCount > 2;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------
        // Local: RetainerList readiness gate (name-based)
        // Require: list exists + has at least 1 non-empty name
        // ---------------------------------------------------------
        bool IsRetainerListReady()
        {
            var list = _ui.GetRetainerList();
            if (list == null) return false;

            var count = list->GetItemCount();
            if (count <= 0) return false;

            for (int i = 0; i < count; i++)
            {
                var r = list->GetItemRenderer(i);
                if (r == null) continue;

                var name = _ui.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
                if (!string.IsNullOrWhiteSpace(name))
                    return true;
            }

            return false;
        }

        switch (_phase)
        {
            // =========================================================
            // Retainer navigation
            // =========================================================
            case RunPhase.NeedOpen:
                {
                    if (!retainerListVisible) return;

                    // NEW: RetainerList readiness gate (names must be populated)
                    if (!IsRetainerListReady())
                    {
                        Log.Information("[RR] RetainerList visible but not ready (names not populated). Waiting...");
                        _lastActionUtc = now;
                        return;
                    }

                    _runPos++;
                    if (_runPos >= _runOrder.Count)
                    {
                        Log.Information("[RR] Done: processed all enabled retainers.");
                        ChatGui.Print("[RetainerRepricer] Repricing has finished.");

                        // optionally close RetainerList at end of run
                        if (Configuration.CloseRetainerListAddon)
                        {
                            Log.Information("[RR] CloseRetainerListAddon enabled: closing RetainerList.");
                            CloseAddonIfOpen("RetainerList");
                        }

                        StopRun();
                        return;
                    }

                    // reset per-retainer loop state
                    _sellListCountCaptured = false;
                    _listedCountThisRetainer = 0;
                    _slotIndexToOpen = 0;

                    // selling state (per retainer) - runs after repricing
                    _sellQueue.Clear();
                    foreach (var e in Configuration.GetSellListSorted())
                    {
                        if (e.ItemId != 0)
                            _sellQueue.Add(e.ItemId);
                    }
                    _sellQueuePos = 0;

                    // capacity counters
                    _sellCapacityThisRetainer = 0;
                    _soldThisRetainer = 0;

                    _processingListedItem = true;
                    _currentSellItemId = 0;
                    _hasPendingSellSlot = false;

                    var row = _runOrder[_runPos];
                    Log.Information($"[RR] Opening retainer row {row} (pos {_runPos + 1}/{_runOrder.Count})");

                    ClickRetainerByIndex(row);

                    _phase = RunPhase.WaitingTalk;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingTalk:
                {
                    // If another plugin auto-closes Talk and SelectString is already up, skip Talk logic.
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information($"[RR] SelectString opened for row {_runOrder[_runPos]}; choosing Sell items.");
                        ClickSelectStringSellItems();

                        _phase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Information($"[RR] Talk open for row {_runOrder[_runPos]}; clicking Talk advance.");
                        var ok = ClickTalkECommons();
                        Log.Information(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _phase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information($"[RR] SelectString opened for row {_runOrder[_runPos]}; choosing Sell items.");
                        ClickSelectStringSellItems();

                        _phase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Information($"[RR] Talk open (late) for row {_runOrder[_runPos]}; clicking Talk advance.");
                        var ok = ClickTalkECommons();
                        Log.Information(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");
                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellList:
                {
                    if (!retainerSellListVisible) return;

                    // NEW: RetainerSellList readiness gate.
                    // Do NOT latch _sellListCountCaptured until we can read the count reliably.
                    if (!_sellListCountCaptured)
                    {
                        var raw = _ui.ReadRetainerSellListCountText();
                        var listedOpt = _ui.ReadRetainerSellListListedCount(); // int?

                        if (listedOpt is null || string.IsNullOrWhiteSpace(raw))
                        {
                            Log.Information($"[RR] RetainerSellList visible but not ready (raw='{raw ?? "null"}'). Waiting...");
                            _lastActionUtc = now;
                            return;
                        }

                        _sellListCountCaptured = true;

                        var listed = listedOpt.Value;
                        _listedCountThisRetainer = Math.Clamp(listed, 0, 20);
                        _slotIndexToOpen = 0;

                        _sellCapacityThisRetainer = Math.Max(0, 20 - _listedCountThisRetainer);
                        _soldThisRetainer = 0;

                        Log.Information($"[RR] Entered RetainerSellList. Listed={_listedCountThisRetainer} (raw='{raw}'); SellCapacity={_sellCapacityThisRetainer}");
                    }

                    _phase = (_listedCountThisRetainer <= 0) ? RunPhase.Sell_FindNextItemInInventory : RunPhase.OpeningSellItem;
                    _lastActionUtc = now;
                    return;
                }

            // =========================================================
            // Repricing existing listed items
            // =========================================================
            case RunPhase.OpeningSellItem:
                {
                    if (!retainerSellListVisible) return;

                    if (_slotIndexToOpen < 0) _slotIndexToOpen = 0;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _phase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    var idx = Math.Clamp(_slotIndexToOpen, 0, 19);
                    Log.Information($"[RR] Opening sell slot {idx} ({_slotIndexToOpen + 1}/{_listedCountThisRetainer})");

                    _processingListedItem = true;

                    if (!FireRetainerSellList_OpenItem(idx))
                        return;

                    _phase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSell:
                {
                    if (contextMenuVisible)
                    {
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellVisible) return;

                    Log.Information($"[RR] RetainerSell opened (processingListedItem={_processingListedItem}).");

                    _phase = RunPhase.CaptureSellContext;
                    _lastActionUtc = now;
                    return;
                }

            // =========================================================
            // Repricing pipeline
            // =========================================================
            case RunPhase.CaptureSellContext:
                {
                    if (!retainerSellVisible) return;

                    // NEW: RetainerSell readiness gate for the data we rely on (price + name).
                    var priceOpt = _ui.ReadRetainerSellAskingPrice(); // int?
                    if (priceOpt is null)
                    {
                        Log.Information("[RR] RetainerSell visible but not ready (asking price unreadable). Waiting...");
                        _lastActionUtc = now;
                        return;
                    }

                    var rawName = _ui.ReadRetainerSellItemNameRaw();
                    if (string.IsNullOrWhiteSpace(rawName))
                    {
                        Log.Information("[RR] RetainerSell visible but not ready (item name unreadable). Waiting...");
                        _lastActionUtc = now;
                        return;
                    }

                    _currentIsHq = _ui.IsRetainerSellItemHq();

                    var name = _ui.NormalizeItemName(rawName)
                        .Replace(Ui.UiReader.RetainerSell_HqGlyphChar.ToString(), string.Empty)
                        .Trim();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log.Information("[RR] RetainerSell visible but not ready (normalized name empty). Waiting...");
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Information($"[RR] Sell ctx: hq={_currentIsHq} item='{name}' currentPrice={priceOpt.Value}");

                    _phase = RunPhase.OpenComparePrices;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.OpenComparePrices:
                {
                    if (!retainerSellVisible) return;

                    _isrThrottleRetried = false;
                    _isrThrottleUntilUtc = DateTime.MinValue;

                    if (marketOpen)
                    {
                        Log.Information("[RR] ItemSearchResult already open (external plugin). Skipping ComparePrices click.");
                        _phase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    var addon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (addon.IsNull) return;

                    new AddonMaster.RetainerSell(addon.Address).ComparePrices();

                    Log.Information("[RR] RetainerSell.ComparePrices() clicked.");
                    _phase = RunPhase.WaitingItemSearchResult;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingItemSearchResult:
                {
                    if (!marketOpen) return;

                    var status = _ui.GetItemSearchResultStatus(out var msg);

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skip repricing.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        if (now < _isrThrottleUntilUtc)
                            return;

                        if (!_isrThrottleRetried && _isrThrottleUntilUtc == DateTime.MinValue)
                        {
                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            Log.Warning("[RR] ISR throttle: 'Please wait...' -> backing off 5s then retry once.");
                            return;
                        }

                        if (!_isrThrottleRetried)
                        {
                            var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                            if (sellAddon.IsNull) return;

                            new AddonMaster.RetainerSell(sellAddon.Address).ComparePrices();
                            _isrThrottleRetried = true;
                            _isrThrottleUntilUtc = DateTime.MinValue;

                            Log.Warning("[RR] ISR throttle: retry sent (Compare Prices).");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Warning("[RR] ISR throttle: still 'Please wait...' after one retry -> skip item.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Warning($"[RR] ISR message: '{msg}' -> skipping item.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var list = _ui.GetMarketList();
                    if (list == null)
                    {
                        Log.Information("[RR] ISR not ready: market list null");
                        _lastActionUtc = now;
                        return;
                    }

                    var count = list->GetItemCount();
                    if (count <= 0)
                    {
                        Log.Information("[RR] ISR not ready: 0 rows");
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Information($"[RR] ItemSearchResult ready (rows={count}).");
                    _phase = RunPhase.ReadMarketAndApplyPrice;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ReadMarketAndApplyPrice:
                {
                    if (!retainerSellVisible) return;
                    if (!marketOpen) return;

                    var status = _ui.GetItemSearchResultStatus(out var msg);

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skip repricing.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        Log.Warning("[RR] ISR throttle surfaced during read -> returning to ISR gate.");
                        _phase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Warning($"[RR] ISR message: '{msg}' -> skipping item.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!TryPickReferenceListing(out var lowestPrice, out var lowestSeller))
                    {
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var referenceIsMine =
                        !string.IsNullOrWhiteSpace(lowestSeller) &&
                        _myRetainers.Contains(lowestSeller);

                    var desired = DecideNewPrice(lowestPrice, referenceIsMine);

                    var sellAddonPeek = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddonPeek.IsNull) return;

                    var current = new AddonMaster.RetainerSell(sellAddonPeek.Address).AskingPrice;

                    if (current == desired)
                    {
                        Log.Information($"[RR] Price unchanged (already {desired}); skipping apply.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    _stagedDesiredPrice = desired;
                    _stagedReferenceSeller = lowestSeller;
                    _stagedReferenceIsMine = referenceIsMine;
                    _hasAppliedStagedPrice = false;

                    Log.Information($"[RR] Staging apply: current={current} desired={desired} seller='{lowestSeller}' mine={referenceIsMine}");

                    CloseMarketWindows();

                    _phase = RunPhase.CloseMarketThenApply;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CloseMarketThenApply:
                {
                    if (MarketWindowsStillOpen())
                        return;

                    if (!retainerSellVisible) return;

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Pending price was null; bailing to cleanup.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var current = _ui.ReadRetainerSellAskingPrice();
                    if (current is null)
                    {
                        Log.Information("[RR] Waiting RetainerSell stable (asking price unreadable)...");
                        _lastActionUtc = now;
                        return;
                    }

                    if (_hasAppliedStagedPrice)
                    {
                        if (current.Value != _stagedDesiredPrice.Value)
                        {
                            Log.Information($"[RR] Waiting price apply... ui={current.Value} desired={_stagedDesiredPrice.Value}");
                            _lastActionUtc = now;
                            return;
                        }

                        _phase = RunPhase.ConfirmAfterApply;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Information($"[RR] Apply after ISR close (safe): {current.Value} -> {_stagedDesiredPrice.Value}");

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null) return;

                    Callback.Fire(unit, updateState: true, 2, _stagedDesiredPrice.Value);

                    _hasAppliedStagedPrice = true;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ConfirmAfterApply:
                {
                    if (!retainerSellVisible) return;

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Pending price was null; bailing to cleanup.");
                        _phase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (MarketWindowsStillOpen())
                        return;

                    var desired = _stagedDesiredPrice.Value;
                    var uiPrice = _ui.ReadRetainerSellAskingPrice();
                    if (uiPrice == null || uiPrice.Value != desired)
                    {
                        Log.Information($"[RR] Waiting price apply... ui={(uiPrice?.ToString() ?? "null")} desired={desired}");
                        _lastActionUtc = now;
                        return;
                    }

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null || !unit->IsVisible) return;

                    Log.Information($"[RR] Confirm (FireCallback) price={desired} seller='{_stagedReferenceSeller}' mine={_stagedReferenceIsMine}");

                    Callback.Fire(unit, updateState: true, 0);

                    _stagedDesiredPrice = null;
                    _hasAppliedStagedPrice = false;

                    _phase = RunPhase.CleanupAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CleanupAfterItem:
                {
                    CloseMarketWindows();
                    CloseRetainerSellIfOpen();

                    Log.Information("[RR] Cleanup done (closed market + exited RetainerSell).");

                    _phase = RunPhase.WaitingRetainerSellListAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellListAfterItem:
                {
                    if (!retainerSellListVisible) return;
                    if (retainerSellVisible) return;

                    if (!_processingListedItem)
                    {
                        _processingListedItem = true;
                        _hasPendingSellSlot = false;
                        _currentSellItemId = 0;

                        _soldThisRetainer++;
                        Log.Information($"[RR] SoldThisRetainer={_soldThisRetainer}/{_sellCapacityThisRetainer}");

                        _phase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _slotIndexToOpen++;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _phase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _phase = RunPhase.OpeningSellItem;
                    _lastActionUtc = now;
                    return;
                }

            // =========================================================
            // Selling pipeline (AFTER repricing), capacity-bounded
            // =========================================================
            case RunPhase.Sell_FindNextItemInInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0)
                    {
                        Log.Information("[RR] Sell skipped: retainer is full (20/20).");
                        _phase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        Log.Information($"[RR] Sell complete: reached capacity {_soldThisRetainer}/{_sellCapacityThisRetainer}.");
                        _phase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_sellQueue.Count == 0)
                    {
                        Log.Information("[RR] Sell skipped: SellQueue empty.");
                        _phase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    while (_sellQueuePos < _sellQueue.Count)
                    {
                        var itemId = _sellQueue[_sellQueuePos];
                        _sellQueuePos++;

                        if (itemId == 0) continue;

                        if (TryFindItemInInventory(itemId, out var slotRef))
                        {
                            _currentSellItemId = itemId;
                            _pendingSellSlot = slotRef;
                            _hasPendingSellSlot = true;

                            Log.Information($"[RR] Sell candidate found in inventory: itemId={itemId} container={slotRef.Container} slot={slotRef.Slot} (cap {_soldThisRetainer}/{_sellCapacityThisRetainer})");

                            _phase = RunPhase.Sell_OpenRetainerSellFromInventory;
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information($"[RR] Sell candidate not found in inventory: itemId={itemId}");
                    }

                    Log.Information("[RR] Sell complete: no SellList items found in inventory.");
                    _phase = RunPhase.ExitToRetainerList;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.Sell_OpenRetainerSellFromInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0 || _soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        _phase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!_hasPendingSellSlot)
                    {
                        Log.Warning("[RR] Sell_Open: no pending slot; returning to Sell_FindNext.");
                        _phase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _processingListedItem = false;

                    var ok = TryOpenRetainerSellFromInventory(_pendingSellSlot);

                    if (!ok)
                    {
                        Log.Warning($"[RR] Sell_Open: failed to open RetainerSell from inventory (itemId={_currentSellItemId}); skipping.");
                        _hasPendingSellSlot = false;
                        _pendingSellSlot = default;
                        _currentSellItemId = 0;

                        _phase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Information($"[RR] Sell_Open: requested RetainerSell from inventory (itemId={_currentSellItemId}). Waiting RetainerSell...");

                    _hasPendingSellSlot = false;
                    _pendingSellSlot = default;

                    _phase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            // =========================================================
            // Exit unwind
            // =========================================================
            case RunPhase.ExitToRetainerList:
                {
                    if (marketOpen || itemHistoryOpen)
                    {
                        Log.Information("[RR] Exit: closing market windows.");
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    if (contextMenuVisible)
                    {
                        Log.Information("[RR] Exit: dismissing ContextMenu.");
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellVisible)
                    {
                        Log.Information("[RR] Exit: closing RetainerSell.");
                        CloseRetainerSellIfOpen();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellListVisible)
                    {
                        Log.Information("[RR] Exit: closing RetainerSellList.");
                        CloseAddonIfOpen("RetainerSellList");
                        _lastActionUtc = now;
                        return;
                    }

                    if (selectStringVisible)
                    {
                        Log.Information("[RR] Exit: closing SelectString.");
                        CloseAddonIfOpen("SelectString");
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Information("[RR] Exit: advancing/closing Talk.");
                        ClickTalkECommons();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerListVisible)
                    {
                        Log.Information("[RR] Exit complete: back on RetainerList; continuing to next retainer.");
                        _phase = RunPhase.NeedOpen;
                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            default:
                return;
        }
    }

    private void PrintHelp()
    {
        ChatGui.Print("[RetainerRepricer]");
        ChatGui.Print("/repricer start   - Start repricing & selling from Retainer List");
        ChatGui.Print("/repricer stop    - Stop current run");
        ChatGui.Print("/repricer config  - Open configuration window");
    }

    // =========================================================
    // Window helpers
    // =========================================================
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => ToggleConfigUi();
}
