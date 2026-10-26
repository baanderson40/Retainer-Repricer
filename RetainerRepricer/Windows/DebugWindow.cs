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
    private readonly Plugin _plugin;
    private int _inventoryItemId;
    private bool _inventoryItemIsHq;

    public DebugWindow(Plugin plugin)
        : base("Retainer Repricer Debug###RetainerRepricerDebug")
    {
        _plugin = plugin;
        Size = new Vector2(620f, 420f);
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
        ImGui.TextWrapped("Diagnostics are written to the Retainer Repricer log buffer and Dalamud log.");
        ImGui.Separator();

        var universalis = _plugin.UniversalisDiagnostics;
        var cooldown = universalis.CooldownRemaining > TimeSpan.Zero
            ? $"Cooldown: {universalis.CooldownRemaining.TotalSeconds:0}s remaining"
            : "Cooldown: inactive";
        ImGui.TextUnformatted("Universalis session");
        ImGui.TextDisabled(cooldown);
        ImGui.TextDisabled(
            $"Requests: {universalis.OutboundRequests}  Cache hits: {universalis.CacheHits}  " +
            $"Suppressed: {universalis.SuppressedRequests}");
        ImGui.TextDisabled(
            $"Timeouts: {universalis.Timeouts}  Connections: {universalis.ConnectionFailures}  " +
            $"HTTP: {universalis.HttpFailures}  Rate limits: {universalis.RateLimitFailures}");
        ImGui.Spacing();

        ImGui.TextUnformatted("Market and retainer dumps");
        if (ImGui.Button("Dump Market Rows / HQ State"))
            _plugin.DumpMarketRowsForDebug();

        if (ImGui.GetContentRegionAvail().X >= 200f) ImGui.SameLine();
        if (ImGui.Button("Dump Retainer Rows"))
            _plugin.DumpRetainerRowsForDebug();

        if (ImGui.GetContentRegionAvail().X >= 200f) ImGui.SameLine();
        if (ImGui.Button("Test Universalis Gate"))
            _plugin.TestUniversalisGateForDebug();

        ImGui.Spacing();
        ImGui.TextUnformatted("Inventory slot dump");
        ImGui.SetNextItemWidth(150f);
        ImGui.InputInt("Item ID", ref _inventoryItemId);
        ImGui.SameLine();
        ImGui.Checkbox("HQ", ref _inventoryItemIsHq);
        ImGui.SameLine();
        _inventoryItemId = Math.Clamp(_inventoryItemId, 0, int.MaxValue);
        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Universalis Data"))
            _ = _plugin.DumpUniversalisDebugAsync((uint)_inventoryItemId, _inventoryItemIsHq);

        ImGui.SameLine();
        if (ImGui.Button("Dump Matching Inventory Slots"))
            _plugin.DumpInventoryByItemIdForDebug((uint)_inventoryItemId);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Matching InventoryGrid Renderers"))
            _plugin.DumpInventoryGridForDebug((uint)_inventoryItemId);
        ImGui.EndDisabled();

        ImGui.Spacing();
        if (ImGui.Button("Open Logs"))
            _plugin.OpenLogWindow();

        ImGui.Spacing();
        ImGui.TextUnformatted("Context menu diagnostics");
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

    public override void PreDraw() => RepricerUiStyle.Push();

    public override void PostDraw() => RepricerUiStyle.Pop();
}
