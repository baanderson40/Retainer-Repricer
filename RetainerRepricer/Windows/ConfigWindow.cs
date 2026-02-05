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

    public void Dispose() { }

    public override void Draw()
    {
        // =========================================================
        // Overlay settings
        // =========================================================
        var overlayEnabled = _cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay on Retainer List", ref overlayEnabled))
        {
            _cfg.OverlayEnabled = overlayEnabled;

            if (!overlayEnabled)
                _cfg.OverlayWantsOpen = false;

            _cfg.Save();
        }

        var wantsOpen = _cfg.OverlayWantsOpen;
        if (ImGui.Checkbox("Overlay default open", ref wantsOpen))
        {
            _cfg.OverlayWantsOpen = wantsOpen;
            _cfg.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Overlay anchor offset");

        var ox = _cfg.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetX = ox;
            _cfg.Save();
        }

        var oy = _cfg.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetY = oy;
            _cfg.Save();
        }

        // =========================================================
        // Retainer enable/disable list
        // =========================================================
        ImGui.Separator();
        ImGui.TextUnformatted("Retainers");

        var keys = _cfg.RetainersEnabled.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            ImGui.TextUnformatted("Open the summoning bell retainer list, then reopen this config.");
            return;
        }

        if (ImGui.Button("Enable all"))
        {
            foreach (var k in keys) _cfg.RetainersEnabled[k] = true;
            _cfg.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable all"))
        {
            foreach (var k in keys) _cfg.RetainersEnabled[k] = false;
            _cfg.Save();
        }

        ImGui.Spacing();

        foreach (var name in keys)
        {
            var enabled = _cfg.RetainersEnabled[name];
            if (ImGui.Checkbox(name, ref enabled))
            {
                _cfg.RetainersEnabled[name] = enabled;
                _cfg.Save();
            }
        }
    }
}
