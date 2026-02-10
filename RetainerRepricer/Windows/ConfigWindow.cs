using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerRepricer.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Configuration _cfg;
    private bool _confirmClearSellList = false;

    // UI-only state (not saved)
    private string _sellListSearch = string.Empty;

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
        if (ImGui.BeginTabBar("##rr_cfg_tabs"))
        {
            // Order requested: Sell List, Retainers, Settings
            if (ImGui.BeginTabItem("Sell List"))
            {
                DrawSellListTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Retainers"))
            {
                DrawRetainersTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // =========================================================
    // Tabs
    // =========================================================
    private void DrawSellListTab()
    {
        ImGui.TextUnformatted("Items to Sell");

        // Search (UI-only)
        ImGui.Spacing();
        ImGui.TextUnformatted("Search");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sell_search", "Filter by item name...", ref _sellListSearch, 128);

        ImGui.Spacing();

        var list = _cfg.SellList;
        if (list == null || list.Count == 0)
        {
            ImGui.TextDisabled("No items in sell list.");
            _confirmClearSellList = false;
            return;
        }

        // Clear list (2-step confirm)
        if (!_confirmClearSellList)
        {
            if (ImGui.Button("Clear list"))
                _confirmClearSellList = true;
        }
        else
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f), "Clear all items?");
            if (ImGui.Button("Confirm clear"))
            {
                _cfg.ClearSellList();
                _cfg.Save();
                _confirmClearSellList = false;
                return;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _confirmClearSellList = false;
        }

        ImGui.Separator();
        ImGui.Spacing();

        // Build filtered view
        var filter = (_sellListSearch ?? string.Empty).Trim();
        var filterLower = filter.ToLowerInvariant();
        var hasFilter = filterLower.Length > 0;

        bool LooksLikeId(string s) => uint.TryParse(s, out _);

        uint idFilter = 0;
        var filterIsId = hasFilter && LooksLikeId(filterLower);
        if (filterIsId) uint.TryParse(filterLower, out idFilter);

        var rows = list.Values
            .Where(e =>
            {
                if (!hasFilter) return true;

                if (filterIsId)
                    return e.ItemId == idFilter;

                var name = e.Name ?? string.Empty;
                return name.ToLowerInvariant().Contains(filterLower);
            })
            .OrderBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ToList();

        ImGui.TextDisabled($"{rows.Count} item(s)");

        // Table
        if (ImGui.BeginTable("##sell_list_table", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableHeadersRow();

            uint? removeId = null;

            foreach (var e in rows)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                var displayName = string.IsNullOrWhiteSpace(e.Name) ? $"(Unknown) [{e.ItemId}]" : e.Name;
                ImGui.TextUnformatted(displayName);

                ImGui.TableSetColumnIndex(1);
                if (ImGui.Button($"Remove##sell_{e.ItemId}"))
                    removeId = e.ItemId;
            }

            ImGui.EndTable();

            if (removeId.HasValue)
            {
                _cfg.RemoveSellItem(removeId.Value);
                _cfg.Save();
            }
        }
    }

    private void DrawRetainersTab()
    {
        DrawRetainerEnableList();
    }

    private void DrawSettingsTab()
    {
        DrawPluginSettings();
        ImGui.Separator();
        DrawOverlaySettings();
    }

    // =========================================================
    // Plugin settings
    // =========================================================
    private void DrawPluginSettings()
    {
        ImGui.TextUnformatted("Plugin");

        var enabled = _cfg.PluginEnabled;
        if (ImGui.Checkbox("Enable plugin functionality", ref enabled))
        {
            _cfg.PluginEnabled = enabled;

            // If we disable the plugin, force-stop any active run immediately.
            if (!enabled && _plugin.IsRunning)
                _plugin.StopRun();

            SaveCfg();
        }

        if (!_cfg.PluginEnabled)
        {
            ImGui.TextDisabled("Plugin is disabled: automation + overlay + context menu are off. Config remains available.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Run behavior");

        // NOTE: This requires a bool on Configuration, e.g.:
        // public bool CloseRetainerListAddon { get; set; } = true;
        var closeRetainerList = _cfg.CloseRetainerListAddon;
        if (ImGui.Checkbox("Close Retainer List when finished", ref closeRetainerList))
        {
            _cfg.CloseRetainerListAddon = closeRetainerList;
            SaveCfg();
        }

        ImGui.TextDisabled("When enabled, the plugin will close the RetainerList addon at the end of a run. When disabled, it leaves it open.");
    }

    // =========================================================
    // Overlay settings
    // =========================================================
    private void DrawOverlaySettings()
    {
        ImGui.TextUnformatted("Overlay");

        // Overlay should be controlled independently of PluginEnabled,
        // but if the plugin is disabled, overlay will not appear anyway.
        var overlayEnabled = _cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay window", ref overlayEnabled))
        {
            _cfg.OverlayEnabled = overlayEnabled;
            SaveCfg();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Overlay anchor offset");

        // Save-on-change for sliders, but only write to disk when the drag completes.
        var ox = _cfg.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetX = ox;

            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveCfg();
        }

        var oy = _cfg.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -200f, 200f, "%.0f"))
        {
            _cfg.OverlayOffsetY = oy;

            if (ImGui.IsItemDeactivatedAfterEdit())
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
