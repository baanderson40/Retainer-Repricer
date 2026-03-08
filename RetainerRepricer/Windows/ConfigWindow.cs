using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Common.Lua;
using System;
using System.Linq;

namespace RetainerRepricer.Windows;

/// <summary>
/// Presents configuration controls for enabled retainers, market settings, and sell list management.
/// </summary>
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

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Listed items will be put up for sale on any enabled retainer."
            );
        }

        DrawSellListSearch();

        var list = _config.SellList;
        var listCount = list?.Count ?? 0;

        DrawSellListActionRow(listCount);

        if (list == null || listCount == 0)
        {
            ImGui.TextDisabled("No items in sell list.");
            _confirmClearSellList = false;
            return;
        }

        ImGui.Separator();
        ImGui.Spacing();

        var rows = BuildFilteredSellListRows();
        ImGui.TextDisabled($"{rows.Count} item(s)");

        DrawSellListTable(rows, list.Count);
    }

    private void DrawSellListActionRow(int totalItemCount)
    {
        ImGui.Spacing();

        var startPos = ImGui.GetCursorPos();
        DrawSellListClearButtonInline(totalItemCount > 0);
        var afterClearY = ImGui.GetCursorPosY();

        var buttonWidth = 100f;
        var rightEdge = ImGui.GetWindowContentRegionMax().X;
        var buttonX = Math.Max(ImGui.GetCursorPosX(), rightEdge - buttonWidth);

        ImGui.SetCursorPos(new System.Numerics.Vector2(buttonX, startPos.Y));
        var statusBottom = DrawSmartSortControlsInline(buttonWidth);

        ImGui.SetCursorPosY(Math.Max(afterClearY, statusBottom));
    }

    private void DrawRetainersTab()
    {
        DrawRetainerEnableList();
    }

    private void DrawSettingsTab()
    {
        DrawPluginSettings();
        DrawOverlayOffsetSettings();
        DrawPricingGateSettings();
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

    private void DrawSellListClearButtonInline(bool hasItems)
    {
        if (!hasItems)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Clear list");
            ImGui.EndDisabled();
            return;
        }

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

    private float DrawSmartSortControlsInline(float buttonWidth)
    {
        var cursorX = ImGui.GetCursorPosX();
        var rightEdge = ImGui.GetWindowContentRegionMax().X;
        var buttonX = Math.Max(cursorX, rightEdge - buttonWidth);

        if (!_config.UseUniversalisApi)
        {
            ImGui.SetCursorPosX(buttonX);
            ImGui.BeginDisabled();
            ImGui.Button("Smart Sort", new System.Numerics.Vector2(buttonWidth, 0f));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Enable Universalis averages in Settings to access smart sorting.");
            ImGui.EndDisabled();
            return ImGui.GetCursorPosY();
        }

        var enabled = _plugin.SmartSortEnabled;
        var sorting = _plugin.SmartSortIsSorting;

        ImGui.SetCursorPosX(buttonX);
        ImGui.BeginDisabled(!enabled || sorting);
        if (ImGui.Button("Smart Sort", new System.Numerics.Vector2(buttonWidth, 0f)))
            _ = _plugin.RequestSmartSortAsync("manual_button", force: true);
        ImGui.EndDisabled();

        var afterButtonY = ImGui.GetCursorPosY();

        if (!enabled)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Enable smart sort in Settings to use this button.");
            return ImGui.GetCursorPosY();
        }

        if (sorting)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Smart sort is currently running.");
            return ImGui.GetCursorPosY();
        }

        return afterButtonY;
    }

    private System.Collections.Generic.List<Configuration.SellListEntry> BuildFilteredSellListRows()
    {
        var ordered = _config.GetSellListOrdered();

        var filter = (_sellListSearch ?? string.Empty).Trim();
        var filterLower = filter.ToLowerInvariant();
        var hasFilter = filterLower.Length > 0;

        static bool LooksLikeId(string s) => uint.TryParse(s, out _);

        uint idFilter = 0;
        var filterIsId = hasFilter && LooksLikeId(filterLower);
        if (filterIsId) uint.TryParse(filterLower, out idFilter);

        return ordered
            .Where(e =>
            {
                if (!hasFilter) return true;

                if (filterIsId)
                    return e.ItemId == idFilter;

                var name = e.Name ?? string.Empty;
                return name.ToLowerInvariant().Contains(filterLower);
            })
            .ToList();
    }

    private void DrawSellListTable(System.Collections.Generic.IReadOnlyList<Configuration.SellListEntry> rows, int totalItemCount)
    {
        var smartSortActive = _plugin.SmartSortEnabled;
        if (!ImGui.BeginTable(
                "##sell_list_table",
                4,
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.BordersV |
                ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Min Inv", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        (uint itemId, bool isHq)? removeKey = null;
        var priorityChanges = new System.Collections.Generic.List<(uint itemId, bool isHq, int newOrder)>();
        var rowIndex = 0;

        foreach (var e in rows)
        {
            ImGui.TableNextRow();

            // Use a stable ID scope per row so InputInt doesn’t collide.
            ImGui.PushID($"{e.ItemId}:{(e.IsHq ? 1 : 0)}");

            // Column 0: Priority input
            ImGui.TableSetColumnIndex(0);
            var priorityValue = e.SortOrder > 0 ? e.SortOrder : (rowIndex + 1);
            ImGui.SetNextItemWidth(-1);
            if (smartSortActive)
                ImGui.BeginDisabled();

            ImGui.InputInt(
                $"##priority_{e.ItemId}_{(e.IsHq ? 1 : 0)}",
                ref priorityValue,
                0,
                0);

            if (smartSortActive)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Priority is managed by Universalis smart sorting while enabled.");
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var clamped = Math.Clamp(priorityValue, 1, Math.Max(1, totalItemCount));
                if (!smartSortActive && clamped != e.SortOrder)
                    priorityChanges.Add((e.ItemId, e.IsHq, clamped));
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Lower numbers are processed first. Higher numbers move items later in the queue.");

            // Column 0: Item name
            ImGui.TableSetColumnIndex(1);
            var displayName = string.IsNullOrWhiteSpace(e.Name) ? $"(Unknown) [{e.ItemId}]" : e.Name;
            ImGui.TextUnformatted(displayName);

            // Column 1: Min inventory editor
            ImGui.TableSetColumnIndex(2);

            var s = e.MinCountToSell.ToString();
            ImGui.SetNextItemWidth(-1);

            if (ImGui.InputText($"##minsell_{e.ItemId}_{(e.IsHq ? 1 : 0)}", ref s, 8, ImGuiInputTextFlags.CharsDecimal))
            {
                if (int.TryParse(s, out var parsed))
                {
                    if (parsed < 1) parsed = 1;
                    if (parsed > 999) parsed = 999;

                    if (parsed != e.MinCountToSell)
                    {
                        e.MinCountToSell = parsed;
                        SaveConfig();
                    }
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Item will be listed only when your inventory count is at least this number.");

            // Column 2: Remove button
            ImGui.TableSetColumnIndex(3);
            if (ImGui.Button($"Remove##sell_{e.ItemId}_{(e.IsHq ? 1 : 0)}"))
                removeKey = (e.ItemId, e.IsHq);

            ImGui.PopID();
            rowIndex++;
        }

        ImGui.EndTable();

        if (priorityChanges.Count > 0)
        {
            var changed = false;
            foreach (var change in priorityChanges)
                changed |= _config.TrySetSellItemOrder(change.itemId, change.isHq, change.newOrder);

            if (changed)
                SaveConfig();
        }

        if (removeKey.HasValue)
        {
            _config.RemoveSellItem(removeKey.Value.itemId, removeKey.Value.isHq);
            SaveConfig();
        }
    }

    #endregion

    #region Retainers tab

    private void DrawRetainerEnableList()
    {
        ImGui.TextUnformatted("Manage retainers");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Enable repricing and selling per retainer."
            );
        }

        var names = _config.RetainersEnabled.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            ImGui.TextUnformatted("Open the summoning bell retainer list, then reopen this config.");
            return;
        }

        ImGui.Spacing();

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

    #region Plugin settings tab

    private void DrawPluginSettings()
    {
        var enabled = _config.PluginEnabled;
        if (ImGui.Checkbox("Enable plugin functionality", ref enabled))
        {
            _config.PluginEnabled = enabled;

            // If we disable the plugin, kill any active run immediately.
            if (!enabled && _plugin.IsRunning)
                _plugin.StopRun();

            SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Master switch for the plugin.\n" +
                "Disabling this stops any active run and disables automation, overlay, and context menu features."
            );
        }

        ImGui.Spacing();

        var closeRetainerList = _config.CloseRetainerListAddon;
        if (ImGui.Checkbox("Close Retainer List when finished", ref closeRetainerList))
        {
            _config.CloseRetainerListAddon = closeRetainerList;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "When enabled, the plugin closes RetainerList at the end of a run.\n" +
                "When disabled, it leaves it open."
            );
        }

        ImGui.Spacing();

        // Overlay toggle is separate from PluginEnabled, but PluginEnabled still gates display.
        var overlayEnabled = _config.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay window", ref overlayEnabled))
        {
            _config.OverlayEnabled = overlayEnabled;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "When enabled, an overlay will be added to the retainer list."
            );
        }

        ImGui.Spacing();
    }

    private void DrawOverlayOffsetSettings()
    {
        var showOffsets = _config.ShowOverlayOffsetControls;
        if (ImGui.Checkbox("Adjust overlay anchor", ref showOffsets))
        {
            _config.ShowOverlayOffsetControls = showOffsets;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Enable to fine-tune the overlay position relative to the retainer list.");
        }

        if (!showOffsets)
            return;

        ImGui.Indent();

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

        ImGui.Unindent();
    }

    private void DrawPricingGateSettings()
    {
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Universalis smart pricing");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Requires Universalis data to guard repricing and handle empty boards.");
        }

        var useUniversalis = _config.UseUniversalisApi;
        if (ImGui.Checkbox("Fetch Universalis averages", ref useUniversalis))
        {
            _config.UseUniversalisApi = useUniversalis;
            SaveConfig();
            _plugin.NotifySmartSortSettingChanged();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Master switch for Universalis-backed pricing safeguards and empty-board fallbacks."
            );
        }

        if (!useUniversalis)
        {
            ImGui.TextDisabled("Enable Universalis averages to configure smart pricing options.");
            return;
        }

        ImGui.Indent();

        var gateEnabled = _config.EnableUndercutPreventionGate;
        if (ImGui.Checkbox("Enable Universalis-backed price gate", ref gateEnabled))
        {
            _config.EnableUndercutPreventionGate = gateEnabled;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Compares in-game listings to Universalis averages and enforces the minimum price floor below."
            );
        }

        if (gateEnabled)
        {
            ImGui.Indent();

            ImGui.PushItemWidth(200f);
            var percent = _config.UndercutPreventionPercent * 100f;
            if (ImGui.SliderFloat("Floor %##universalis_floor_percent", ref percent, 10f, 90f, "%.0f"))
            {
                var normalized = Math.Clamp(percent / 100f, 0.1f, 0.9f);
                if (Math.Abs(normalized - _config.UndercutPreventionPercent) > 0.0001f)
                {
                    _config.UndercutPreventionPercent = normalized;
                    SaveConfig();
                }
            }
            ImGui.PopItemWidth();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Listings below this percent of the Universalis average will be ignored.");
            }

            ImGui.Unindent();
        }

        var fallbackEnabled = _config.UseUniversalisForEmptyMarket;
        if (ImGui.Checkbox("Use Universalis fallback for empty boards", ref fallbackEnabled))
        {
            _config.UseUniversalisForEmptyMarket = fallbackEnabled;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "When enabled, listings with no in-game competition will use Universalis averages before skipping."
            );
        }

        DrawSmartSortSettings();

        ImGui.Unindent();
    }

    private void DrawSmartSortSettings()
    {
        var smartSortEnabled = _config.EnableUniversalisSmartSort;
        if (ImGui.Checkbox("Enable smart sell sorting", ref smartSortEnabled))
        {
            _config.EnableUniversalisSmartSort = smartSortEnabled;
            SaveConfig();
            _plugin.NotifySmartSortSettingChanged();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Automatically reorder sell items using Universalis sale velocity and average price data.");
        }

        if (!smartSortEnabled)
            return;

        ImGui.Indent();

        ImGui.PushItemWidth(200f);

        var velocityWeight = _config.SmartSortVelocityWeight;
        if (ImGui.SliderFloat("Velocity weight", ref velocityWeight, 0.2f, 0.9f, "%.2f"))
        {
            velocityWeight = Math.Clamp(velocityWeight, 0.2f, 0.9f);
            _config.SmartSortVelocityWeight = velocityWeight;
            _config.SmartSortPriceWeight = 1f - velocityWeight;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Higher values favor fast-selling items; lower balances toward expensive items.");
        }

        var refreshMinutes = _config.SmartSortRefreshMinutes;
        if (ImGui.SliderInt("Auto refresh", ref refreshMinutes, 5, 180))
        {
            refreshMinutes = Math.Clamp(refreshMinutes, 5, 180);
            _config.SmartSortRefreshMinutes = refreshMinutes;
            SaveConfig();
        }

        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Smart sort will automatically refresh after this many minutes when a run starts.");
        }

        var (vw, pw) = _config.GetSmartSortWeights();
        ImGui.TextDisabled($"Active weights: velocity {vw:0.00}, price {pw:0.00}");

        var lastRun = _config.GetSmartSortLastRunUtc();
        var lastRunText = lastRun <= DateTime.MinValue ? "Never" : lastRun.ToLocalTime().ToString("g");
        ImGui.TextDisabled($"Last smart sort: {lastRunText}");

        ImGui.Unindent();
    }

    #endregion

    #region Save helpers

    private void SaveConfig() => _config.Save();

    #endregion
}
