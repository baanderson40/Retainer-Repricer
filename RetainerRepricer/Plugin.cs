using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Windows;
using System;
using System.Collections.Generic;

// Alias to avoid "ValueType" naming collisions and keep callsites clean.
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

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
        NeedOpen,               // ready to click next retainer row in RetainerList
        WaitingSelectString,    // waiting for SelectString after clicking a retainer
        WaitingRetainerSellList,// waiting for RetainerSellList after choosing Sell items
        WaitingReturn,          // waiting for user to return to RetainerList
    }

    private RunPhase _phase = RunPhase.Idle;

    private readonly List<int> _runOrder = new(); // row indices (0-based)
    private int _runPos = -1;                     // current position into _runOrder

    private DateTime _lastActionUtc = DateTime.MinValue;
    private const double ActionIntervalSeconds = 0.75; // pacing between actions (safe while testing)

    // When we enter RetainerSellList, capture listed count once.
    private bool _sellListCountCaptured;

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
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        _ui = new Ui.UiReader(GameGui);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "toggle | config | count | mbdump (debug) | mbnodelist (debug) | rldump (debug)"
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
    }

    // =========================================================
    // UI Callbacks (FireCallback helpers)
    // =========================================================

    /// <summary>
    /// Click a retainer row in RetainerList.
    /// Observed args: Int=2, UInt=rowIndex, (Bool=true via callback/updateVisibility).
    /// </summary>
    private unsafe bool ClickRetainerByIndex(int index)
    {
        var addon = GameGui.GetAddonByName("RetainerList", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return false;

        var values = stackalloc AtkValue[3];

        values[0].Type = AtkValueType.Int;
        values[0].Int = 2;

        values[1].Type = AtkValueType.UInt;
        values[1].UInt = (uint)index;

        // Matches your manual working pattern more closely.
        values[2].Type = AtkValueType.Bool;
        values[2].Bool = true;

        unit->FireCallback(3, values, true);

        Log.Information($"[RL] FireCallback: (Int=2, UInt={index}, Bool=true)");
        return true;
    }

    /// <summary>
    /// SelectString -> choose "Sell items"
    /// Observed args: Int=2 (and close with Bool=true / updateVisibility)
    /// </summary>
    private unsafe bool ClickSelectStringSellItems()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return false;

        var values = stackalloc AtkValue[2];

        values[0].Type = AtkValueType.Int;
        values[0].Int = 2;

        values[1].Type = AtkValueType.Bool;
        values[1].Bool = true;

        unit->FireCallback(2, values, true);

        Log.Information("[SS] FireCallback: (Int=2, Bool=true)");
        return true;
    }

    // =========================================================
    // RetainerSellList helpers
    // =========================================================

    internal int? GetCurrentListedCount()
    {
        var raw = _ui.ReadAddonTextNode("RetainerSellList", Ui.NodePaths.RetainerSellList_CountNodeId);
        var count = ParseListedCount(raw);

        Log.Information($"[RSL] Listed count text='{raw}' parsed={count?.ToString() ?? "null"}");
        return count;
    }

    // =========================================================
    // Commands
    // =========================================================
    private unsafe void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        switch (a)
        {
            case "config":
                ToggleConfigUi();
                return;

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
                    var count = GetCurrentListedCount();
                    if (count == null)
                    {
                        Log.Warning("[Count] Listed count returned null (open RetainerSellList first).");
                        return;
                    }
                    Log.Information($"[Count] Listed = {count}");
                    return;
                }

            default:
                // /repricer (no args) toggles overlay intent
                MainWindow.ToggleIntent();
                return;
        }
    }

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

            var unit = ParseGil(unitRaw);
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

        // Pace all actions centrally so UI ticks can be faster than actions safely.
        var now = DateTime.UtcNow;
        if ((now - _lastActionUtc).TotalSeconds < ActionIntervalSeconds)
            return;

        var rlOpen = !GameGui.GetAddonByName("RetainerList", 1).IsNull;
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

                    _phase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
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
                        var listed = GetCurrentListedCount();
                        Log.Information($"[RR] Listed count captured = {listed?.ToString() ?? "null"}");
                    }

                    // For now, you manually exit back to RetainerList.
                    _phase = RunPhase.WaitingReturn;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingReturn:
                {
                    // Wait until we're back on RetainerList and other UIs are closed.
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
    // Parsing Helpers
    // =========================================================
    private static int? ParseGil(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        long value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > int.MaxValue) return int.MaxValue;
            }
        }

        return any ? (int)value : null;
    }

    public static int? ParseListedCount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Expect "19/20" but tolerate spaces or stray characters.
        int value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch == '/') break;

            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > 999) return 999; // sanity clamp
            }
        }

        return any ? value : null;
    }

    // =========================================================
    // UI helpers
    // =========================================================
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => MainWindow.EnsureIntentOpen();
}
