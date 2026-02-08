using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerRepricer.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Configuration _cfg;

    public ConfigWindow(Plugin plugin)
        : base("Retainer Repricer Configuration##Config")
    {
        _plugin = plugin;
        _cfg = plugin.Configuration;

        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(360, 180),
            MaximumSize = new(800, 600),
        };
    }

    public void Dispose()
    {
        // nothing to dispose
    }

    public override void Draw()
    {
        DrawOverlaySettings();

        ImGui.Separator();

        DrawRetainerEnableList();
    }

    // =========================================================
    // Overlay settings
    // =========================================================
    private void DrawOverlaySettings()
    {
        var overlayEnabled = _cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay on Retainer List", ref overlayEnabled))
        {
            _cfg.OverlayEnabled = overlayEnabled;

            // If overlay is disabled, also force it closed.
            if (!overlayEnabled)
                _cfg.OverlayWantsOpen = false;

            SaveCfg();
        }

        var wantsOpen = _cfg.OverlayWantsOpen;
        if (ImGui.Checkbox("Overlay default open", ref wantsOpen))
        {
            _cfg.OverlayWantsOpen = wantsOpen;
            SaveCfg();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Overlay anchor offset");

        var ox = _cfg.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetX = ox;
            SaveCfg();
        }

        var oy = _cfg.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetY = oy;
            SaveCfg();
        }
    }

    // =========================================================
    // Retainer enable/disable list
    // =========================================================
    private void DrawRetainerEnableList()
    {
        ImGui.TextUnformatted("Retainers");

        var names = _cfg.RetainersEnabled.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            ImGui.TextUnformatted("Open the summoning bell retainer list, then reopen this config.");
            return;
        }

        DrawRetainerBulkButtons(names);

        ImGui.Spacing();

        foreach (var name in names)
            DrawRetainerToggle(name);
    }

    private void DrawRetainerBulkButtons(System.Collections.Generic.IReadOnlyList<string> names)
    {
        if (ImGui.Button("Enable all"))
        {
            foreach (var n in names)
                _cfg.RetainersEnabled[n] = true;

            SaveCfg();
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable all"))
        {
            foreach (var n in names)
                _cfg.RetainersEnabled[n] = false;

            SaveCfg();
        }
    }

    private void DrawRetainerToggle(string name)
    {
        var enabled = _cfg.RetainersEnabled[name];
        if (ImGui.Checkbox(name, ref enabled))
        {
            _cfg.RetainersEnabled[name] = enabled;
            SaveCfg();
        }
    }

    private void SaveCfg() => _cfg.Save();
}
