using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerRepricer.Windows;

/// <summary>
/// Button-driven diagnostics for inspecting the game state used by repricing.
/// </summary>
public sealed class DebugWindow : Window, IDisposable
{
    private enum DebugSection
    {
        AutoRetainer,
        ContextMenus,
        Inventory,
        MarketAndRetainers,
        Universalis,
    }

    private readonly Plugin _plugin;
    private int _inventoryItemId;
    private bool _inventoryItemIsHq;
    private DebugSection _selectedSection = DebugSection.AutoRetainer;

    public DebugWindow(Plugin plugin)
        : base("Retainer Repricer Debug###RetainerRepricerDebug")
    {
        _plugin = plugin;
        Size = new Vector2(780f, 500f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    public void Dispose()
    {
    }

    public void Open()
    {
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Diagnostics are written to the Retainer Repricer log buffer and Dalamud log.");
        ImGui.SameLine();
        if (ImGui.Button("Open Logs"))
            _plugin.OpenLogWindow();
        ImGui.Separator();

        if (ImGui.BeginChild("##debug-navigation", new Vector2(175f, 0f), true))
        {
            ImGui.TextUnformatted("Sections");
            ImGui.Separator();
            DrawNavigationItem(DebugSection.AutoRetainer, "AutoRetainer");
            DrawNavigationItem(DebugSection.ContextMenus, "Context Menus");
            DrawNavigationItem(DebugSection.Inventory, "Inventory");
            DrawNavigationItem(DebugSection.MarketAndRetainers, "Market & Retainers");
            DrawNavigationItem(DebugSection.Universalis, "Universalis");
        }
        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##debug-actions", new Vector2(0f, 0f), true))
        {
            DrawSelectedSection();
        }
        ImGui.EndChild();
    }

    private void DrawNavigationItem(DebugSection section, string label)
    {
        if (ImGui.Selectable(label, _selectedSection == section))
            _selectedSection = section;
    }

    private void DrawSelectedSection()
    {
        switch (_selectedSection)
        {
            case DebugSection.AutoRetainer:
                DrawAutoRetainerSection();
                break;
            case DebugSection.ContextMenus:
                DrawContextMenuSection();
                break;
            case DebugSection.Inventory:
                DrawInventorySection();
                break;
            case DebugSection.MarketAndRetainers:
                DrawMarketAndRetainerSection();
                break;
            case DebugSection.Universalis:
                DrawUniversalisSection();
                break;
        }
    }

    private void DrawAutoRetainerSection()
    {
        ImGui.TextUnformatted("Retainer dialog dump (read-only)");
        ImGui.TextWrapped("Capture SelectString / SelectYesno AtkValues and button state. Watch samples changes every 100ms; no game actions. Text may contain character or item names.");
        if (ImGui.Button("Dump current retainer dialogs"))
            _plugin.RequestRetainerAddonDump();
        if (ImGui.Button("Watch retainer dialogs (60s)"))
            _plugin.ArmRetainerAddonCapture();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.RetainerAddonCaptureActive);
        if (ImGui.Button("Stop dialog capture"))
            _plugin.StopRetainerAddonCapture();
        ImGui.EndDisabled();
        ImGui.TextWrapped(_plugin.RetainerAddonDumpStatus);
        if (ImGui.Button("Read buyback warning sheets"))
            _plugin.RequestBuybackSheetDump();
        ImGui.TextWrapped("Read both localized custom-sheet candidates and compare with the open Yes/No prompt. Read-only; see Logs ([RR][BuybackSheets]).");
        ImGui.Separator();
        ImGui.TextUnformatted("AutoRetainer handoff diagnostics");
        ImGui.Separator();
        ImGui.TextDisabled(_plugin.AutoRetainerDiagnosticsEnabled
            ? "Automatic callback snapshots: enabled for this session"
            : "Automatic callback snapshots: disabled");
        if (ImGui.Button("Capture AR Snapshot"))
            _plugin.CaptureAutoRetainerDiagnosticSnapshot();
        ImGui.SameLine();
        if (_plugin.AutoRetainerDiagnosticsEnabled)
        {
            if (ImGui.Button("Disable Automatic Snapshots"))
                _plugin.SetAutoRetainerDiagnosticsEnabled(false);
        }
        else if (ImGui.Button("Enable Automatic Snapshots"))
        {
            _plugin.SetAutoRetainerDiagnosticsEnabled(true);
        }
    }

    private void DrawContextMenuSection()
    {
        ImGui.TextUnformatted("Context menu diagnostics");
        ImGui.Separator();
        ImGui.TextWrapped("Dump the open menu, or capture the next inventory context menu after right-clicking an item.");
        if (ImGui.Button("Dump open context menu"))
            _plugin.RequestOpenContextMenuDebugDump();
        if (ImGui.GetContentRegionAvail().X >= 250f) ImGui.SameLine();
        if (ImGui.Button("Capture next context menu"))
            _plugin.ArmNextContextMenuDebugDump();
        if (ImGui.GetContentRegionAvail().X >= 150f) ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.CanCancelContextMenuDebugDump);
        if (ImGui.Button("Cancel capture"))
            _plugin.CancelContextMenuDebugDump();
        ImGui.EndDisabled();
        ImGui.TextDisabled(_plugin.ContextMenuDebugDumpStatus);
    }

    private void DrawInventorySection()
    {
        ImGui.TextUnformatted("Inventory slot diagnostics");
        ImGui.Separator();
        DrawItemSelector();
        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Matching Inventory Slots"))
            _plugin.DumpInventoryByItemIdForDebug((uint)_inventoryItemId);
        if (ImGui.Button("Dump Matching InventoryGrid Renderers"))
            _plugin.DumpInventoryGridForDebug((uint)_inventoryItemId);
        ImGui.EndDisabled();
    }

    private void DrawMarketAndRetainerSection()
    {
        ImGui.TextUnformatted("Market and retainer dumps");
        ImGui.Separator();
        if (ImGui.Button("Dump Market Rows / HQ State"))
            _plugin.DumpMarketRowsForDebug();
        if (ImGui.Button("Dump Retainer Rows"))
            _plugin.DumpRetainerRowsForDebug();
    }

    private void DrawUniversalisSection()
    {
        var universalis = _plugin.UniversalisDiagnostics;
        var cooldown = universalis.CooldownRemaining > TimeSpan.Zero
            ? $"Cooldown: {universalis.CooldownRemaining.TotalSeconds:0}s remaining"
            : "Cooldown: inactive";
        ImGui.TextUnformatted("Universalis session");
        ImGui.Separator();
        ImGui.TextDisabled(cooldown);
        ImGui.TextDisabled(
            $"Requests: {universalis.OutboundRequests}  Cache hits: {universalis.CacheHits}  " +
            $"Suppressed: {universalis.SuppressedRequests}");
        ImGui.TextDisabled(
            $"Timeouts: {universalis.Timeouts}  Connections: {universalis.ConnectionFailures}  " +
            $"HTTP: {universalis.HttpFailures}  Rate limits: {universalis.RateLimitFailures}");
        if (ImGui.Button("Test Universalis Gate"))
            _plugin.TestUniversalisGateForDebug();
        ImGui.Spacing();
        ImGui.TextUnformatted("Item data");
        DrawItemSelector();
        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Universalis Data"))
            _ = _plugin.DumpUniversalisDebugAsync((uint)_inventoryItemId, _inventoryItemIsHq);
        ImGui.EndDisabled();
    }

    private void DrawItemSelector()
    {
        ImGui.SetNextItemWidth(180f);
        ImGui.InputInt("Item ID", ref _inventoryItemId);
        ImGui.SameLine();
        ImGui.Checkbox("HQ", ref _inventoryItemIsHq);
        _inventoryItemId = Math.Clamp(_inventoryItemId, 0, int.MaxValue);
    }

    public override void PreDraw() => RepricerUiStyle.Push();

    public override void PostDraw() => RepricerUiStyle.Pop();
}
