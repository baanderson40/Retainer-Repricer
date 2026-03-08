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

public unsafe sealed partial class Plugin : IDalamudPlugin
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
    private const string CommandAlias = "/rr";
    private const string CommandHelp = "help | start [price|sell] | stop | config";
    private const string ChatTag = "Retainer Repricer";
    private const ushort InfoTagColor = 34;
    private const ushort ErrorTagColor = 14;
    private const ushort SuccessTagColor = 45;

    #endregion

    #region Config-driven tuning accessors

    private int UndercutAmount => Configuration.UndercutAmount;
    private float MarketValidationThreshold => Configuration.MarketValidationThreshold;
    private double ActionIntervalSeconds => Configuration.ActionIntervalSeconds;
    private double RetainerSyncIntervalSeconds => Configuration.RetainerSyncIntervalSeconds;
    private double ItemSearchResultThrottleBackoffSeconds => Configuration.ItemSearchResultThrottleBackoffSeconds;
    private double MbBaseIntervalSeconds => Configuration.MbBaseIntervalSeconds;
    private double MbIntervalMinSeconds => Configuration.MbIntervalMinSeconds;
    private double MbIntervalMaxSeconds => Configuration.MbIntervalMaxSeconds;
    private double MbJitterMaxSeconds => Configuration.MbJitterMaxSeconds;
    private double IsrNoItemsSettleSeconds => Configuration.IsrNoItemsSettleSeconds;
    private double IsrHqFilterInitialDelaySeconds => Configuration.IsrHqFilterInitialDelaySeconds;
    private double IsrHqFilterUiDebounceSeconds => Configuration.IsrHqFilterUiDebounceSeconds;
    private double IsrHqFilterOpenRetrySeconds => Configuration.IsrHqFilterOpenRetrySeconds;
    private double IsrHqFilterPostOpenSeconds => Configuration.IsrHqFilterPostOpenSeconds;
    private double FrameworkTickIntervalSeconds => Configuration.FrameworkTickIntervalSeconds;

    #endregion

    #region Public/plugin-facing state

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("RetainerRepricer");

    internal bool IsRunning;

    private readonly UniversalisApiClient _universalisClient;
    private readonly SellListSmartSorter _smartSorter;
    private Task<bool>? _pendingSmartSortTask;
    private bool _smartSortKickoffDone;

    #endregion

    #region Windows / UI helpers

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }
    private MinCountPopup MinCountPopup { get; }
    private ContextMenuManager ContextMenu { get; }

    private readonly Ui.UiReader _uiReader;

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
        _smartSorter = new SellListSmartSorter(Configuration, _universalisClient, log, GetWorldDcRegionKey);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        MinCountPopup = new MinCountPopup();
        ContextMenu = new ContextMenuManager(this, Configuration, MinCountPopup);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(MinCountPopup);

        _uiReader = new Ui.UiReader(GameGui);

        _mbIntervalSec = Configuration.MbBaseIntervalSeconds;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = CommandHelp
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = CommandHelp
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
        MinCountPopup.Dispose();
        ContextMenu.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        _universalisClient.Dispose();
        _smartSorter.Dispose();

        ECommonsMain.Dispose();
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

    internal unsafe bool StartRunFromRetainerList(RunMode mode = RunMode.PriceAndSell, bool notifyChatOnFailure = false)
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            NotifyStartFailed("RetainerList not open. Summon retainers first.", notifyChatOnFailure);
            return false;
        }

        ResetRunState();
        _runMode = mode;

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
            NotifyStartFailed("No enabled retainers found in the list.", notifyChatOnFailure);
            StopRun();
            return false;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Start ({DescribeRunMode(mode)}). Enabled rows = {string.Join(",", _retainerRowOrder)}");
        return true;
    }

    private unsafe bool StartRun(RunMode mode = RunMode.PriceAndSell, bool notifyChatOnFailure = false)
    {
        if (IsRunning)
        {
            NotifyStartFailed("Already running.", notifyChatOnFailure);
            return false;
        }

        if (!IsAddonVisible("RetainerList"))
        {
            NotifyStartFailed("Retainer List is not open. Use a summoning bell first.", notifyChatOnFailure);
            return false;
        }

        ResetRunState();
        _runMode = mode;

        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            NotifyStartFailed("Retainer List is not readable right now.", notifyChatOnFailure);
            return false;
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
            NotifyStartFailed("No enabled retainers found in the list.", notifyChatOnFailure);
            return false;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information($"[RR] Started ({DescribeRunMode(mode)}). Enabled retainers: {string.Join(",", _retainerRowOrder)}");
        return true;
    }

    internal void StopRun()
    {
        IsRunning = false;
        _runPhase = RunPhase.Idle;
        _runMode = RunMode.PriceAndSell;

        _retainerRowOrder.Clear();
        _retainerRowPos = -1;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        _sellQueuePos = 0;
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
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
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
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

    private static string DescribeRunMode(RunMode mode)
        => mode switch
        {
            RunMode.PriceOnly => "price-only",
            RunMode.SellOnly => "sell-only",
            _ => "price+sell",
        };

    private void NotifyStartFailed(string reason, bool notifyChat)
    {
        Log.Information($"[RR] Start blocked: {reason}");
        if (notifyChat)
            PrintError($"Cannot start: {reason}");
    }

    private void PrintChat(string message, ushort tagColor)
    {
        ChatGui.Print(message, ChatTag, tagColor);
    }

    private void PrintInfo(string message)
    {
        PrintChat(message, InfoTagColor);
    }

    private void PrintError(string message)
    {
        PrintChat(message, ErrorTagColor);
    }

    private void PrintSuccess(string message)
    {
        PrintChat(message, SuccessTagColor);
    }

    private string GetCompletionMessage()
        => _runMode switch
        {
            RunMode.PriceOnly => "Repricing run complete.",
            RunMode.SellOnly => "Sell-only run complete.",
            _ => "Repricing and selling run complete.",
        };

    #endregion

    #region Decision helpers

    private int DecideNewPrice(int lowestPrice, bool sellerIsMine)
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
                lowestSeller = "[UniversalisFloor]";
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
            lowestSeller = "[UniversalisFloor]";
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

    #region Window helpers

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => ToggleConfigUi();

    internal bool SmartSortEnabled => _smartSorter.IsEnabled;
    internal bool SmartSortIsSorting => _smartSorter.IsSorting;
    internal DateTime SmartSortLastRunUtc => _smartSorter.LastSortUtc;
    internal bool SmartSortRefreshDue => _smartSorter.IsRefreshDue();

    internal Task<bool> RequestSmartSortAsync(string reason, bool force = false)
    {
        if (force)
            return _smartSorter.ForceSortAsync(reason);

        return _smartSorter.TrySortAsync(reason, force: false);
    }

    internal void NotifySmartSortSettingChanged()
    {
        // Reset pending state so a new run can force refresh immediately.
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
    }

    #endregion
}
