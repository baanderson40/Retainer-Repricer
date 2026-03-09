using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private bool _showAdvancedResetConfirmation;
    private DateTime _advancedResetConfirmationUtc;
    private static readonly Configuration DefaultConfig = new();

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

        // Order: Sell List, Retainers, Settings, Advanced (optional).
        if (ImGui.BeginTabItem("MB Sell List"))
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

        if (_config.ShowAdvancedSettingsTab && ImGui.BeginTabItem("Advanced"))
        {
            DrawAdvancedTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    #endregion

    #region Tabs

    private void DrawSellListTab()
    {
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

        var buttonWidth = 80f;
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
        DrawPricingGateSettings();
        DrawAdvancedTabToggle();
    }

    private void DrawAdvancedTab()
    {
        DrawAdvancedOverlaySection();
        ImGui.Spacing();
        DrawAdvancedPricingSection();
        ImGui.Spacing();
        DrawAdvancedAutomationSection();
        ImGui.Spacing();
        DrawAdvancedMarketTimingSection();
        ImGui.Spacing();
        DrawAdvancedHqTimingSection();
        ImGui.Spacing();
        DrawAdvancedSmartSortSection();
        ImGui.Spacing();
        DrawAdvancedResetControls();
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
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 30f);
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
            var priorityTooltip = smartSortActive
                ? "Priority is managed by smart sorting. The current order is shown for reference."
                : "Lower numbers are processed first. Higher numbers move items later in the queue.";

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
                    ImGui.SetTooltip(priorityTooltip);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var clamped = Math.Clamp(priorityValue, 1, Math.Max(1, totalItemCount));
                if (!smartSortActive && clamped != e.SortOrder)
                    priorityChanges.Add((e.ItemId, e.IsHq, clamped));
            }

            if (!smartSortActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(priorityTooltip);

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
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##sell_{e.ItemId}_{(e.IsHq ? 1 : 0)}"))
                removeKey = (e.ItemId, e.IsHq);
            ImGui.PopFont();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove from sell list");

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
            {
                Plugin.Log.Information("[RR][Config] Sell List priority changed.");
                SaveConfig();
            }
        }

        if (removeKey.HasValue)
        {
            _config.RemoveSellItem(removeKey.Value.itemId, removeKey.Value.isHq);
            Plugin.Log.Information("[RR][Config] Removed item {ItemId} (HQ={IsHq}) from Sell List", removeKey.Value.itemId, removeKey.Value.isHq);
            SaveConfig();
        }
    }

    #endregion

    #region Retainers tab

    private void DrawRetainerEnableList()
    {
        ImGui.TextDisabled("Disabling a retainer stops repricing and selling.");

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

            Plugin.Log.Information("[RR][Config] Enabled all {Count} retainers.", names.Count);
            SaveConfig();
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable all"))
        {
            foreach (var n in names)
                _config.RetainersEnabled[n] = false;

            Plugin.Log.Information("[RR][Config] Disabled all {Count} retainers.", names.Count);
            SaveConfig();
        }
    }

    private void DrawRetainerToggle(string name)
    {
        var enabled = _config.RetainersEnabled[name];
        if (ImGui.Checkbox(name, ref enabled))
        {
            _config.RetainersEnabled[name] = enabled;
            Plugin.Log.Information("[RR][Config] Retainer '{Name}' enabled={Enabled}", name, enabled);
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

            Plugin.Log.Information("[RR][Config] Plugin enabled={Enabled}", enabled);
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

        // Overlay toggle is separate from PluginEnabled, but PluginEnabled still gates display.
        var overlayEnabled = _config.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay window", ref overlayEnabled))
        {
            _config.OverlayEnabled = overlayEnabled;
            Plugin.Log.Information("[RR][Config] Overlay enabled={Enabled}", overlayEnabled);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "When enabled, an overlay will be added to the retainer list."
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
    }

    private void DrawOverlayOffsetSettings()
    {
        ImGui.PushItemWidth(200f);

        var ox = _config.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -1000f, 1000f, "%.0f"))
        {
            _config.OverlayOffsetX = ox;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }

        var oy = _config.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -1000f, 1000f, "%.0f"))
        {
            _config.OverlayOffsetY = oy;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }

        ImGui.PopItemWidth();
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
    }

    private void DrawAdvancedTabToggle()
    {
        ImGui.Separator();
        ImGui.Spacing();

        var showAdvanced = _config.ShowAdvancedSettingsTab;
        if (ImGui.Checkbox("Show advanced settings tab (expert)", ref showAdvanced))
        {
            _config.ShowAdvancedSettingsTab = showAdvanced;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Reveals low-level controls that can destabilize automation if misconfigured.");
        }
    }

    private void DrawAdvancedOverlaySection()
    {
        ImGui.TextUnformatted("Overlay positioning");
        ImGui.Separator();
        ImGui.Spacing();
        DrawOverlayOffsetSettings();
    }

    private void DrawAdvancedPricingSection()
    {
        ImGui.TextUnformatted("Pricing aggression");
        ImGui.Separator();
        ImGui.Spacing();

        var undercut = _config.UndercutAmount;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderInt("Undercut amount", ref undercut, 0, 10))
        {
            undercut = Math.Clamp(undercut, 0, 10);
            if (undercut != _config.UndercutAmount)
            {
                _config.UndercutAmount = undercut;
                SaveConfig();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Gil subtracted from the lowest competing listing when that listing is not yours.");
        }

        var validation = _config.MarketValidationThreshold;
        if (ImGui.SliderFloat("Market validation threshold", ref validation, 1.1f, 5f, "%.1f"))
        {
            validation = Math.Clamp(validation, 1.1f, 5f);
            if (Math.Abs(validation - _config.MarketValidationThreshold) > 0.0001f)
            {
                _config.MarketValidationThreshold = validation;
                SaveConfig();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("If Universalis averages exceed live market prices by more than this multiplier, the plugin trusts the market instead.");
        }

        if (!_config.UseUniversalisApi)
        {
            ImGui.PopItemWidth();
            ImGui.TextDisabled("Enable Universalis averages in Settings to adjust the floor.");
            return;
        }

        if (!_config.EnableUndercutPreventionGate)
        {
            ImGui.PopItemWidth();
            ImGui.TextDisabled("Enable the Universalis price gate in Settings to adjust the floor.");
            return;
        }

        var percent = _config.UndercutPreventionPercent * 100f;
        if (ImGui.SliderFloat("Universalis Avg Floor %", ref percent, 10f, 90f, "%.0f"))
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
    }

    private void DrawAdvancedSmartSortSection()
    {
        ImGui.TextUnformatted("Smart sort tuning");
        ImGui.Separator();
        ImGui.Spacing();

        if (!_config.EnableUniversalisSmartSort)
        {
            ImGui.TextDisabled("Enable smart sell sorting in Settings to adjust these sliders.");
            return;
        }

        ImGui.PushItemWidth(200f);

        DrawFloatSlider(
            "Velocity cap##adv_ss_velocity_cap",
            () => _config.SmartSortVelocityCap,
            v => _config.SmartSortVelocityCap = v,
            10f,
            500f,
            "%.0f",
            "Daily sale velocity is clamped to this value before normalization.");

        DrawFloatSlider(
            "Velocity log base##adv_ss_velocity_log",
            () => _config.SmartSortVelocityLogBase,
            v => _config.SmartSortVelocityLogBase = v,
            2f,
            500f,
            "%.0f",
            "Log base controlling how aggressive velocity scores ramp.");

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

        DrawFloatSlider(
            "Price log base##adv_ss_price_log",
            () => _config.SmartSortPriceLogBase,
            v => _config.SmartSortPriceLogBase = v,
            100f,
            200000f,
            "%.0f",
            "Log base controlling how expensive items contribute to the score.");

        var priceWeight = _config.SmartSortPriceWeight;
        if (ImGui.SliderFloat("Price weight", ref priceWeight, 0.1f, 0.8f, "%.2f"))
        {
            priceWeight = Math.Clamp(priceWeight, 0.1f, 0.8f);
            _config.SmartSortPriceWeight = priceWeight;
            _config.SmartSortVelocityWeight = Math.Clamp(1f - priceWeight, 0.2f, 0.9f);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Higher values favor expensive items; lower balances toward fast sellers.");
        }

        var refreshMinutes = _config.SmartSortRefreshMinutes;
        if (ImGui.SliderInt("Auto refresh", ref refreshMinutes, 5, 180))
        {
            refreshMinutes = Math.Clamp(refreshMinutes, 5, 180);
            _config.SmartSortRefreshMinutes = refreshMinutes;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Smart sort refreshes automatically if the last refresh is older than this many minutes when a run begins.");
        }

        ImGui.PopItemWidth();

        var lastRun = _config.GetSmartSortLastRunUtc();
        var lastRunText = lastRun <= DateTime.MinValue ? "Never" : lastRun.ToLocalTime().ToString("g");
        ImGui.TextDisabled($"Last smart sort: {lastRunText}");

        ImGui.TextDisabled("Smart sort formula");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "composite = (velocityScore × velocityWeight) + (priceScore × priceWeight)\n" +
                "velocityScore = log10(min(cap, velocity)+1) / log10(base)\n" +
                "priceScore = log10(price+1) / log10(base)\n" +
                "Example: an item with 0.5 avg velocity and ~100k avg price will have an ~0.5 composite score with default values."
            );
        }
    }

    private void DrawAdvancedAutomationSection()
    {
        ImGui.TextUnformatted("Automation pacing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "Framework tick interval##adv_framework_tick",
            () => _config.FrameworkTickIntervalSeconds,
            v => _config.FrameworkTickIntervalSeconds = v,
            0.02f,
            0.2f,
            "Outer guard on how frequently automation logic evaluates.");

        DrawSecondsSlider(
            "Action interval##adv_action_interval",
            () => _config.ActionIntervalSeconds,
            v => _config.ActionIntervalSeconds = v,
            0.05f,
            0.5f,
            "Delay between automation actions. Lower values are faster but risk UI instability.");

        DrawSecondsSlider(
            "Retainer sync interval##adv_retainer_sync",
            () => _config.RetainerSyncIntervalSeconds,
            v => _config.RetainerSyncIntervalSeconds = v,
            0.5f,
            10f,
            "How often enabled retainer names are refreshed.");
    }

    private void DrawAdvancedMarketTimingSection()
    {
        ImGui.TextUnformatted("Market board timing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "MB base interval##adv_mb_base",
            () => _config.MbBaseIntervalSeconds,
            v => _config.MbBaseIntervalSeconds = v,
            0.5f,
            5f,
            "Target pacing between Compare Prices requests.\nThis value is adjusted up or down automatically based on recent market throttles.");

        DrawSecondsSlider(
            "MB min interval##adv_mb_min",
            () => _config.MbIntervalMinSeconds,
            v => _config.MbIntervalMinSeconds = v,
            0.3f,
            5f,
            "Lower bound for market pacing adjustments.");

        DrawSecondsSlider(
            "MB max interval##adv_mb_max",
            () => _config.MbIntervalMaxSeconds,
            v => _config.MbIntervalMaxSeconds = v,
            0.5f,
            6f,
            "Upper bound for market pacing adjustments.");

        DrawFloatSlider(
            "MB jitter##adv_mb_jitter",
            () => _config.MbJitterMaxSeconds,
            v => _config.MbJitterMaxSeconds = v,
            0f,
            1f,
            "%.2f s",
            "Random variance added to market pacing so requests do not occur on a strict cadence.");

        DrawSecondsSlider(
            "ISR throttle backoff##adv_isr_backoff",
            () => _config.ItemSearchResultThrottleBackoffSeconds,
            v => _config.ItemSearchResultThrottleBackoffSeconds = v,
            0.5f,
            5f,
            "Wait applied after the market board tells you to retry.");

        DrawSecondsSlider(
            "No items settle delay##adv_isr_settle",
            () => _config.IsrNoItemsSettleSeconds,
            v => _config.IsrNoItemsSettleSeconds = v,
            0.1f,
            2f,
            "Grace period before trusting an empty market board result.");

        NormalizeMarketIntervals();
    }

    private void DrawAdvancedHqTimingSection()
    {
        ImGui.TextUnformatted("HQ filter timing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "Initial HQ delay##adv_hq_initial",
            () => _config.IsrHqFilterInitialDelaySeconds,
            v => _config.IsrHqFilterInitialDelaySeconds = v,
            0.2f,
            3f,
            "Wait after ItemSearchResult opens before attempting the HQ filter fallback.");

        DrawSecondsSlider(
            "HQ UI debounce##adv_hq_debounce",
            () => _config.IsrHqFilterUiDebounceSeconds,
            v => _config.IsrHqFilterUiDebounceSeconds = v,
            0.05f,
            1f,
            "Time the HQ filter UI must remain visible before interacting.");

        DrawSecondsSlider(
            "HQ open retry##adv_hq_retry",
            () => _config.IsrHqFilterOpenRetrySeconds,
            v => _config.IsrHqFilterOpenRetrySeconds = v,
            0.05f,
            1f,
            "Delay between attempts to open the HQ filter window.");

        DrawSecondsSlider(
            "HQ post-open wait##adv_hq_post",
            () => _config.IsrHqFilterPostOpenSeconds,
            v => _config.IsrHqFilterPostOpenSeconds = v,
            0.05f,
            1f,
            "Wait after opening the filter window before issuing actions.");
    }

    private void DrawSecondsSlider(string label, Func<double> getter, Action<double> setter, float min, float max, string tooltip, string format = "%.2f s")
    {
        var existing = getter();
        var value = (float)existing;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderFloat(label, ref value, min, max, format))
        {
            var clamped = Math.Clamp(value, min, max);
            var newValue = Math.Round(clamped, 4);
            if (Math.Abs(newValue - existing) > 0.0001d)
            {
                setter(newValue);
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawFloatSlider(string label, Func<double> getter, Action<double> setter, float min, float max, string format, string tooltip)
    {
        var existing = getter();
        var value = (float)existing;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderFloat(label, ref value, min, max, format))
        {
            var clamped = Math.Clamp(value, min, max);
            var newValue = Math.Round(clamped, 4);
            if (Math.Abs(newValue - existing) > 0.0001d)
            {
                setter(newValue);
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void NormalizeMarketIntervals()
    {
        var min = _config.MbIntervalMinSeconds;
        var max = _config.MbIntervalMaxSeconds;
        var baseInterval = _config.MbBaseIntervalSeconds;

        var changed = false;

        if (min > max)
        {
            max = min;
            changed = true;
        }

        if (baseInterval < min)
        {
            baseInterval = min;
            changed = true;
        }
        else if (baseInterval > max)
        {
            baseInterval = max;
            changed = true;
        }

        if (changed)
        {
            _config.MbIntervalMinSeconds = min;
            _config.MbIntervalMaxSeconds = max;
            _config.MbBaseIntervalSeconds = baseInterval;
            SaveConfig();
        }
    }

    private void DrawAdvancedResetControls()
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset advanced settings to defaults"))
        {
            ResetAdvancedSettingsToDefaults();
            _showAdvancedResetConfirmation = true;
            _advancedResetConfirmationUtc = DateTime.UtcNow;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Restores every advanced setting on this tab to its default value.");
        }

        if (_showAdvancedResetConfirmation)
        {
            if (DateTime.UtcNow - _advancedResetConfirmationUtc < TimeSpan.FromSeconds(3))
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.9f, 0.6f, 1f), "Defaults restored.");
            }
            else
            {
                _showAdvancedResetConfirmation = false;
            }
        }
    }

    private void ResetAdvancedSettingsToDefaults()
    {
        var changed = false;
        var smartSortChanged = false;

        if (Math.Abs(_config.OverlayOffsetX - DefaultConfig.OverlayOffsetX) > 0.001f)
        {
            _config.OverlayOffsetX = DefaultConfig.OverlayOffsetX;
            changed = true;
        }

        if (Math.Abs(_config.OverlayOffsetY - DefaultConfig.OverlayOffsetY) > 0.001f)
        {
            _config.OverlayOffsetY = DefaultConfig.OverlayOffsetY;
            changed = true;
        }

        if (_config.ShowOverlayOffsetControls)
        {
            _config.ShowOverlayOffsetControls = false;
            changed = true;
        }

        if (Math.Abs(_config.UndercutPreventionPercent - DefaultConfig.UndercutPreventionPercent) > 0.0001f)
        {
            _config.UndercutPreventionPercent = DefaultConfig.UndercutPreventionPercent;
            changed = true;
        }

        if (_config.UndercutAmount != DefaultConfig.UndercutAmount)
        {
            _config.UndercutAmount = DefaultConfig.UndercutAmount;
            changed = true;
        }

        if (Math.Abs(_config.MarketValidationThreshold - DefaultConfig.MarketValidationThreshold) > 0.0001f)
        {
            _config.MarketValidationThreshold = DefaultConfig.MarketValidationThreshold;
            changed = true;
        }

        if (Math.Abs(_config.ActionIntervalSeconds - DefaultConfig.ActionIntervalSeconds) > 0.0001d)
        {
            _config.ActionIntervalSeconds = DefaultConfig.ActionIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.RetainerSyncIntervalSeconds - DefaultConfig.RetainerSyncIntervalSeconds) > 0.0001d)
        {
            _config.RetainerSyncIntervalSeconds = DefaultConfig.RetainerSyncIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.FrameworkTickIntervalSeconds - DefaultConfig.FrameworkTickIntervalSeconds) > 0.0001d)
        {
            _config.FrameworkTickIntervalSeconds = DefaultConfig.FrameworkTickIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.ItemSearchResultThrottleBackoffSeconds - DefaultConfig.ItemSearchResultThrottleBackoffSeconds) > 0.0001d)
        {
            _config.ItemSearchResultThrottleBackoffSeconds = DefaultConfig.ItemSearchResultThrottleBackoffSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbBaseIntervalSeconds - DefaultConfig.MbBaseIntervalSeconds) > 0.0001d)
        {
            _config.MbBaseIntervalSeconds = DefaultConfig.MbBaseIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbIntervalMinSeconds - DefaultConfig.MbIntervalMinSeconds) > 0.0001d)
        {
            _config.MbIntervalMinSeconds = DefaultConfig.MbIntervalMinSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbIntervalMaxSeconds - DefaultConfig.MbIntervalMaxSeconds) > 0.0001d)
        {
            _config.MbIntervalMaxSeconds = DefaultConfig.MbIntervalMaxSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbJitterMaxSeconds - DefaultConfig.MbJitterMaxSeconds) > 0.0001d)
        {
            _config.MbJitterMaxSeconds = DefaultConfig.MbJitterMaxSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrNoItemsSettleSeconds - DefaultConfig.IsrNoItemsSettleSeconds) > 0.0001d)
        {
            _config.IsrNoItemsSettleSeconds = DefaultConfig.IsrNoItemsSettleSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterInitialDelaySeconds - DefaultConfig.IsrHqFilterInitialDelaySeconds) > 0.0001d)
        {
            _config.IsrHqFilterInitialDelaySeconds = DefaultConfig.IsrHqFilterInitialDelaySeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterUiDebounceSeconds - DefaultConfig.IsrHqFilterUiDebounceSeconds) > 0.0001d)
        {
            _config.IsrHqFilterUiDebounceSeconds = DefaultConfig.IsrHqFilterUiDebounceSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterOpenRetrySeconds - DefaultConfig.IsrHqFilterOpenRetrySeconds) > 0.0001d)
        {
            _config.IsrHqFilterOpenRetrySeconds = DefaultConfig.IsrHqFilterOpenRetrySeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterPostOpenSeconds - DefaultConfig.IsrHqFilterPostOpenSeconds) > 0.0001d)
        {
            _config.IsrHqFilterPostOpenSeconds = DefaultConfig.IsrHqFilterPostOpenSeconds;
            changed = true;
        }

        if (Math.Abs(_config.SmartSortVelocityWeight - DefaultConfig.SmartSortVelocityWeight) > 0.0001f)
        {
            _config.SmartSortVelocityWeight = DefaultConfig.SmartSortVelocityWeight;
            _config.SmartSortPriceWeight = DefaultConfig.SmartSortPriceWeight;
            changed = true;
            smartSortChanged = true;
        }

        if (_config.SmartSortRefreshMinutes != DefaultConfig.SmartSortRefreshMinutes)
        {
            _config.SmartSortRefreshMinutes = DefaultConfig.SmartSortRefreshMinutes;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortVelocityCap - DefaultConfig.SmartSortVelocityCap) > 0.0001d)
        {
            _config.SmartSortVelocityCap = DefaultConfig.SmartSortVelocityCap;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortVelocityLogBase - DefaultConfig.SmartSortVelocityLogBase) > 0.0001d)
        {
            _config.SmartSortVelocityLogBase = DefaultConfig.SmartSortVelocityLogBase;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortPriceLogBase - DefaultConfig.SmartSortPriceLogBase) > 0.0001d)
        {
            _config.SmartSortPriceLogBase = DefaultConfig.SmartSortPriceLogBase;
            changed = true;
            smartSortChanged = true;
        }

        if (changed)
        {
            SaveConfig();

            if (smartSortChanged)
                _plugin.NotifySmartSortSettingChanged();
        }
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
    }

    #endregion

    #region Save helpers

    private void SaveConfig() => _config.Save();

    #endregion
}
