using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerRepricer.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _cfg;

    public ConfigWindow(Plugin plugin)
        : base("Retainer Repricer Configuration##Config")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(360, 180),
            MaximumSize = new(800, 600)
        };

        _cfg = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = _cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay on Retainer List", ref enabled))
        {
            _cfg.OverlayEnabled = enabled;

            if (!enabled)
            {
                _cfg.OverlayWantsOpen = false;
            }

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
    }
}
