using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using RetainerRepricer.Services;

namespace RetainerRepricer.Windows;

public sealed partial class ConfigWindow
{
    private ItemBlocklistCatalog? _sellCatalog;
    private readonly HashSet<string> _sellCatalogCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<byte> _sellCatalogRarities = new();
    private string _sellCatalogSearch = string.Empty;
    private bool _sellCatalogFiltersOpen;
    private int _sellCatalogItemType;
    private int _sellCatalogMinLevel;
    private int _sellCatalogMaxLevel = 999;
    private int _sellCatalogQuality;
    private int _sellCatalogPage;
    private string _sellCatalogSnapshot = string.Empty;
    private SellCatalogTarget[] _sellCatalogPending = Array.Empty<SellCatalogTarget>();
    private ulong _sellCatalogPendingCharacter;
    private bool _sellCatalogConfirmRequested;
    private const int SellCatalogPageSize = 200;
    private readonly record struct SellCatalogTarget(ItemBlocklistCatalog.Entry Item, bool Hq);

    private void DrawSellListAddItems(ulong cid)
    {
        if (!BeginScreenFillingDropdown("##sell_catalog", "Add Items", preferBelow: true)) return;
        if (cid == 0)
        {
            ImGui.TextWrapped("Select a character to add items to its sell list.");
            ImGui.EndPopup();
            return;
        }
        if (_sellCatalog == null)
        {
            _sellCatalog = new ItemBlocklistCatalog();
            _sellCatalogCategories.UnionWith(_sellCatalog.Categories);
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##sell_catalog_search", "Search item name or ID", ref _sellCatalogSearch, 100);
        if (ImGui.Button(_sellCatalogFiltersOpen ? "Filters ▲" : "Filters ▼"))
            _sellCatalogFiltersOpen = !_sellCatalogFiltersOpen;
        if (_sellCatalogFiltersOpen) DrawSellCatalogFilters();

        ImGui.TextUnformatted("Add quality:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Both", _sellCatalogQuality == 0)) _sellCatalogQuality = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("NQ", _sellCatalogQuality == 1)) _sellCatalogQuality = 1;
        ImGui.SameLine();
        if (ImGui.RadioButton("HQ", _sellCatalogQuality == 2)) _sellCatalogQuality = 2;
        ImGui.TextWrapped($"Adding to: {GetCharacterDisplayName(cid, _config.Characters.GetValueOrDefault(cid))}. New entries start with Min Inv 1 and Keep enabled, so items absent from inventory survive auto-prune. Existing settings are unchanged.");

        var search = _sellCatalogSearch.Trim();
        var numeric = uint.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId);
        var items = _sellCatalog.Items.Where(item => _sellCatalogCategories.Contains(item.Category) &&
            (_sellCatalogItemType == 0 || (_sellCatalogItemType == 1 ? item.IsGear : !item.IsGear)) &&
            _sellCatalogMinLevel <= _sellCatalogMaxLevel && item.ItemLevel >= _sellCatalogMinLevel && item.ItemLevel <= _sellCatalogMaxLevel &&
            (_sellCatalogRarities.Count == 0 || _sellCatalogRarities.Contains(item.Rarity)) &&
            (_sellCatalogQuality != 2 || item.CanBeHq) &&
            (search.Length == 0 || (numeric ? item.Id == itemId : item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))).ToList();
        var snapshot = string.Join("|", cid, search, _sellCatalogItemType, _sellCatalogMinLevel, _sellCatalogMaxLevel, _sellCatalogQuality,
            string.Join(",", _sellCatalogCategories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", _sellCatalogRarities.OrderBy(value => value)));
        if (snapshot != _sellCatalogSnapshot)
        {
            _sellCatalogSnapshot = snapshot;
            _sellCatalogPage = 0;
        }
        var missing = items.SelectMany(GetSellCatalogTargets).Where(target => !_config.HasSellItem(target.Item.Id, target.Hq, cid)).ToArray();
        ImGui.TextDisabled($"{items.Count} matching items • {missing.Length} item-quality entries available to add");
        ImGui.BeginDisabled(missing.Length == 0);
        if (ImGui.Button($"Add filtered ({missing.Length})")) RequestSellCatalogAddition(missing, cid);
        ImGui.EndDisabled();

        var pageCount = Math.Max(1, (items.Count + SellCatalogPageSize - 1) / SellCatalogPageSize);
        _sellCatalogPage = Math.Clamp(_sellCatalogPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_sellCatalogPage == 0);
        if (ImGui.SmallButton("Previous")) _sellCatalogPage--;
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_sellCatalogPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_sellCatalogPage >= pageCount - 1);
        if (ImGui.SmallButton("Next")) _sellCatalogPage++;
        ImGui.EndDisabled();
        var height = Math.Max(150f, ImGui.GetContentRegionAvail().Y - 12f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginTable("##sell_catalog_items", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, height)))
        {
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 75f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 125f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("In list", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in items.Skip(_sellCatalogPage * SellCatalogPageSize).Take(SellCatalogPageSize))
            {
                var targets = GetSellCatalogTargets(item).Where(target => !_config.HasSellItem(item.Id, target.Hq, cid)).ToArray();
                ImGui.PushID((int)item.Id);
                ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                ImGui.BeginDisabled(targets.Length == 0);
                if (ImGui.Selectable(targets.Length == 0 ? "Added" : "Add", false, ImGuiSelectableFlags.DontClosePopups))
                    RequestSellCatalogAddition(targets, cid);
                ImGui.EndDisabled();
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name} (#{item.Id})");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Category);
                var nq = _config.HasSellItem(item.Id, false, cid);
                var hq = item.CanBeHq && _config.HasSellItem(item.Id, true, cid);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted((nq, hq) switch { (true, true) => "Both", (true, false) => "NQ", (false, true) => "HQ", _ => "—" });
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        // Open and render in the same popup scope, never a table row's PushID scope.
        if (_sellCatalogConfirmRequested)
        {
            ImGui.OpenPopup("Confirm sell list additions");
            _sellCatalogConfirmRequested = false;
        }
        if (ImGui.BeginPopupModal("Confirm sell list additions", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var sameCharacter = cid == _sellCatalogPendingCharacter && cid == _sellListEditingCharacter;
            ImGui.TextWrapped($"Add {_sellCatalogPending.Length} item-quality entries to {GetCharacterDisplayName(_sellCatalogPendingCharacter, _config.Characters.GetValueOrDefault(_sellCatalogPendingCharacter))}'s sell list?");
            ImGui.TextUnformatted("New entries use Min Inv 1 and Keep enabled. Existing settings stay unchanged.");
            if (!sameCharacter) ImGui.TextUnformatted("Character changed; cancel and select items again.");
            ImGui.BeginDisabled(!sameCharacter);
            if (ImGui.Button("Add entries"))
            {
                ApplySellCatalogAddition(_sellCatalogPending, _sellCatalogPendingCharacter);
                _sellCatalogPending = Array.Empty<SellCatalogTarget>();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _sellCatalogPending = Array.Empty<SellCatalogTarget>();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawSellCatalogFilters()
    {
        ImGui.BeginChild("##sell_catalog_filters", new Vector2(0, 190f * ImGuiHelpers.GlobalScale), true);
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("##sell_catalog_categories", _sellCatalogCategories.Count == _sellCatalog!.Categories.Count ? "All categories" : $"{_sellCatalogCategories.Count} categories"))
        {
            if (ImGui.SmallButton("All")) _sellCatalogCategories.UnionWith(_sellCatalog.Categories);
            ImGui.SameLine(); if (ImGui.SmallButton("None")) _sellCatalogCategories.Clear();
            foreach (var category in _sellCatalog.Categories)
            {
                var selected = _sellCatalogCategories.Contains(category);
                if (ImGui.Checkbox(category, ref selected))
                {
                    if (selected) _sellCatalogCategories.Add(category); else _sellCatalogCategories.Remove(category);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("Item type", _sellCatalogItemType switch { 1 => "Gear", 2 => "Non-gear", _ => "All types" }))
        {
            if (ImGui.Selectable("All types", _sellCatalogItemType == 0)) _sellCatalogItemType = 0;
            if (ImGui.Selectable("Gear", _sellCatalogItemType == 1)) _sellCatalogItemType = 1;
            if (ImGui.Selectable("Non-gear", _sellCatalogItemType == 2)) _sellCatalogItemType = 2;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("Rarity", _sellCatalogRarities.Count == 0 ? "Any" : $"{_sellCatalogRarities.Count} selected"))
        {
            if (ImGui.SmallButton("Any")) _sellCatalogRarities.Clear();
            for (byte rarity = 1; rarity <= 5; rarity++)
            {
                var selected = _sellCatalogRarities.Contains(rarity);
                var label = rarity switch { 1 => "White", 2 => "Green", 3 => "Blue", 4 => "Purple", _ => "Orange" };
                if (ImGui.Checkbox($"{label} ({rarity})", ref selected))
                {
                    if (selected) _sellCatalogRarities.Add(rarity); else _sellCatalogRarities.Remove(rarity);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Min item level", ref _sellCatalogMinLevel)) _sellCatalogMinLevel = Math.Clamp(_sellCatalogMinLevel, 0, 999);
        ImGui.SameLine(); ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Max item level", ref _sellCatalogMaxLevel)) _sellCatalogMaxLevel = Math.Clamp(_sellCatalogMaxLevel, 0, 999);
        if (ImGui.SmallButton("Reset filters"))
        {
            _sellCatalogCategories.Clear(); _sellCatalogCategories.UnionWith(_sellCatalog!.Categories);
            _sellCatalogRarities.Clear(); _sellCatalogItemType = 0;
            _sellCatalogMinLevel = 0; _sellCatalogMaxLevel = 999;
        }
        ImGui.EndChild();
    }

    private IEnumerable<SellCatalogTarget> GetSellCatalogTargets(ItemBlocklistCatalog.Entry item)
    {
        if (_sellCatalogQuality != 2) yield return new SellCatalogTarget(item, false);
        if (_sellCatalogQuality != 1 && item.CanBeHq) yield return new SellCatalogTarget(item, true);
    }

    private void RequestSellCatalogAddition(SellCatalogTarget[] targets, ulong cid)
    {
        if (targets.Length == 0 || cid == 0 || cid != _sellListEditingCharacter || _config.IsCharacterBlacklisted(cid)) return;
        if (targets.Length == 1) { ApplySellCatalogAddition(targets, cid); return; }
        _sellCatalogPending = targets;
        _sellCatalogPendingCharacter = cid;
        _sellCatalogConfirmRequested = true;
    }

    private void ApplySellCatalogAddition(IEnumerable<SellCatalogTarget> targets, ulong cid)
    {
        if (cid == 0 || cid != _sellListEditingCharacter || _config.IsCharacterBlacklisted(cid)) return;
        var added = 0;
        foreach (var target in targets)
        {
            var key = Configuration.MakeSellKey(target.Item.Id, target.Hq);
            var existed = _config.SellList.ContainsKey(key);
            if (!_config.TryAddSellItem(target.Item.Id, target.Hq, target.Item.Name, cid)) continue;
            if (!existed) _config.SetSellItemPreserveFlag(target.Item.Id, target.Hq, true);
            added++;
        }
        if (added == 0) return;
        SaveConfig();
        Plugin.Log.Information("[RR][SellCatalog] Added {Count} item-quality entries for cid={Cid}; new entries preserved from auto-prune.", added, cid);
        if (cid == _plugin.CurrentCharacterId && _plugin.SmartSortEnabled)
            _ = _plugin.RequestSmartSortAsync("item_added", force: true);
    }
}
