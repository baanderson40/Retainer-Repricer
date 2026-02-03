using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer.Windows;

public sealed unsafe class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    // User intent (mirrors config)
    private bool _wantsOpen;

    // Runtime state placeholder
    private bool _running = false;

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

        // Keep the window registered as open so WindowSystem will evaluate DrawConditions().
        IsOpen = true;

        _wantsOpen = _plugin.Configuration.OverlayWantsOpen;
    }

    public void Dispose() { }

    // Toggle via /repricer
    public void ToggleIntent()
    {
        _wantsOpen = !_wantsOpen;
        _plugin.Configuration.OverlayWantsOpen = _wantsOpen;
        _plugin.Configuration.Save();
    }

    // Used by UiBuilder.OpenMainUi to satisfy Dalamud validation
    public void EnsureIntentOpen()
    {
        if (_wantsOpen) return;

        _wantsOpen = true;
        _plugin.Configuration.OverlayWantsOpen = true;
        _plugin.Configuration.Save();
    }

    public override bool DrawConditions()
    {
        if (!_plugin.Configuration.OverlayEnabled)
            return false;

        // Config is authoritative
        _wantsOpen = _plugin.Configuration.OverlayWantsOpen;
        if (!_wantsOpen)
            return false;

        var retainerList = Plugin.GameGui.GetAddonByName("RetainerList", 1);
        if (retainerList.IsNull)
            return false;

        const float OverlayW = 140f;
        const float OverlayH = 38f;
        ImGui.SetNextWindowSize(new Vector2(OverlayW, OverlayH), ImGuiCond.Always);

        AnchorToRetainerList((AtkUnitBase*)retainerList.Address);
        return true;
    }

    private void AnchorToRetainerList(AtkUnitBase* unit)
    {
        if (unit == null) return;

        // RetainerList position + width
        var x = unit->X;
        var y = unit->Y;
        var w = unit->GetScaledWidth(true);

        // Change OverLay Left or Right. Increase to move left.
        const float OverlayW = 145f;

        // Default anchor behavior:
        // - right edge aligned to RetainerList right edge
        // - raised above the addon by RaiseY pixels
        // - change OverLay Up or Down. Increase to raise.
        const float RaiseY = 38f;

        var overlayX = (x + w) - OverlayW + _plugin.Configuration.OverlayOffsetX;
        var overlayY = (y - RaiseY) + _plugin.Configuration.OverlayOffsetY;

        ImGui.SetNextWindowPos(new Vector2(overlayX, overlayY), ImGuiCond.Always);
    }

    public override void Draw()
    {
        // Tight toolbar spacing
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));

        // Start/Stop single toggle
        var icon = _running ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        ImGui.PushID("rr-startstop");
        if (ImGuiComponents.IconButton(icon))
        {
            _running = !_running;
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_running ? "Stop" : "Start");

        ImGui.SameLine();

        // Config
        ImGui.SameLine();

        ImGui.PushID("rr-config");
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
        {
            _plugin.ToggleConfigUi();
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Config");

        ImGui.SameLine();

        // Minimal status text derived from state (no stored _status)
        var status = _running ? "Running" : "Idle";

        var avail = ImGui.GetContentRegionAvail().X;
        var textW = ImGui.CalcTextSize(status).X;

        // Push cursor to the right edge of the window
        ImGui.SameLine(MathF.Max(0, ImGui.GetCursorPosX() + avail - textW));

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);

        // Optional: if you later need it for anchoring math
        // var myHeight = ImGui.GetWindowSize().Y;

        ImGui.PopStyleVar(2);
    }
}
