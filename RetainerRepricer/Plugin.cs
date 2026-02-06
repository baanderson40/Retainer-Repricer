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
using System.Text;

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

    // =========================================================
    // Constants / Fields
    // =========================================================
    private const string CommandName = "/repricer";

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("RetainerRepricer");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    private readonly Ui.UiReader _ui;

    // =========================================================
    // Run State (Retainer cycling)
    // =========================================================
    internal bool IsRunning;

    private enum RunPhase
    {
        Idle,
        NeedOpen,                 // ready to click next retainer row in RetainerList
        WaitingTalk,              // waiting for Talk after clicking a retainer; click to advance
        WaitingSelectString,      // waiting for SelectString after Talk closes
        WaitingRetainerSellList,  // waiting for RetainerSellList after choosing Sell items
        OpeningFirstSellItem,     // fire RetainerSellList callback to open item detail
        WaitingRetainerSell,      // wait for RetainerSell to appear (and clear ContextMenu if needed)
        WaitingReturn,            // waiting for user to return to RetainerList
    }

    private RunPhase _phase = RunPhase.Idle;

    private readonly List<int> _runOrder = new(); // row indices (0-based)
    private int _runPos = -1;                     // current position into _runOrder

    private DateTime _lastActionUtc = DateTime.MinValue;
    private const double ActionIntervalSeconds = 0.75; // pacing between actions (safe while testing)

    // When we enter RetainerSellList, capture listed count once.
    private bool _sellListCountCaptured;
    private int _slotIndexToOpen = 0; // 0-based slot in RetainerSellList you want to open first

    // =========================================================
    // Retainer name sync -> Configuration
    // =========================================================
    private DateTime _lastRetainerSyncUtc = DateTime.MinValue;
    private const double RetainerSyncIntervalSeconds = 2.0;

    // =========================================================
    // Lifecycle
    // =========================================================
    public Plugin()
    {
        // ECommons needs to be initialized early so Svc and AddonMaster work.
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        _ui = new Ui.UiReader(GameGui);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "toggle | config | count | talk (debug) | hqdump (debug) | mbdump (debug) | mbnodelist (debug) | rldump (debug)"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        Log.Information($"[{PluginInterface.Manifest.Name}] loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }

    // =========================================================
    // Commands
    // =========================================================
    private unsafe void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();
        Log.Information($"[CMD] raw args='{args}' parsed='{a}'");

        switch (a)
        {
            case "config":
                ToggleConfigUi();
                return;

            case "talk":
                {
                    Log.Information("[TalkTest] entered /repricer talk");
                    var ok = ClickTalkECommons();
                    Log.Information(ok ? "[TalkTest] Sent (ECommons)" : "[TalkTest] Failed (ECommons)");
                    return;
                }

            case "hqdump":
                {
                    // Asking price
                    var priceText = _ui.ReadRetainerSellAskingPriceText();
                    var asking = _ui.ReadRetainerSellAskingPrice();
                    Log.Information($"[RS] askingPrice raw='{priceText}' parsed={asking?.ToString() ?? "null"}");

                    // Item name + HQ glyph
                    var rawName = _ui.ReadRetainerSellItemNameRaw();
                    var cleaned = _ui.NormalizeItemName(rawName);
                    var isHq = _ui.RetainerSellNameContainsHqGlyph(rawName);

                    Log.Information($"[HQ] raw='{rawName}'");
                    Log.Information($"[HQ] cleaned='{cleaned}'");
                    Log.Information($"[HQ] containsHqGlyph={isHq} (glyph U+{(int)Ui.UiReader.RetainerSell_HqGlyphChar:X4})");
                    return;
                }

            case "mbnodelist":
                _ui.DumpAddonNodeList("ItemSearchResult", s => Log.Information(s));
                return;

            case "mbdump":
                DumpMarketRows();
                return;

            case "rldump":
                DumpRetainerRows();
                return;

            case "count":
                {
                    var raw = _ui.ReadRetainerSellListCountText();
                    var count = _ui.ReadRetainerSellListListedCount();

                    if (count == null)
                    {
                        Log.Warning("[Count] Listed count returned null (open RetainerSellList first).");
                        return;
                    }

                    Log.Information($"[Count] Listed = {count} (raw='{raw}')");
                    return;
                }

            default:
                // /repricer (no args) toggles overlay intent
                MainWindow.ToggleIntent();
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

        // Lua: /pcall RetainerSellList true 0 idx 1
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

        // Lua tries (0,0) then (1,0) to clear it; keep both.
        Callback.Fire(unit, updateState: true, 0, 0);
        Callback.Fire(unit, updateState: true, 1, 0);

        Log.Information("[CTX] FireCallback dismiss tried: (0,0) then (1,0)");
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
    // Retainer list -> config sync
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

        _runOrder.Clear();
        _runPos = -1;
        _sellListCountCaptured = false;

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

        Log.Information("[RR] Stopped.");
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

        var rlOpen = !GameGui.GetAddonByName("RetainerList", 1).IsNull;
        var talkOpen = !GameGui.GetAddonByName("Talk", 1).IsNull;
        var ssOpen = !GameGui.GetAddonByName("SelectString", 1).IsNull;
        var sellListOpen = !GameGui.GetAddonByName("RetainerSellList", 1).IsNull;

        switch (_phase)
        {
            case RunPhase.NeedOpen:
                {
                    if (!rlOpen) return;

                    _runPos++;
                    if (_runPos >= _runOrder.Count)
                    {
                        Log.Information("[RR] Done: processed all enabled retainers.");
                        StopRun();
                        return;
                    }

                    var row = _runOrder[_runPos];
                    Log.Information($"[RR] Opening retainer row {row} (pos {_runPos + 1}/{_runOrder.Count})");

                    _sellListCountCaptured = false;
                    ClickRetainerByIndex(row);

                    _phase = RunPhase.WaitingTalk;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingTalk:
                {
                    if (ssOpen)
                    {
                        Log.Information($"[RR] SelectString opened for row {_runOrder[_runPos]}; choosing Sell items.");
                        ClickSelectStringSellItems();

                        _phase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkOpen)
                    {
                        Log.Information($"[RR] Talk open for row {_runOrder[_runPos]}; clicking Talk advance.");
                        var ok = ClickTalkECommons();
                        Log.Information(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    Log.Information($"[RR] Talk closed for row {_runOrder[_runPos]}; waiting for SelectString.");
                    _phase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
                    if (talkOpen)
                    {
                        Log.Information($"[RR] Talk open (late) for row {_runOrder[_runPos]}; clicking Talk advance.");
                        var ok = ClickTalkECommons();
                        Log.Information(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");
                        _lastActionUtc = now;
                        return;
                    }

                    if (!ssOpen) return;

                    Log.Information($"[RR] SelectString opened for row {_runOrder[_runPos]}; choosing Sell items.");
                    ClickSelectStringSellItems();

                    _phase = RunPhase.WaitingRetainerSellList;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellList:
                {
                    if (!sellListOpen) return;

                    Log.Information("[RR] Entered RetainerSellList.");

                    if (!_sellListCountCaptured)
                    {
                        _sellListCountCaptured = true;
                        var raw = _ui.ReadRetainerSellListCountText();
                        var listed = _ui.ReadRetainerSellListListedCount();
                        Log.Information($"[RR] Listed count captured = {listed?.ToString() ?? "null"} (raw='{raw}')");
                    }

                    _slotIndexToOpen = 0;
                    _phase = RunPhase.OpeningFirstSellItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.OpeningFirstSellItem:
                {
                    if (!sellListOpen) return;

                    var idx = Math.Clamp(_slotIndexToOpen, 0, 19);
                    if (!FireRetainerSellList_OpenItem(idx))
                        return;

                    _phase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSell:
                {
                    var ctxOpen = !GameGui.GetAddonByName("ContextMenu", 1).IsNull;
                    if (ctxOpen)
                    {
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    var sellOpen = !GameGui.GetAddonByName("RetainerSell", 1).IsNull;
                    if (!sellOpen) return;

                    Log.Information($"[RR] RetainerSell opened for slot {_slotIndexToOpen}.");
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingReturn:
                {
                    if (rlOpen && !sellListOpen && !ssOpen)
                    {
                        Log.Information("[RR] Back on RetainerList; ready for next retainer.");
                        _phase = RunPhase.NeedOpen;
                        _lastActionUtc = now;
                    }
                    return;
                }

            default:
                return;
        }
    }

    // =========================================================
    // Window helpers
    // =========================================================
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => MainWindow.EnsureIntentOpen();
}
