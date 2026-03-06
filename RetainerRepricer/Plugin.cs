using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Services;
using RetainerRepricer.Windows;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RetainerRepricer;

public unsafe sealed class Plugin : IDalamudPlugin
{
    #region Dalamud services

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    #endregion

    #region Constants

    private const string CommandName = "/repricer";
    private const int UndercutAmount = 1;
    private const string UniversalisFloorSeller = "[UniversalisFloor]";
    private const float MarketValidationThreshold = 2.0f;  // Can be made configurable later

    // General pacing between UI actions. Keep conservative while iterating on UI stability.
    private const double ActionIntervalSeconds = 0.15;

    private const double RetainerSyncIntervalSeconds = 2.0;

    // ItemSearchResult throttle handling ("Please wait and try your search again")
    private const double ItemSearchResultThrottleBackoffSeconds = 1.0;

    // Market query pacing (keeps Compare Prices calls from tripping server-side throttles)
    private const double MbBaseIntervalSeconds = 1.5;
    private const double MbIntervalMinSeconds = 1.0;
    private const double MbIntervalMaxSeconds = 2.0;
    private const double MbJitterMaxSeconds = 0.10;

    // ISR settle gate: early "No items found" can flash before rows populate.
    private const double IsrNoItemsSettleSeconds = 0.5;

    // ISR HQ filter timing
    private const double IsrHqFilterInitialDelaySeconds = 1.25;   // Seconds to wait after ISR opens before attempting the HQ-filter fallback.
    private const double IsrHqFilterUiDebounceSeconds = 0.20;     // Seconds the HQ filter UI must remain visible before clicking toggle/accept (UI settle/animation debounce).
    private const double IsrHqFilterOpenRetrySeconds = 0.30;      // Seconds between attempts to open the ItemSearchFilter window if it didn’t appear.
    private const double IsrHqFilterPostOpenSeconds = 0.15;       // Seconds to wait after requesting the filter window before interacting with it.

    // Framework tick throttle (outer gate). TickRun has its own pacing too.
    private const double FrameworkTickIntervalSeconds = 0.075;

    #endregion

    #region Public/plugin-facing state

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("RetainerRepricer");

    internal bool IsRunning;

    private readonly UniversalisApiClient _universalisClient;

    #endregion

    #region Windows / UI helpers

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }
    private ContextMenuManager ContextMenu { get; }

    private readonly Ui.UiReader _uiReader;

    #endregion

    #region Run state

    private enum RunPhase
    {
        Idle,

        // Retainer navigation
        NeedOpen,                              // ready to click next retainer row in RetainerList
        WaitingTalk,                           // waiting for Talk after clicking a retainer; click to advance
        WaitingSelectString,                   // waiting for SelectString after Talk closes
        WaitingRetainerSellList,               // waiting for RetainerSellList after choosing Sell items

        // Repricing existing listed items
        OpeningSellItem,                       // open current slot in RetainerSellList
        WaitingRetainerSell,                   // wait for RetainerSell to appear (dismiss ContextMenu if it blocks)

        // Repricing pipeline (RetainerSell -> market -> apply -> confirm)
        CaptureSellContext,                    // read item HQ flag + current asking price from RetainerSell
        OpenComparePrices,                     // click Compare Prices (opens ItemSearchResult)
        WaitingItemSearchResult,               // wait until ItemSearchResult is visible and rows are ready
        ReadMarketAndApplyPrice,               // pick reference listing -> stage desired price -> close market windows
        CloseMarketThenApply,                  // wait for market windows to close, then apply staged price
        ConfirmAfterApply,                     // confirm listing after UI reflects the new price

        // Selling pipeline (new listings from inventory). This runs after repricing finishes for the retainer.
        Sell_FindNextItemInInventory,          // scan SellList -> locate next itemId in inventory (capacity-bounded)
        Sell_OpenRetainerSellFromInventory,    // click InventoryGrid slot to open RetainerSell for a new listing

        CleanupAfterItem,                      // close market windows + exit RetainerSell
        WaitingRetainerSellListAfterItem,      // wait until back in RetainerSellList, then advance to next slot/phase

        // Auto-unwind back to RetainerList between retainers (closes any leftover windows in a safe order)
        ExitToRetainerList,

    }

    private RunPhase _runPhase = RunPhase.Idle;

    // Retainer cycling order (RetainerList row indices, 0-based)
    private readonly List<int> _retainerRowOrder = new();
    private int _retainerRowPos = -1;

    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _lastFrameworkTickUtc = DateTime.MinValue;

    // Per-retainer listed item loop state
    private bool _sellListCountCaptured;
    private int _listedCountThisRetainer;
    private int _slotIndexToOpen; // 0-based cursor into RetainerSellList

    #endregion

    #region Selling (new listings) state

    private struct SellCandidate
    {
        public uint ItemId;        // base id
        public bool IsHq;          // quality
        public int MinCountToSell; // per-quality threshold
        public string Name;        // optional (debug)
    }

    private readonly List<SellCandidate> _sellQueue = new(); // per-retainer pass
    private int _sellQueuePos;

    // Capacity limiting: retainer can hold 20 listings.
    private int _sellCapacityThisRetainer; // 20 - listedCount at entry
    private int _soldThisRetainer;

    private bool _processingListedItem = true; // true = repricing existing listing; false = selling new listing
    private uint _currentSellItemId;

    private InventorySlotRef _pendingSellSlot;
    private bool _hasPendingSellSlot;

    #endregion

    #region Universalis gate state

    private Task<decimal?>? _universalisGateTask;
    private UniversalisGateKey? _universalisGateKey;
    private decimal? _universalisGateAverage;
    private int? _universalisPriceFloor;

    #endregion

    #region Market reference + staging

    // Store retainer names as a set for fast lookup when checking "mine" sellers.
    private readonly HashSet<string> _myRetainers = new(StringComparer.Ordinal);

    // Per-item context captured from RetainerSell
    private bool _currentIsHq;

    // Staged apply: decide desired price from market, apply after market windows close.
    private int? _stagedDesiredPrice;
    private string _stagedReferenceSeller = string.Empty;
    private bool _stagedReferenceIsMine;
    private bool _hasAppliedStagedPrice;

    // ISR throttle handling (one retry policy)
    private bool _isrThrottleRetried;
    private DateTime _isrThrottleUntilUtc = DateTime.MinValue;

    // Adaptive Compare Prices pacing
    private double _mbIntervalSec = MbBaseIntervalSeconds;
    private DateTime _lastMbQueryUtc = DateTime.MinValue;

    // ISR settle gate (prevents trusting "No items found" too early)
    private DateTime _isrOpenedUtc = DateTime.MinValue;
    private int _isrNoItemsConfirm;

    // HQ filtering for ItemSearchResult (avoids virtualized rows issue)
    private bool _isrNeedApplyHqFilter;
    private bool _isrHqFilterApplied;
    private DateTime _isrHqFilterRequestedUtc = DateTime.MinValue;
    private DateTime _isrHqFilterVisibleUtc = DateTime.MinValue;

    // Only apply HQ filter as a fallback if HQ isn't visible in the first page
    private bool _isrHqFilterFallbackTried;

    // HQ filter timing gate: don't try to open/toggle filter until this time
    private DateTime _isrAllowFilterAfterUtc = DateTime.MinValue;

    // Retainer name sync -> Configuration
    private DateTime _lastRetainerSyncUtc = DateTime.MinValue;

    #endregion

    #region Inventory slot ref

    private struct InventorySlotRef
    {
        public int Container;
        public int Slot;
    }

    #endregion

    #region Lifecycle

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

        _universalisClient = new UniversalisApiClient(log);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        ContextMenu = new ContextMenuManager(Configuration);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        _uiReader = new Ui.UiReader(GameGui);

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

        _universalisClient.Dispose();

        ECommonsMain.Dispose();
    }

    #endregion

    #region Commands

    private unsafe void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        switch (a)
        {
            case "":
            case "help":
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
                    var raw = _uiReader.ReadRetainerSellListCountText();
                    var count = _uiReader.ReadRetainerSellListListedCount();
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

            case "testgate":
                TestUniversalisGate();
                return;

            default:
                PrintHelp();
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

    private void TestUniversalisGate()
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

        var baseUrl = string.IsNullOrWhiteSpace(Configuration.UniversalisApiBaseUrl)
            ? "https://universalis.app/api/v2/aggregated"
            : Configuration.UniversalisApiBaseUrl.Trim();

        Log.Information($"[RR][TestGate] Fetching Universalis data for item {itemId}, region='{region}', HQ={_currentIsHq}");

        decimal avg;
        try
        {
            var result = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
            avg = result != null ? (result.Result ?? 0) : 0;
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

        // Market validation: compare API average to current market average
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

        Log.Information($"[RR][TestGate] === Universalis Results ===");
        Log.Information($"[RR][TestGate] Item ID: {itemId}");
        Log.Information($"[RR][TestGate] Region: {region}");
        Log.Information($"[RR][TestGate] API Average: {(avg > 0 ? avg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Market Average ({marketCount} listings): {(marketAvg > 0 ? marketAvg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Config Percent: {percentDisplay:0}%");
        Log.Information($"[RR][TestGate] Floor Price: {(floor > 0 ? floor.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] ========================");

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
        var usedFloor = false;

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out var seller, out var isHq))
                continue;

            bool passes = floor <= 0 || price >= floor;
            var status = passes ? "PASS" : "SKIP (below floor)";

            Log.Information($"[RR][TestGate] Row {i}: price={price:N0} hq={isHq} seller='{seller}' => {status}");

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
            usedFloor = true;
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
        Log.Information($"[RR][TestGate] === Final Decision ===");
        Log.Information($"[RR][TestGate] Selected Row: {(selectedRow >= 0 ? selectedRow.ToString() : "FLOOR")}");
        Log.Information($"[RR][TestGate] Selected Price: {selectedPrice:N0}");
        Log.Information($"[RR][TestGate] Undercut Price: {undercutPrice:N0}");
        Log.Information($"[RR][TestGate] ========================");

        ChatGui.Print($"[RetainerRepricer] TestGate complete - check log for details.");
    }

    #endregion

    #region Framework

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastFrameworkTickUtc).TotalSeconds < FrameworkTickIntervalSeconds)
            return;

        TickRun();
        _lastFrameworkTickUtc = now;
    }

    #endregion

    #region Addon visibility / closing helpers

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

    #endregion

    #region UI navigation (AddonMaster wrappers)

    private unsafe bool TryClickRetainerListEntry(int index)
    {
        var addon = GameGui.GetAddonByName("RetainerList", 1);
        if (addon.IsNull) return false;

        try
        {
            var rl = new AddonMaster.RetainerList(addon.Address);
            var retainers = rl.Retainers;

            if (index < 0 || index >= retainers.Length) return false;

            var ok = retainers[index].Select();
            Log.Debug(ok
                ? $"[RL] Select retainer index={index}"
                : $"[RL] Retainer entry inactive index={index}");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RL] RetainerList select failed.");
            return false;
        }
    }

    private unsafe bool TrySelectSellItems()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        // SelectString is 0-based; "Sell items" is the 3rd entry here.
        const int sellItemsIndex = 2;

        try
        {
            var ss = new AddonMaster.SelectString(addon.Address);
            if (sellItemsIndex < 0 || sellItemsIndex >= ss.EntryCount) return false;

            ss.Entries[sellItemsIndex].Select();
            Log.Debug($"[SS] Select Sell items index={sellItemsIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SS] SelectString select failed.");
            return false;
        }
    }

    private unsafe bool TryAdvanceTalk()
    {
        var addon = GameGui.GetAddonByName("Talk", 1);
        if (addon.IsNull)
        {
            Log.Debug("[Talk] Addon not open.");
            return false;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
        {
            Log.Debug("[Talk] Addon not visible.");
            return false;
        }

        new AddonMaster.Talk(addon.Address).Click();
        Log.Debug("[Talk] Click advance");
        return true;
    }

    private unsafe bool FireRetainerSellListOpenItem(int slotIndex0)
    {
        var addon = GameGui.GetAddonByName("RetainerSellList", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, slotIndex0, 1);
        Log.Verbose($"[RSL] Open item callback (0, {slotIndex0}, 1)");
        return true;
    }

    private unsafe bool FireContextMenuDismiss()
    {
        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        // Two callbacks because the context menu can be in slightly different states.
        Callback.Fire(unit, updateState: true, 0, 0);
        Callback.Fire(unit, updateState: true, 1, 0);

        Log.Verbose("[CTX] Dismiss callbacks fired");
        return true;
    }
    private unsafe bool FireItemSearchResultOpenFilter()
    {
        var addon = GameGui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        // Per your finding: ItemSearchResult callback 1 opens ItemSearchFilter
        Callback.Fire(unit, updateState: true, 1);

        Log.Verbose("[ISR] Open filter (callback 1)");
        return true;
    }

    private unsafe bool FireItemSearchFilterToggleHq()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        // Per your finding: ItemSearchFilter 1,1 toggles HQ
        Callback.Fire(unit, updateState: true, 1, 1);

        Log.Verbose("[ISF] Toggle HQ (callback 1,1)");
        return true;
    }

    private unsafe bool FireItemSearchFilterAccept()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        // Per your finding: ItemSearchFilter 0 accepts
        Callback.Fire(unit, updateState: true, 0);

        Log.Verbose("[ISF] Accept filter (callback 0)");
        return true;
    }

    #endregion

    #region Retainer list sync

    internal unsafe List<string> ReadRetainerNames()
    {
        var names = new List<string>();

        var list = _uiReader.GetRetainerList();
        if (list == null) return names;

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
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
  
        SyncRetainersIntoConfig(ReadRetainerNames());
        _lastRetainerSyncUtc = now;
    }

    internal void RebuildMyRetainersSet()
    {
        _myRetainers.Clear();
        foreach (var kvp in Configuration.RetainersEnabled)
            _myRetainers.Add(kvp.Key);
    }

    #endregion

    #region Start/stop helpers

    internal unsafe void StartRunFromRetainerList()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            Log.Warning("[RR] Can't start: RetainerList not open.");
            return;
        }

        ResetRunState();

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            if (Configuration.IsRetainerEnabled(name))
                _retainerRowOrder.Add(i);
        }

        if (_retainerRowOrder.Count == 0)
        {
            Log.Warning("[RR] No enabled retainers found.");
            StopRun();
            return;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Start: enabled rows = {string.Join(",", _retainerRowOrder)}");
    }

    private unsafe void StartRun()
    {
        if (IsRunning)
        {
            Log.Information("[RR] Start ignored: already running.");
            return;
        }

        if (!IsAddonVisible("RetainerList"))
        {
            Log.Information("[RR] Cannot start: RetainerList is not visible. Open a summoning bell first.");
            return;
        }

        ResetRunState();

        var list = _uiReader.GetRetainerList();
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

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            if (Configuration.IsRetainerEnabled(name))
                _retainerRowOrder.Add(i);
        }

        if (_retainerRowOrder.Count == 0)
        {
            Log.Information("[RR] No enabled retainers found.");
            return;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Started. Enabled retainers: {string.Join(",", _retainerRowOrder)}");
    }

    internal void StopRun()
    {
        IsRunning = false;
        _runPhase = RunPhase.Idle;

        _retainerRowOrder.Clear();
        _retainerRowPos = -1;

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

        ResetUniversalisGateState();

        Log.Information("[RR] Stopped.");
    }

    private void ResetRunState()
    {
        ResetUniversalisGateState();

        RebuildMyRetainersSet();

        _retainerRowOrder.Clear();
        _retainerRowPos = -1;

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

        // Reset pacing so one bad throttle event doesn't slow the whole plugin permanently.
        _mbIntervalSec = MbBaseIntervalSeconds;
        _lastMbQueryUtc = DateTime.MinValue;

        _isrOpenedUtc = DateTime.MinValue;
        _isrNoItemsConfirm = 0;

        _isrThrottleRetried = false;
        _isrThrottleUntilUtc = DateTime.MinValue;
        _isrNeedApplyHqFilter = false;
        _isrAllowFilterAfterUtc = DateTime.MinValue;
        _isrHqFilterApplied = false;
        _isrHqFilterRequestedUtc = DateTime.MinValue;
        _isrHqFilterFallbackTried = false;
        _isrHqFilterVisibleUtc = DateTime.MinValue;

        _stagedDesiredPrice = null;
        _stagedReferenceSeller = string.Empty;
        _stagedReferenceIsMine = false;
        _hasAppliedStagedPrice = false;

        _lastRetainerSyncUtc = DateTime.MinValue;
    }

    #endregion

    #region Universalis gate helpers

    private void ResetUniversalisGateState()
    {
        _universalisGateTask = null;
        _universalisGateKey = null;
        _universalisGateAverage = null;
        _universalisPriceFloor = null;
    }

    private unsafe bool TryGetCurrentMarketItemId(out uint itemId)
    {
        itemId = 0;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        var agent = agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent == null)
            return false;

        var itemSearch = (AgentItemSearch*)agent;
        if (itemSearch == null)
            return false;

        var id = itemSearch->ResultItemId;
        if (id == 0)
            return false;

        itemId = id;
        return true;
    }

    private string? GetWorldDcRegionKey()
    {
        if (!Player.Available)
            return null;

        var worldName = Player.CurrentWorldName;
        if (!string.IsNullOrWhiteSpace(worldName))
            return worldName;

        var dcName = Player.CurrentDataCenterName;
        if (!string.IsNullOrWhiteSpace(dcName))
            return dcName;

        return null;
    }

    private UniversalisGateStatus UpdateUniversalisGate(out int floorPrice)
    {
        floorPrice = 0;

        if (!Configuration.EnableUndercutPreventionGate || !Configuration.UseUniversalisApi)
            return UniversalisGateStatus.Disabled;

        var baseUrl = string.IsNullOrWhiteSpace(Configuration.UniversalisApiBaseUrl)
            ? "https://universalis.app/api/v2/aggregated"
            : Configuration.UniversalisApiBaseUrl.Trim();

        if (!TryGetCurrentMarketItemId(out var itemId))
        {
            Log.Warning("[RR][Gate] Unable to resolve current item id for Universalis lookup; skipping item.");
            return UniversalisGateStatus.Failed;
        }

        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            Log.Warning("[RR][Gate] Unable to resolve world/DC region; skipping item.");
            return UniversalisGateStatus.Failed;
        }

        var key = new UniversalisGateKey(itemId, _currentIsHq, region, baseUrl);
        if (_universalisGateKey is not { } existing || existing != key)
        {
            _universalisGateKey = key;
            _universalisGateAverage = null;
            _universalisGateTask = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
            Log.Debug($"[RR][Gate] Requesting Universalis average for item {itemId} (region='{region}', HQ={_currentIsHq}).");
        }

        if (_universalisGateAverage is { } avg)
        {
            floorPrice = ComputeUniversalisFloor(avg, Configuration.UndercutPreventionPercent);
            return UniversalisGateStatus.Ready;
        }

        var task = _universalisGateTask;
        if (task == null)
            return UniversalisGateStatus.Failed;

        if (!task.IsCompleted)
            return UniversalisGateStatus.Pending;

        decimal? averageResult;
        try
        {
            averageResult = task.Result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][Gate] Universalis request failed; skipping item.");
            _universalisGateTask = null;
            return UniversalisGateStatus.Failed;
        }

        _universalisGateTask = null;

        if (averageResult is null || averageResult <= 0)
        {
            Log.Warning($"[RR][Gate] Universalis returned no data for item {itemId} (region='{region}', HQ={_currentIsHq}); skipping item.");
            return UniversalisGateStatus.Failed;
        }

        _universalisGateAverage = averageResult;

        // Market validation: compare API average to current market average
        var (marketAvg, marketCount) = TryGetMarketAverage();
        if (marketCount > 0)
        {
            var threshold = marketAvg * (decimal)MarketValidationThreshold;
            if (averageResult.Value > threshold)
            {
                Log.Information($"[RR][Gate] API avg {averageResult.Value:N0} > market avg {marketAvg:N0} * {MarketValidationThreshold:F1} = {threshold:N0}; using market avg instead.");
                _universalisGateAverage = marketAvg;
                averageResult = marketAvg;
            }
        }

        floorPrice = ComputeUniversalisFloor(averageResult.Value, Configuration.UndercutPreventionPercent);
        Log.Debug($"[RR][Gate] Universalis average={averageResult.Value:0} floor={floorPrice} for item {itemId} (region='{region}', HQ={_currentIsHq}).");
        return UniversalisGateStatus.Ready;
    }

    private (decimal Average, int Count) TryGetMarketAverage()
    {
        var list = _uiReader.GetMarketList();
        if (list == null)
            return (0, 0);

        var count = list->GetItemCount();
        if (count <= 0)
            return (0, 0);

        decimal sum = 0;
        var validCount = 0;
        var maxRows = Math.Min(count, 10);

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out _, out var isHq))
                continue;

            // Filter by HQ/NQ to match current item
            if (isHq != _currentIsHq)
                continue;

            sum += price;
            validCount++;
        }

        if (validCount == 0)
            return (0, 0);

        return (sum / validCount, validCount);
    }

    private static int ComputeUniversalisFloor(decimal averagePrice, float configuredPercent)
    {
        if (averagePrice <= 0)
            return 0;

        var percent = Math.Clamp(configuredPercent, 0.1f, 0.9f);
        var multiplier = (decimal)percent;
        var raw = (int)Math.Floor(averagePrice * multiplier);
        return raw < 1 ? 1 : raw;
    }

    private enum UniversalisGateStatus
    {
        Disabled,
        Pending,
        Ready,
        Failed,
    }

    private readonly record struct UniversalisGateKey(uint ItemId, bool IsHq, string Region, string BaseUrl);

    #endregion

    #region Decision helpers

    private static int DecideNewPrice(int lowestPrice, bool sellerIsMine)
    {
        if (sellerIsMine) return lowestPrice;

        var v = lowestPrice - UndercutAmount;
        return v < 1 ? 1 : v;
    }

    private unsafe bool IsSelectStringReady()
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

    private unsafe bool IsRetainerListReady()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (!string.IsNullOrWhiteSpace(name))
                return true;
        }

        return false;
    }

    #endregion

    #region Inventory selling helpers

    /// <summary>
    /// Scans bags (Inventory1-4) once. Returns:
    /// - totalCount across all stacks found
    /// - first sellable slotRef (container, slot) if any
    /// </summary>
    private bool TryFindItemInInventory(uint baseItemId, bool isHq, out InventorySlotRef slotRef, out int totalCount)
    {
        slotRef = default;
        totalCount = 0;

        if (_uiReader.TryFindItemInInventory(baseItemId, isHq, out var container, out var slot, out totalCount))
        {
            slotRef = new InventorySlotRef { Container = container, Slot = slot };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clicks an InventoryGrid slot to open RetainerSell for a new listing.
    /// This only works if InventoryGrid is actually visible.
    /// </summary>
    private unsafe bool TryOpenRetainerSellFromInventory(InventorySlotRef slotRef)
        => _uiReader.TryOpenRetainerSellFromInventory(slotRef.Container, slotRef.Slot);

    #endregion

    #region Market reading helpers

    private unsafe bool TryReadMarketRow(int rowIndex, out int unitPrice, out string seller, out bool isHq)
    {
        unitPrice = 0;
        seller = string.Empty;
        isHq = false;

        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0 || rowIndex < 0 || rowIndex >= count) return false;

        var r = list->GetItemRenderer(rowIndex);
        if (r == null) return false;

        isHq = _uiReader.RowIsHq(r);

        var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
        var sellerRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);

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

        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        var gateFloor = _universalisPriceFloor;

        if (_currentIsHq)
        {
            var max = Math.Min(count, 10);
            for (int i = 0; i < max; i++)
            {
                if (!TryReadMarketRow(i, out var price, out var seller, out var isHq)) continue;
                if (!isHq) continue;

                if (gateFloor.HasValue && price < gateFloor.Value)
                {
                    Log.Debug($"[RR][Gate] HQ row {i} price {price} below floor {gateFloor.Value}; skipping row.");
                    continue;
                }

                Log.Debug($"[RR] Market HQ ref row={i} price={price} seller='{seller}'");
                lowestPrice = price;
                lowestSeller = seller;
                return true;
            }

            if (gateFloor.HasValue)
            {
                Log.Information($"[RR][Gate] No HQ listings met the floor {gateFloor.Value}; using floor price.");
                lowestPrice = gateFloor.Value;
                lowestSeller = UniversalisFloorSeller;
                return true;
            }

            Log.Information("[RR] HQ item but no HQ rows found in first page.");
            return false;
        }

        if (gateFloor.HasValue)
        {
            var max = Math.Min(count, 10);
            for (int i = 0; i < max; i++)
            {
                if (!TryReadMarketRow(i, out var price, out var seller, out var isHq)) continue;
                if (isHq) continue;

                if (price < gateFloor.Value)
                {
                    Log.Debug($"[RR][Gate] NQ row {i} price {price} below floor {gateFloor.Value}; skipping row.");
                    continue;
                }

                Log.Debug($"[RR] Market NQ gated ref row={i} price={price} seller='{seller}'");
                lowestPrice = price;
                lowestSeller = seller;
                return true;
            }

            Log.Information($"[RR][Gate] No NQ listings met the floor {gateFloor.Value}; using floor price.");
            lowestPrice = gateFloor.Value;
            lowestSeller = UniversalisFloorSeller;
            return true;
        }

        // NQ without gate: row 0 is the reference.
        if (!TryReadMarketRow(0, out var p0, out var s0, out _)) return false;

        Log.Debug($"[RR] Market NQ ref row0 price={p0} seller='{s0}'");
        lowestPrice = p0;
        lowestSeller = s0;
        return true;
    }
    private unsafe bool IsAnyHqVisibleInFirstPage()
    {
        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            // Reuse existing row-read path so we don't trust unpopulated nodes
            if (!TryReadMarketRow(i, out _, out _, out var isHq)) continue;
            if (isHq) return true;
        }

        return false;
    }

    #endregion

    #region Debug dumps

    private unsafe void DumpMarketRows()
    {
        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Information("[MB] Market list not found. Open ItemSearchResult first.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[MB] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var seller = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
            var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
            var qtyRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

            _uiReader.DumpHqIconState(r, i, s => Log.Verbose(s));
            var isHq = _uiReader.RowIsHq(r);

            var unit = Ui.UiReader.ParseGil(unitRaw);
            var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

            Log.Information($"[MB] row {i}: seller={seller} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
        }
    }

    private unsafe void DumpRetainerRows()
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

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            Log.Information($"[RL] row {i}: name='{name}'");
        }
    }

    #endregion

    #region Run tick (state machine)

    internal unsafe void TickRun()
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastActionUtc).TotalSeconds < ActionIntervalSeconds)
            return;

        // Snapshot addon visibility once per tick (cheaper and keeps checks consistent for this tick).
        var retainerListVisible = IsAddonVisible("RetainerList");
        var talkVisible = IsAddonVisible("Talk");
        var selectStringVisible = IsAddonVisible("SelectString");
        var retainerSellListVisible = IsAddonVisible("RetainerSellList");
        var retainerSellVisible = IsAddonVisible("RetainerSell");
        var marketOpen = IsAddonOpen("ItemSearchResult");
        var itemHistoryOpen = IsAddonOpen("ItemHistory");
        var contextMenuVisible = IsAddonVisible("ContextMenu");

        switch (_runPhase)
        {
            #region Retainer navigation

            case RunPhase.NeedOpen:
                {
                    if (!retainerListVisible) return;

                    if (!IsRetainerListReady())
                    {
                        Log.Verbose("[RR] RetainerList visible but names not populated yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    _retainerRowPos++;
                    if (_retainerRowPos >= _retainerRowOrder.Count)
                    {
                        Log.Information("[RR] Finished: processed all enabled retainers.");
                        ChatGui.Print("[RetainerRepricer] Repricing has finished.");

                        if (Configuration.CloseRetainerListAddon)
                        {
                            Log.Information("[RR] Closing RetainerList (CloseRetainerListAddon enabled).");
                            CloseAddonIfOpen("RetainerList");
                        }

                        StopRun();
                        return;
                    }

                    // Reset per-retainer counters.
                    _sellListCountCaptured = false;
                    _listedCountThisRetainer = 0;
                    _slotIndexToOpen = 0;

                    // Build per-retainer sell queue (runs after repricing).
                    _sellQueue.Clear();
                    foreach (var e in Configuration.GetSellListSorted())
                    {
                        if (e.ItemId == 0) continue;

                        _sellQueue.Add(new SellCandidate
                        {
                            ItemId = e.ItemId,
                            IsHq = e.IsHq,
                            MinCountToSell = Math.Max(1, e.MinCountToSell),
                            Name = e.Name ?? string.Empty,
                        });
                    }
                    _sellQueuePos = 0;

                    _sellCapacityThisRetainer = 0;
                    _soldThisRetainer = 0;

                    _processingListedItem = true;
                    _currentSellItemId = 0;
                    _hasPendingSellSlot = false;

                    // Reset pacing per retainer so a throttle hit doesn't poison the next retainer.
                    _mbIntervalSec = MbBaseIntervalSeconds;
                    _lastMbQueryUtc = DateTime.MinValue;

                    var row = _retainerRowOrder[_retainerRowPos];
                    Log.Information($"[RR] Opening retainer row {row} ({_retainerRowPos + 1}/{_retainerRowOrder.Count})");

                    TryClickRetainerListEntry(row);

                    _runPhase = RunPhase.WaitingTalk;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingTalk:
                {
                    // Some setups skip Talk entirely and jump straight to SelectString.
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for row {_retainerRowOrder[_retainerRowPos]}; selecting Sell items.");
                        TrySelectSellItems();

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open for row {_retainerRowOrder[_retainerRowPos]}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for row {_retainerRowOrder[_retainerRowPos]}; selecting Sell items.");
                        TrySelectSellItems();

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open (late) for row {_retainerRowOrder[_retainerRowPos]}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellList:
                {
                    if (!retainerSellListVisible) return;

                    // Don't latch listed count until we can read it reliably.
                    if (!_sellListCountCaptured)
                    {
                        var raw = _uiReader.ReadRetainerSellListCountText();
                        var listedOpt = _uiReader.ReadRetainerSellListListedCount();

                        if (listedOpt is null || string.IsNullOrWhiteSpace(raw))
                        {
                            Log.Verbose($"[RR] RetainerSellList visible but count not readable (raw='{raw ?? "null"}').");
                            _lastActionUtc = now;
                            return;
                        }

                        _sellListCountCaptured = true;

                        var listed = listedOpt.Value;
                        _listedCountThisRetainer = Math.Clamp(listed, 0, 20);
                        _slotIndexToOpen = 0;

                        _sellCapacityThisRetainer = Math.Max(0, 20 - _listedCountThisRetainer);
                        _soldThisRetainer = 0;

                        Log.Information($"[RR] Entered RetainerSellList. Listed={_listedCountThisRetainer}; new sells capacity={_sellCapacityThisRetainer}");
                    }

                    _runPhase = (_listedCountThisRetainer <= 0)
                        ? RunPhase.Sell_FindNextItemInInventory
                        : RunPhase.OpeningSellItem;

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing existing listed items

            case RunPhase.OpeningSellItem:
                {
                    if (!retainerSellListVisible) return;

                    if (_slotIndexToOpen < 0) _slotIndexToOpen = 0;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    var idx = Math.Clamp(_slotIndexToOpen, 0, 19);
                    Log.Debug($"[RR] Opening sell slot {idx} ({_slotIndexToOpen + 1}/{_listedCountThisRetainer})");

                    _processingListedItem = true;

                    if (!FireRetainerSellListOpenItem(idx))
                        return;

                    _runPhase = RunPhase.WaitingRetainerSell;
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

                    Log.Debug($"[RR] RetainerSell opened (listedItem={_processingListedItem}).");

                    _runPhase = RunPhase.CaptureSellContext;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing pipeline

            case RunPhase.CaptureSellContext:
                {
                    if (!retainerSellVisible) return;

                    // Wait until the fields we rely on are readable.
                    var priceOpt = _uiReader.ReadRetainerSellAskingPrice();
                    if (priceOpt is null)
                    {
                        Log.Verbose("[RR] RetainerSell visible but asking price not readable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    _currentIsHq = _uiReader.IsRetainerSellItemHq();

                    // Always use the display-safe name (raw payload can leak printable junk).
                    var name = _uiReader.GetRetainerSellItemNameDisplay(stripHqGlyph: true);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log.Verbose("[RR] RetainerSell visible but item name not stable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    ResetUniversalisGateState();

                    Log.Information($"[RR] Sell capture: item='{name}' currentPrice={priceOpt.Value}");

                    _runPhase = RunPhase.OpenComparePrices;
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
                        Log.Debug("[RR] ItemSearchResult already open; skipping Compare Prices click.");

                        _isrOpenedUtc = DateTime.MinValue;
                        _isrNoItemsConfirm = 0;

                        _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                        _isrHqFilterApplied = false;
                        _isrHqFilterRequestedUtc = DateTime.MinValue;
                        _isrHqFilterFallbackTried = false;
                        _isrAllowFilterAfterUtc = DateTime.MinValue;
                        _isrHqFilterVisibleUtc = DateTime.MinValue;

                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    var addon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (addon.IsNull) return;

                    // Space out Compare Prices calls to avoid "Please wait..." throttles.
                    var jitter = Random.Shared.NextDouble() * MbJitterMaxSeconds;
                    if (_lastMbQueryUtc != DateTime.MinValue &&
                        (now - _lastMbQueryUtc).TotalSeconds < (_mbIntervalSec + jitter))
                    {
                        Log.Verbose("[RR] Compare Prices pacing gate hit.");
                        _lastActionUtc = now;
                        return;
                    }

                    _lastMbQueryUtc = now;
                    new AddonMaster.RetainerSell(addon.Address).ComparePrices();

                    _isrOpenedUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;

                    Log.Debug($"[RR] ComparePrices clicked (interval={_mbIntervalSec:0.00}s).");
                    _runPhase = RunPhase.WaitingItemSearchResult;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingItemSearchResult:
                {
                    if (!marketOpen) return;

                    // If HQ item: apply the HQ filter so HQ rows are rendered immediately.
                    // This avoids list virtualization where only 10 visible rows have populated text.
                    if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                    {
                        // Respect global filter timing gate
                        if (_isrAllowFilterAfterUtc != DateTime.MinValue && now < _isrAllowFilterAfterUtc)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        var filterVisible = IsAddonVisible("ItemSearchFilter");
                        if (!filterVisible)
                            _isrHqFilterVisibleUtc = DateTime.MinValue;

                        // If filter UI is visible, toggle HQ and accept
                        if (filterVisible)
                        {
                            if (_isrHqFilterVisibleUtc == DateTime.MinValue)
                            {
                                _isrHqFilterVisibleUtc = now;
                                _lastActionUtc = now;
                                return;
                            }

                            if ((now - _isrHqFilterVisibleUtc).TotalSeconds < IsrHqFilterUiDebounceSeconds)
                            {
                                _lastActionUtc = now;
                                return;
                            }

                            FireItemSearchFilterToggleHq();
                            FireItemSearchFilterAccept();

                            _isrHqFilterApplied = true;
                            _isrNeedApplyHqFilter = false;

                            // reset ISR tracking so rows repopulate cleanly
                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            Log.Debug("[RR] HQ filter applied; waiting for ISR to repopulate.");
                            _lastActionUtc = now;
                            return;
                        }

                        // Filter window not open yet → request it
                        if (_isrHqFilterRequestedUtc == DateTime.MinValue ||
                            (now - _isrHqFilterRequestedUtc).TotalSeconds >= IsrHqFilterOpenRetrySeconds)
                        {
                            FireItemSearchResultOpenFilter();
                            _isrHqFilterRequestedUtc = now;

                            // brief pause to allow the filter window to appear
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterPostOpenSeconds);

                            _lastActionUtc = now;
                            return;
                        }

                        _lastActionUtc = now;
                        return;
                    }

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);

                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows > 0)
                        {
                            Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                            _isrNoItemsConfirm = 0;
                            _runPhase = RunPhase.ReadMarketAndApplyPrice;
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // If we're in the middle of applying the HQ filter, do NOT retry ComparePrices.
                        // Filtering triggers a repopulation and can temporarily surface "Please wait".
                        if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                        {
                            Log.Verbose("[RR] ISR throttle while applying HQ filter; waiting (no ComparePrices retry).");
                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            _lastActionUtc = now;
                            return;
                        }

                        if (now < _isrThrottleUntilUtc)
                        {
                            Log.Verbose("[RR] ISR throttle backoff window active.");
                            _lastActionUtc = now;
                            return;
                        }

                        if (!_isrThrottleRetried && _isrThrottleUntilUtc == DateTime.MinValue)
                        {
                            _mbIntervalSec = Math.Clamp(_mbIntervalSec + 0.08, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            Log.Debug("[RR] ISR throttle: backing off then retrying once.");
                            return;
                        }

                        if (!_isrThrottleRetried)
                        {
                            var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                            if (sellAddon.IsNull) return;

                            _lastMbQueryUtc = now;
                            new AddonMaster.RetainerSell(sellAddon.Address).ComparePrices();

                            _isrThrottleRetried = true;
                            _isrThrottleUntilUtc = DateTime.MinValue;

                            Log.Debug("[RR] ISR throttle: retry sent.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information("[RR] ISR throttle persists after one retry; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var list = _uiReader.GetMarketList();
                    if (list == null)
                    {
                        Log.Verbose("[RR] ISR not ready: market list null.");
                        _lastActionUtc = now;
                        return;
                    }

                    var count = list->GetItemCount();
                    if (count <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: 0 rows.");
                        _lastActionUtc = now;
                        return;
                    }

                    // Renderer can exist before nodes/text are actually populated.
                    var r0 = list->GetItemRenderer(0);
                    if (r0 == null || r0->UldManager.NodeList == null || r0->UldManager.NodeListCount <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 renderer nodes not ready.");
                        _lastActionUtc = now;
                        return;
                    }

                    var unitRaw = _uiReader.ReadRendererText(r0, Ui.NodePaths.UnitPriceNodeId);
                    if (Ui.UiReader.ParseGil(unitRaw) is null)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 unit price not parsable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] ItemSearchResult ready (rows={count}).");
                    _mbIntervalSec = Math.Clamp(_mbIntervalSec - 0.02, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                    _runPhase = RunPhase.ReadMarketAndApplyPrice;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ReadMarketAndApplyPrice:
                {
                    if (!retainerSellVisible) return;
                    if (!marketOpen) return;

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);
                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows <= 0)
                        {
                            Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                        _isrNoItemsConfirm = 0;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // Optional: backoff a little so we don't hammer the gate
                        _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);

                        Log.Debug("[RR] ISR throttle surfaced during read; returning to ISR gate.");
                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (Configuration.EnableUndercutPreventionGate && Configuration.UseUniversalisApi)
                    {
                        var gateStatus = UpdateUniversalisGate(out var floorPrice);
                        if (gateStatus == UniversalisGateStatus.Pending)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        if (gateStatus == UniversalisGateStatus.Failed)
                        {
                            Log.Information("[RR][Gate] Skipping repricing: Universalis gate failed for this item.");
                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        _universalisPriceFloor = gateStatus == UniversalisGateStatus.Ready
                            ? floorPrice
                            : null;
                    }
                    else
                    {
                        _universalisPriceFloor = null;
                    }

                    if (!TryPickReferenceListing(out var lowestPrice, out var lowestSeller))
                    {
                        // If this is an HQ item and we didn't see HQ in the first page,
                        // try applying the HQ filter ONCE as a fallback to avoid list virtualization.
                        if (_currentIsHq && !_isrHqFilterFallbackTried)
                        {
                            if (IsAnyHqVisibleInFirstPage())
                            {
                                Log.Debug("[RR] HQ became visible in first page; retrying pick without filtering.");
                                _lastActionUtc = now;
                                return; // next tick TryPickReferenceListing will succeed
                            }

                            Log.Information("[RR] HQ not visible in first page; applying HQ filter fallback once.");

                            _isrHqFilterFallbackTried = true;

                            // Arm the filter state machine
                            _isrNeedApplyHqFilter = true;
                            _isrHqFilterApplied = false;
                            _isrHqFilterRequestedUtc = DateTime.MinValue;

                            // delay before attempting HQ filter so ISR can settle
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterInitialDelaySeconds);

                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            _runPhase = RunPhase.WaitingItemSearchResult;
                            _lastActionUtc = now;
                            return;
                        }

                        // If fallback already tried (or NQ item), skip.
                        _runPhase = RunPhase.CleanupAfterItem;
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
                        Log.Information($"[RR] Price unchanged ({desired}); skipping apply.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    _stagedDesiredPrice = desired;
                    _stagedReferenceSeller = lowestSeller;
                    _stagedReferenceIsMine = referenceIsMine;
                    _hasAppliedStagedPrice = false;

                    Log.Information($"[RR] Stage apply: current={current} desired={desired} seller='{lowestSeller}' mine={referenceIsMine}");

                    CloseMarketWindows();

                    _runPhase = RunPhase.CloseMarketThenApply;
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
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var current = _uiReader.ReadRetainerSellAskingPrice();
                    if (current is null)
                    {
                        Log.Verbose("[RR] Waiting RetainerSell stable (asking price unreadable).");
                        _lastActionUtc = now;
                        return;
                    }

                    if (_hasAppliedStagedPrice)
                    {
                        if (current.Value != _stagedDesiredPrice.Value)
                        {
                            Log.Verbose($"[RR] Waiting price apply... ui={current.Value} desired={_stagedDesiredPrice.Value}");
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.ConfirmAfterApply;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Apply after market close: {current.Value} -> {_stagedDesiredPrice.Value}");

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
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (MarketWindowsStillOpen())
                        return;

                    var desired = _stagedDesiredPrice.Value;
                    var uiPrice = _uiReader.ReadRetainerSellAskingPrice();
                    if (uiPrice == null || uiPrice.Value != desired)
                    {
                        Log.Verbose($"[RR] Waiting price apply... ui={(uiPrice?.ToString() ?? "null")} desired={desired}");
                        _lastActionUtc = now;
                        return;
                    }

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null || !unit->IsVisible) return;

                    Log.Information($"[RR] Confirm price={desired}");

                    Callback.Fire(unit, updateState: true, 0);

                    _stagedDesiredPrice = null;
                    _hasAppliedStagedPrice = false;

                    _runPhase = RunPhase.CleanupAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CleanupAfterItem:
                {
                    CloseMarketWindows();
                    CloseRetainerSellIfOpen();

                    Log.Verbose("[RR] Cleanup complete (market closed, exited RetainerSell).");

                    _isrNeedApplyHqFilter = false;
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _runPhase = RunPhase.WaitingRetainerSellListAfterItem;
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
                        Log.Information($"[RR] New listings: {_soldThisRetainer}/{_sellCapacityThisRetainer}");

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _slotIndexToOpen++;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.OpeningSellItem;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Selling pipeline (after repricing)

            case RunPhase.Sell_FindNextItemInInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0)
                    {
                        Log.Information("[RR] Sell skipped: retainer is full (20/20).");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        Log.Information($"[RR] Sell complete: reached capacity {_soldThisRetainer}/{_sellCapacityThisRetainer}.");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_sellQueue.Count == 0)
                    {
                        Log.Debug("[RR] Sell skipped: SellList is empty.");
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    while (_sellQueuePos < _sellQueue.Count)
                    {
                        var c = _sellQueue[_sellQueuePos];
                        _sellQueuePos++;

                        if (c.ItemId == 0)
                            continue;

                        var threshold = Math.Max(1, c.MinCountToSell);

                        if (!TryFindItemInInventory(c.ItemId, c.IsHq, out var slotRef, out var totalCount))
                        {
                            Log.Verbose($"[RR] Sell candidate not in inventory: itemId={c.ItemId} hq={c.IsHq}");
                            continue;
                        }

                        if (totalCount < threshold)
                        {
                            Log.Verbose($"[RR] Sell candidate skipped: itemId={c.ItemId} hq={c.IsHq} count={totalCount} < threshold={threshold}");
                            continue;
                        }

                        _currentSellItemId = c.ItemId;
                        _pendingSellSlot = slotRef;
                        _hasPendingSellSlot = true;

                        Log.Debug($"[RR] Sell candidate: itemId={c.ItemId} hq={c.IsHq} count={totalCount} threshold={threshold} container={slotRef.Container} slot={slotRef.Slot} (cap {_soldThisRetainer}/{_sellCapacityThisRetainer})");

                        _runPhase = RunPhase.Sell_OpenRetainerSellFromInventory;
                        _lastActionUtc = now;
                        return;

                    }

                    Log.Information("[RR] Sell complete: none of the SellList items were found in inventory.");
                    _runPhase = RunPhase.ExitToRetainerList;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.Sell_OpenRetainerSellFromInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0 || _soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        _runPhase = RunPhase.ExitToRetainerList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!_hasPendingSellSlot)
                    {
                        Log.Warning("[RR] Sell_Open: no pending slot; returning to scan.");
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _processingListedItem = false;

                    var ok = TryOpenRetainerSellFromInventory(_pendingSellSlot);
                    if (!ok)
                    {
                        Log.Information($"[RR] Sell_Open: failed to open RetainerSell from inventory (itemId={_currentSellItemId}); skipping.");
                        _hasPendingSellSlot = false;
                        _pendingSellSlot = default;
                        _currentSellItemId = 0;

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Sell_Open: requested RetainerSell from inventory (itemId={_currentSellItemId}).");

                    _hasPendingSellSlot = false;
                    _pendingSellSlot = default;

                    _runPhase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Exit unwind

            case RunPhase.ExitToRetainerList:
                {
                    if (marketOpen || itemHistoryOpen)
                    {
                        Log.Debug("[RR] Exit: closing market windows.");
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    if (contextMenuVisible)
                    {
                        Log.Debug("[RR] Exit: dismissing ContextMenu.");
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSell.");
                        CloseRetainerSellIfOpen();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellListVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSellList.");
                        CloseAddonIfOpen("RetainerSellList");
                        _lastActionUtc = now;
                        return;
                    }

                    if (selectStringVisible)
                    {
                        Log.Debug("[RR] Exit: closing SelectString.");
                        CloseAddonIfOpen("SelectString");
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug("[RR] Exit: advancing/closing Talk.");
                        TryAdvanceTalk();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerListVisible)
                    {
                        Log.Debug("[RR] Exit: back on RetainerList; continuing.");
                        _runPhase = RunPhase.NeedOpen;
                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            default:
                return;
        }
    }

    #endregion

    #region Window helpers

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => ToggleConfigUi();

    #endregion
}
