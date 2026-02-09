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

    public void Dispose()
    {
        // nothing to dispose
    }

    private static AtkUnitBase* GetVisibleUnit(string addonName)
    {
        var a = Plugin.GameGui.GetAddonByName(addonName, 1);
        if (a.IsNull) return null;

        var u = (AtkUnitBase*)a.Address;
        if (u == null || !u->IsVisible) return null;

        return u;
    }

    private static bool IsAddonVisible(string addonName)
        => GetVisibleUnit(addonName) != null;

    private static bool IsAddonOpen(string addonName)
        => !Plugin.GameGui.GetAddonByName(addonName, 1).IsNull;

    private bool AnyRepricerAddonVisible()
    {
        // “Flow” windows where we want the Stop button available
        if (IsAddonVisible("RetainerList")) return true;
        if (IsAddonVisible("RetainerSellList")) return true;
        if (IsAddonVisible("RetainerSell")) return true;
        if (IsAddonVisible("SelectString")) return true;
        if (IsAddonVisible("Talk")) return true;

        // Market windows can be open while Sell is open
        if (IsAddonOpen("ItemSearchResult")) return true;
        if (IsAddonOpen("ItemHistory")) return true;

        return false;
    }

    private static AtkUnitBase* GetBestAnchorUnit()
    {
        // Prefer anchoring to whichever of these is currently visible.
        // This keeps the overlay accessible after RetainerList disappears.
        var u = GetVisibleUnit("RetainerList");
        if (u != null) return u;

        u = GetVisibleUnit("RetainerSellList");
        if (u != null) return u;

        u = GetVisibleUnit("RetainerSell");
        if (u != null) return u;

        u = GetVisibleUnit("SelectString");
        if (u != null) return u;

        u = GetVisibleUnit("Talk");
        if (u != null) return u;

        return null;
    }

    // =========================================================
    // DrawConditions
    // =========================================================
    public override bool DrawConditions()
    {
        // Master plugin kill switch: no overlay when disabled
        if (!_plugin.Configuration.PluginEnabled)
            return false;

        // Overlay toggle: only controls this window
        if (!_plugin.Configuration.OverlayEnabled)
            return false;

        // Only show while we're in the retainer/sell/market flow,
        // so Stop is available even after RetainerList disappears.
        if (!AnyRepricerAddonVisible())
            return false;

        // Sync roster only when RetainerList is visible (same behavior as before)
        if (IsAddonVisible("RetainerList"))
            _plugin.TrySyncRetainersThrottled();

        var anchor = GetBestAnchorUnit();
        if (anchor == null)
            return false;

        ImGui.SetNextWindowSize(new Vector2(OverlayW, OverlayH), ImGuiCond.Always);
        AnchorToUnit(anchor);

        return true;
    }

    private void AnchorToUnit(AtkUnitBase* unit)
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

        DrawStartStopButton();
        ImGui.SameLine();
        DrawConfigButton();
        ImGui.SameLine();
        DrawRightAlignedStatusText();

        ImGui.PopStyleVar(2);
    }

    private void DrawStartStopButton()
    {
        var icon = _plugin.IsRunning ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        ImGui.PushID("rr-startstop");
        if (ImGuiComponents.IconButton(icon))
{
    if (_plugin.IsRunning)
    {
        _plugin.StopRun();
    }
    else
    {
        // Don't allow starting while plugin is disabled
        if (_plugin.Configuration.PluginEnabled)
            _plugin.StartRunFromRetainerList();
    }
}

        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_plugin.IsRunning ? "Stop" : "Start");
    }

    private void DrawConfigButton()
    {
        ImGui.PushID("rr-config");
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            _plugin.ToggleConfigUi();
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Config");
    }

    private void DrawRightAlignedStatusText()
    {
        var status = _plugin.IsRunning ? "Running" : "Idle";

        var avail = ImGui.GetContentRegionAvail().X;
        var textW = ImGui.CalcTextSize(status).X;

        ImGui.SameLine(MathF.Max(0, ImGui.GetCursorPosX() + avail - textW));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);
    }
}
