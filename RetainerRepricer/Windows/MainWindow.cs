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

    // Runtime state placeholder
    private bool _running;

    // Overlay size (keep consistent with anchor math)
    private const float OverlayW = 140f;
    private const float OverlayH = 38f;

    // Default anchor: raised above RetainerList
    private const float RaiseY = 38f;
    private const float MoveX = 4f;

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

    // Toggle via /repricer (intent only)
    public void ToggleIntent()
    {
        var wants = !_plugin.Configuration.OverlayWantsOpen;
        _plugin.Configuration.OverlayWantsOpen = wants;
        _plugin.Configuration.Save();
    }

    // Called by UiBuilder.OpenMainUi
    public void EnsureIntentOpen()
    {
        if (_plugin.Configuration.OverlayWantsOpen) return;

        _plugin.Configuration.OverlayWantsOpen = true;
        _plugin.Configuration.Save();
    }

    public override bool DrawConditions()
    {
        if (!_plugin.Configuration.OverlayEnabled)
            return false;

        if (!_plugin.Configuration.OverlayWantsOpen)
            return false;

        var retainerList = Plugin.GameGui.GetAddonByName("RetainerList", 1);
        if (retainerList.IsNull)
            return false;

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

        // Right-align overlay to RetainerList right edge, raise above by RaiseY
        var overlayX = (x + w) - MoveX - OverlayW + _plugin.Configuration.OverlayOffsetX;
        var overlayY = (y - RaiseY) + _plugin.Configuration.OverlayOffsetY;

        ImGui.SetNextWindowPos(new Vector2(overlayX, overlayY), ImGuiCond.Always);
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));

        // Start/Stop toggle
        var icon = _running ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        ImGui.PushID("rr-startstop");
        if (ImGuiComponents.IconButton(icon))
            _running = !_running;
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_running ? "Stop" : "Start");

        ImGui.SameLine();

        // Config
        ImGui.PushID("rr-config");
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            _plugin.ToggleConfigUi();
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Config");

        // Right-aligned status text
        ImGui.SameLine();
        var status = _running ? "Running" : "Idle";

        var avail = ImGui.GetContentRegionAvail().X;
        var textW = ImGui.CalcTextSize(status).X;
        ImGui.SameLine(MathF.Max(0, ImGui.GetCursorPosX() + avail - textW));

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);

        ImGui.PopStyleVar(2);
    }
}
