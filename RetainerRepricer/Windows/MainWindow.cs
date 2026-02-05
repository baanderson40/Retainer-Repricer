using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;

namespace RetainerRepricer.Windows;

public sealed unsafe class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    // =========================================================
    // Overlay sizing / anchoring
    // =========================================================
    private const float OverlayW = 140f;
    private const float OverlayH = 38f;

    private const float RaiseY = 38f;
    private const float MoveX = 4f;

    // =========================================================
    // UI tick pacing (plugin has its own action pacing)
    // =========================================================
    private DateTime _lastTickUtc = DateTime.MinValue;
    private const double TickIntervalSeconds = 0.25;

    public MainWindow(Plugin plugin)
        : base(
            "Retainer Repricer##Overlay",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse
        )
    {
        _plugin = plugin;

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        // Keep registered so WindowSystem evaluates DrawConditions()
        IsOpen = true;
    }

    public void Dispose() { }

    // =========================================================
    // Intent (overlay wants open/closed)
    // =========================================================
    public void ToggleIntent()
    {
        var wants = !_plugin.Configuration.OverlayWantsOpen;
        _plugin.Configuration.OverlayWantsOpen = wants;
        _plugin.Configuration.Save();
    }

    public void EnsureIntentOpen()
    {
        if (_plugin.Configuration.OverlayWantsOpen) return;
        _plugin.Configuration.OverlayWantsOpen = true;
        _plugin.Configuration.Save();
    }

    // =========================================================
    // DrawConditions
    // =========================================================
    public override bool DrawConditions()
    {
        if (!_plugin.Configuration.OverlayEnabled)
            return false;

        if (!_plugin.Configuration.OverlayWantsOpen)
            return false;

        // IMPORTANT:
        // Tick even if RetainerList is not visible, or we could miss SelectString/SellList transitions.
        var now = DateTime.UtcNow;
        if (_plugin.IsRunning && (now - _lastTickUtc).TotalSeconds >= TickIntervalSeconds)
        {
            _plugin.TickRun();
            _lastTickUtc = now;
        }

        // Only DRAW/ANCHOR overlay when RetainerList is visible.
        var retainerList = Plugin.GameGui.GetAddonByName("RetainerList", 1);
        if (retainerList.IsNull)
            return false;

        // Sync roster into config while RetainerList is open.
        _plugin.TrySyncRetainersThrottled();

        ImGui.SetNextWindowSize(new Vector2(OverlayW, OverlayH), ImGuiCond.Always);
        AnchorToRetainerList((AtkUnitBase*)retainerList.Address);

        return true;
    }

    private void AnchorToRetainerList(AtkUnitBase* unit)
    {
        if (unit == null) return;

        var x = unit->X;
        var y = unit->Y;
        var w = unit->GetScaledWidth(true);

        var overlayX = (x + w) - MoveX - OverlayW + _plugin.Configuration.OverlayOffsetX;
        var overlayY = (y - RaiseY) + _plugin.Configuration.OverlayOffsetY;

        ImGui.SetNextWindowPos(new Vector2(overlayX, overlayY), ImGuiCond.Always);
    }

    // =========================================================
    // Draw
    // =========================================================
    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));

        // Start/Stop toggle
        var icon = _plugin.IsRunning ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        ImGui.PushID("rr-startstop");
        if (ImGuiComponents.IconButton(icon))
        {
            if (_plugin.IsRunning)
                _plugin.StopRun();
            else
                _plugin.StartRunFromRetainerList();
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_plugin.IsRunning ? "Stop" : "Start");

        ImGui.SameLine();

        // Config button
        ImGui.PushID("rr-config");
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            _plugin.ToggleConfigUi();
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Config");

        // Right-aligned status
        ImGui.SameLine();
        var status = _plugin.IsRunning ? "Running" : "Idle";

        var avail = ImGui.GetContentRegionAvail().X;
        var textW = ImGui.CalcTextSize(status).X;
        ImGui.SameLine(MathF.Max(0, ImGui.GetCursorPosX() + avail - textW));

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);

        ImGui.PopStyleVar(2);
    }
}
