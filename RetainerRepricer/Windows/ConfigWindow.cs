using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;

namespace RetainerRepricer.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    #region Fields (plugin + config)

    private readonly Plugin _plugin;
    private readonly Configuration _config;

    #endregion

    #region UI-only state (not saved)

    private bool _confirmClearSellList;
    private string _sellListSearch = string.Empty;

    #endregion

    #region Lifecycle

    public ConfigWindow(Plugin plugin)
        : base("Retainer Repricer Configuration##Config")
    {
        _plugin = plugin;
        _config = plugin.Configuration;

        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(360, 180),
            MaximumSize = new(800, 600),
        };
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }

    #endregion

    #region Draw

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##rr_cfg_tabs"))
            return;

        // Order: Sell List, Retainers, Settings.
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

    #endregion

    #region Tabs

    private void DrawSellListTab()
    {
        ImGui.TextUnformatted("Items to Sell");

        DrawSellListSearch();

        var list = _config.SellList;
        if (list == null || list.Count == 0)
        {
            ImGui.TextDisabled("No items in sell list.");
            _confirmClearSellList = false;
            return;
        }

        DrawSellListClearButton();

        ImGui.Separator();
        ImGui.Spacing();

        var rows = BuildFilteredSellListRows();
        ImGui.TextDisabled($"{rows.Count} item(s)");

        DrawSellListTable(rows);
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

    #endregion

    #region Sell list tab

    private void DrawSellListSearch()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Search");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sell_search", "Filter by item name...", ref _sellListSearch, 128);
        ImGui.Spacing();
    }

    private void DrawSellListClearButton()
    {
        // Two-step confirm to avoid fat-finger clears.
        if (!_confirmClearSellList)
        {
            if (ImGui.Button("Clear list"))
                _confirmClearSellList = true;

            return;
        }

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f), "Clear all items?");
        if (ImGui.Button("Confirm clear"))
        {
            _config.ClearSellList();
            SaveConfig();
            _confirmClearSellList = false;
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _confirmClearSellList = false;
    }

    private System.Collections.Generic.List<Configuration.SellListEntry> BuildFilteredSellListRows()
    {
        var list = _config.SellList;

        var filter = (_sellListSearch ?? string.Empty).Trim();
        var filterLower = filter.ToLowerInvariant();
        var hasFilter = filterLower.Length > 0;

        static bool LooksLikeId(string s) => uint.TryParse(s, out _);

        uint idFilter = 0;
        var filterIsId = hasFilter && LooksLikeId(filterLower);
        if (filterIsId) uint.TryParse(filterLower, out idFilter);

        return list.Values
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
    }

    private void DrawSellListTable(System.Collections.Generic.IReadOnlyList<Configuration.SellListEntry> rows)
    {
        if (!ImGui.BeginTable(
                "##sell_list_table",
                2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersV))
            return;

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
            _config.RemoveSellItem(removeId.Value);
            SaveConfig();
        }
    }

    #endregion

    #region Plugin settings tab

    private void DrawPluginSettings()
    {
        ImGui.TextUnformatted("Plugin");

        var enabled = _config.PluginEnabled;
        if (ImGui.Checkbox("Enable plugin functionality", ref enabled))
        {
            _config.PluginEnabled = enabled;

            // If we disable the plugin, kill any active run immediately.
            if (!enabled && _plugin.IsRunning)
                _plugin.StopRun();

            SaveConfig();
        }

        if (!_config.PluginEnabled)
        {
            ImGui.TextDisabled("Plugin is disabled: automation + overlay + context menu are off. Config remains available.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Run behavior");

        var closeRetainerList = _config.CloseRetainerListAddon;
        if (ImGui.Checkbox("Close Retainer List when finished", ref closeRetainerList))
        {
            _config.CloseRetainerListAddon = closeRetainerList;
            SaveConfig();
        }

        ImGui.TextDisabled("When enabled, the plugin closes RetainerList at the end of a run. When disabled, it leaves it open.");
    }

    #endregion

    #region Overlay settings tab

    private void DrawOverlaySettings()
    {
        ImGui.TextUnformatted("Overlay");

        // Overlay toggle is separate from PluginEnabled, but PluginEnabled still gates display.
        var overlayEnabled = _config.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay window", ref overlayEnabled))
        {
            _config.OverlayEnabled = overlayEnabled;
            SaveConfig();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Overlay anchor offset");

        // Save-on-change for sliders, but only write to disk once the drag finishes.
        var ox = _config.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -200f, 200f, "%.0f"))
        {
            _config.OverlayOffsetX = ox;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }

        var oy = _config.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -200f, 200f, "%.0f"))
        {
            _config.OverlayOffsetY = oy;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }
    }

    #endregion

    #region Retainers tab

    private void DrawRetainerEnableList()
    {
        ImGui.TextUnformatted("Retainers");

        var names = _config.RetainersEnabled.Keys
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
                _config.RetainersEnabled[n] = true;

            SaveConfig();
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable all"))
        {
            foreach (var n in names)
                _config.RetainersEnabled[n] = false;

            SaveConfig();
        }
    }

    private void DrawRetainerToggle(string name)
    {
        var enabled = _config.RetainersEnabled[name];
        if (ImGui.Checkbox(name, ref enabled))
        {
            _config.RetainersEnabled[name] = enabled;
            SaveConfig();
        }
    }

    #endregion

    #region Save helpers

    private void SaveConfig() => _config.Save();

    #endregion
}
