using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using RetainerRepricer.Services;

namespace RetainerRepricer.Windows;

public sealed partial class ConfigWindow
{
    private int _blocklistSavedPage;
    private int _vendorSavedPage;
    private string _vendorPendingAllowAllListId = string.Empty;
    private ulong[] _vendorPendingAllowAllKeys = Array.Empty<ulong>();

    private static bool BeginScreenFillingDropdown(string popupId, string label, bool preferBelow = false)
    {
        var viewport = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var workTop = viewport.WorkPos.Y;
        var workBottom = workTop + viewport.WorkSize.Y;
        var workRight = viewport.WorkPos.X + viewport.WorkSize.X;
        var buttonPosition = ImGui.GetCursorScreenPos();
        var width = Math.Max(120f * scale, Math.Min(ImGui.GetContentRegionAvail().X, workRight - buttonPosition.X));
        var buttonHeight = ImGui.GetFrameHeight();
        if (ImGui.Button($"{label}  ▼##{popupId}", new System.Numerics.Vector2(width, buttonHeight)))
            ImGui.OpenPopup(popupId);

        return BeginScreenFillingDropdownPopup(popupId, buttonPosition, width, buttonHeight, preferBelow);
    }

    private static bool BeginScreenFillingDropdownPopup(
        string popupId,
        System.Numerics.Vector2 buttonPosition,
        float width,
        float buttonHeight,
        bool preferBelow = false,
        bool fitContentHeight = false)
    {
        if (!ImGui.IsPopupOpen(popupId)) return false;

        var viewport = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var workTop = viewport.WorkPos.Y;
        var workBottom = workTop + viewport.WorkSize.Y;
        var workRight = viewport.WorkPos.X + viewport.WorkSize.X;
        const float edgePadding = 8f;
        var below = workBottom - (buttonPosition.Y + buttonHeight) - edgePadding * scale;
        var above = buttonPosition.Y - workTop - edgePadding * scale;
        var openBelow = (preferBelow && below > 0) || below >= above;
        var availableHeight = openBelow ? below : above;
        var height = fitContentHeight
            ? Math.Max(1f, availableHeight)
            : preferBelow && openBelow
                ? Math.Max(1f, availableHeight)
                : Math.Max(80f * scale, availableHeight);
        var x = Math.Clamp(buttonPosition.X, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, workRight - width));
        var y = openBelow ? buttonPosition.Y + buttonHeight : fitContentHeight ? workTop : buttonPosition.Y - height;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(x, y), ImGuiCond.Always);
        if (fitContentHeight)
        {
            ImGui.SetNextWindowSizeConstraints(
                new System.Numerics.Vector2(width, 0f),
                new System.Numerics.Vector2(width, height));
        }
        else
        {
            var size = new System.Numerics.Vector2(width, height);
            ImGui.SetNextWindowSizeConstraints(size, size);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        }
        return ImGui.BeginPopup(popupId,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse |
            (fitContentHeight
                ? ImGuiWindowFlags.AlwaysAutoResize
                : preferBelow ? ImGuiWindowFlags.AlwaysVerticalScrollbar : ImGuiWindowFlags.None));
    }

    private void DrawBlocklistEditor()
    {
        var lists = _config.AutoListBlocklists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ImGui.TextUnformatted("CREATE LIST");
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        ImGui.InputText("New list name", ref _newBlocklistName, 100);
        ImGui.SameLine();
        if (ImGui.Button("Create") && _plugin.CanEditAutoListBlocklists)
        {
            var id = Guid.NewGuid().ToString("N");
            if (_config.TryCreateAutoListBlocklist(id, _newBlocklistName))
            {
                _selectedBlocklistId = id;
                _newBlocklistName = string.Empty;
                SaveConfig();
                lists = _config.AutoListBlocklists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else Plugin.Log.Warning("[RR][Blocklists] List name is empty or already used.");
        }
        if (lists.Count == 0)
        {
            ImGui.TextDisabled("No item blocklists yet. Create one to start.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("EDIT LIST");
        if (string.IsNullOrEmpty(_selectedBlocklistId) || !_config.AutoListBlocklists.ContainsKey(_selectedBlocklistId))
            _selectedBlocklistId = lists[0].Key;
        var selected = _config.AutoListBlocklists[_selectedBlocklistId];
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("List", selected.Name))
        {
            foreach (var pair in lists)
                if (ImGui.Selectable($"{pair.Value.Name} ({pair.Value.ItemKeys.Count})##{pair.Key}", pair.Key == _selectedBlocklistId))
                    _selectedBlocklistId = pair.Key;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var id = Guid.NewGuid().ToString("N");
            if (_config.TryCreateAutoListBlocklist(id, GetUniqueBlocklistName($"{selected.Name} (copy)"), selected.ItemKeys))
            {
                _selectedBlocklistId = id;
                SaveConfig();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete") && _plugin.CanEditAutoListBlocklists) ImGui.OpenPopup("Delete item blocklist?");
        if (ImGui.BeginPopupModal("Delete item blocklist?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var consumers = GetCharactersAssignedTo(_selectedBlocklistId).Count();
            ImGui.TextWrapped($"Delete '{selected.Name}' ({selected.ItemKeys.Count} entries)? It is assigned to {consumers} character(s); their affected items may become eligible for auto-listing.");
            if (!_plugin.CanEditAutoListBlocklists) ImGui.TextColored(new System.Numerics.Vector4(1, .5f, .3f, 1), "Stop automation before deleting a list.");
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
            if (ImGui.Button("Delete list"))
            {
                _config.AutoListBlocklists.Remove(_selectedBlocklistId);
                foreach (var settings in _config.Characters.Values) settings?.AutoListBlocklistIds?.Remove(_selectedBlocklistId);
                _selectedBlocklistId = string.Empty;
                SaveConfig();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        if (!string.Equals(_renameBlocklistId, _selectedBlocklistId, StringComparison.Ordinal))
        {
            _renameBlocklistId = _selectedBlocklistId;
            _renameBlocklistName = selected.Name;
        }
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        ImGui.InputText("Rename", ref _renameBlocklistName, 100);
        ImGui.SameLine();
        if (ImGui.Button("Apply name") && _plugin.CanEditAutoListBlocklists)
        {
            if (_config.TryRenameAutoListBlocklist(_selectedBlocklistId, _renameBlocklistName)) SaveConfig();
            else Plugin.Log.Warning("[RR][Blocklists] List name is empty or already used.");
        }
        ImGui.TextDisabled($"{selected.ItemKeys.Select(key => (uint)(key >> 1)).Distinct().Count()} items • {selected.ItemKeys.Count} quality entries • used by {GetCharactersAssignedTo(_selectedBlocklistId).Count()} characters");

        DrawBlocklistAddItems(selected);
        DrawBlocklistSavedItems(selected);
    }

    private void DrawBlocklistAddItems(Configuration.AutoListBlocklist list)
    {
        var categories = _blocklistCatalog!.Categories;
        if (!_blocklistCategoriesInitialized)
        {
            _blocklistCategories.UnionWith(categories);
            _blocklistCategoriesInitialized = true;
        }
        if (!BeginScreenFillingDropdown("##blocklist_add_items", "Add Items")) return;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##blocklist_search", "Search item name or ID", ref _blocklistSearch, 100);
        var showFilters = _blocklistFiltersOpen;
        if (ImGui.Button(showFilters ? "Filters ▲" : "Filters ▼"))
            _blocklistFiltersOpen = !showFilters;
        ImGui.SameLine();
        ImGui.Checkbox("Show blocked only", ref _blocklistOnly);
        if (_blocklistFiltersOpen)
        {
            ImGui.BeginChild("##blocklist_add_filters", new System.Numerics.Vector2(0, 190f * ImGuiHelpers.GlobalScale), true);
            ImGui.SetNextItemWidth(BlocklistInputWidth);
            if (ImGui.BeginCombo("##blocklist_categories", _blocklistCategories.Count == categories.Count ? "All categories" : $"{_blocklistCategories.Count} categories"))
            {
                if (ImGui.SmallButton("All")) { _blocklistCategories.Clear(); _blocklistCategories.UnionWith(categories); }
                ImGui.SameLine();
                if (ImGui.SmallButton("None")) _blocklistCategories.Clear();
                foreach (var category in categories)
                {
                    var included = _blocklistCategories.Contains(category);
                    if (ImGui.Checkbox(category, ref included))
                    {
                        if (included) _blocklistCategories.Add(category); else _blocklistCategories.Remove(category);
                    }
                }
                ImGui.EndCombo();
            }
            var itemType = _blocklistItemType switch { 1 => "Gear", 2 => "Non-gear", _ => "All types" };
            ImGui.SetNextItemWidth(BlocklistInputWidth);
            if (ImGui.BeginCombo("Item type", itemType))
            {
                if (ImGui.Selectable("All types", _blocklistItemType == 0)) _blocklistItemType = 0;
                if (ImGui.Selectable("Gear", _blocklistItemType == 1)) _blocklistItemType = 1;
                if (ImGui.Selectable("Non-gear", _blocklistItemType == 2)) _blocklistItemType = 2;
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(BlocklistInputWidth);
            if (ImGui.BeginCombo("Rarity", _blocklistRarities.Count == 0 ? "Any" : $"{_blocklistRarities.Count} selected"))
            {
                if (ImGui.SmallButton("Any")) _blocklistRarities.Clear();
                for (byte rarity = 1; rarity <= 5; rarity++)
                {
                    var chosen = _blocklistRarities.Contains(rarity);
                    var label = rarity switch { 1 => "White", 2 => "Green", 3 => "Blue", 4 => "Purple", _ => "Orange" };
                    if (ImGui.Checkbox($"{label} ({rarity})", ref chosen))
                    {
                        if (chosen) _blocklistRarities.Add(rarity); else _blocklistRarities.Remove(rarity);
                    }
                }
                ImGui.EndCombo();
            }
            var min = _blocklistMinItemLevel;
            var max = _blocklistMaxItemLevel;
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Min item level", ref min)) _blocklistMinItemLevel = Math.Clamp(min, 0, 999);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Max item level", ref max)) _blocklistMaxItemLevel = Math.Clamp(max, 0, 999);
            if (ImGui.SmallButton("Reset filters"))
            {
                _blocklistCategories.Clear(); _blocklistCategories.UnionWith(categories);
                _blocklistItemType = 0; _blocklistRarities.Clear();
                _blocklistMinItemLevel = 0; _blocklistMaxItemLevel = 999;
            }
            ImGui.EndChild();
        }

        var items = GetFilteredBlocklistItems(list).ToList();
        var filterSnapshot = string.Join("|", _selectedBlocklistId, _blocklistSearch, _blocklistItemType, _blocklistHqOnly, _blocklistNqOnly,
            _blocklistOnly, _blocklistMinItemLevel, _blocklistMaxItemLevel,
            string.Join(",", _blocklistCategories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", _blocklistRarities.OrderBy(value => value)));
        if (!string.Equals(filterSnapshot, _blocklistFilterSnapshot, StringComparison.Ordinal))
        {
            _blocklistFilterSnapshot = filterSnapshot;
            _blocklistPage = 0;
        }
        var targets = GetBlocklistTargets(items.Select(item => item.Id), _blocklistHqOnly, _blocklistNqOnly).ToList();
        var toBlock = targets.Where(target => !list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        var toUnblock = targets.Where(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        ImGui.TextDisabled($"{items.Count} matching items • {toUnblock.Select(target => target.ItemId).Distinct().Count()} blocked in this list");
        ImGui.TextUnformatted("Block quality:"); ImGui.SameLine();
        if (ImGui.RadioButton("Both", !_blocklistHqOnly && !_blocklistNqOnly)) { _blocklistHqOnly = false; _blocklistNqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("NQ", _blocklistNqOnly)) { _blocklistNqOnly = true; _blocklistHqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("HQ", _blocklistHqOnly)) { _blocklistHqOnly = true; _blocklistNqOnly = false; }
        var affected = GetCharactersAssignedTo(_selectedBlocklistId).ToArray();
        ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists || toBlock.Count == 0);
        if (ImGui.Button($"Block filtered ({toBlock.Count})")) RequestBlocklistMutation(toBlock, true, affected);
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists || toUnblock.Count == 0);
        if (ImGui.Button($"Unblock filtered ({toUnblock.Count})")) RequestBlocklistMutation(toUnblock, false, affected);
        ImGui.EndDisabled();
        var pageCount = Math.Max(1, (items.Count + BlocklistPageSize - 1) / BlocklistPageSize);
        _blocklistPage = Math.Clamp(_blocklistPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_blocklistPage == 0); if (ImGui.SmallButton("Previous")) _blocklistPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_blocklistPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_blocklistPage >= pageCount - 1); if (ImGui.SmallButton("Next")) _blocklistPage++; ImGui.EndDisabled();

        var height = Math.Max(160f, ImGui.GetContentRegionAvail().Y - 12f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginTable("##blocklist_add_table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new System.Numerics.Vector2(0, height)))
        {
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 78f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 125f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("In list", ImGuiTableColumnFlags.WidthFixed, 75f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in items.Skip(_blocklistPage * BlocklistPageSize).Take(BlocklistPageSize))
            {
                var itemTargets = GetBlocklistTargets(item, _blocklistHqOnly, _blocklistNqOnly).ToList();
                var blocked = itemTargets.Count(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)));
                var allBlocked = itemTargets.Count > 0 && blocked == itemTargets.Count;
                var mixed = blocked > 0 && blocked < itemTargets.Count;
                ImGui.TableNextRow(); ImGui.PushID((int)item.Id);
                ImGui.TableSetColumnIndex(0);
                var action = mixed ? "Mixed" : allBlocked ? "Unblock" : "Block";
                if (ImGui.Selectable($"{action}##block_add", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    var add = mixed || !allBlocked;
                    var mutation = itemTargets.Where(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)) != add).ToList();
                    RequestBlocklistMutation(mutation, add, affected);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(mixed ? "Mixed NQ/HQ state; click to block missing qualities." : $"Click to {(allBlocked ? "unblock" : "block")} selected qualities.");
                ImGui.TableSetColumnIndex(1);
                if (ImGui.Selectable($"{item.Name}##item", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    var add = mixed || !allBlocked;
                    var mutation = itemTargets.Where(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)) != add).ToList();
                    RequestBlocklistMutation(mutation, add, affected);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name} (#{item.Id})");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(GetListQualityStatus(list, item));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndPopup();
    }

    private void DrawBlocklistSavedItems(Configuration.AutoListBlocklist list)
    {
        var catalog = _blocklistCatalog!.Items.ToDictionary(item => item.Id);
        var saved = list.ItemKeys.Select(key => (Key: key, ItemId: (uint)(key >> 1)))
            .Where(entry => catalog.ContainsKey(entry.ItemId)).Select(entry => entry.ItemId).Distinct()
            .Select(id => catalog[id]).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToList();
        if (!BeginScreenFillingDropdown("##blocklist_saved_items_combo", $"Saved Items ({saved.Count} items, {list.ItemKeys.Count} qualities)")) return;
        var pageCount = Math.Max(1, (saved.Count + BlocklistPageSize - 1) / BlocklistPageSize);
        _blocklistSavedPage = Math.Clamp(_blocklistSavedPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_blocklistSavedPage == 0); if (ImGui.SmallButton("Previous saved")) _blocklistSavedPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_blocklistSavedPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_blocklistSavedPage >= pageCount - 1); if (ImGui.SmallButton("Next saved")) _blocklistSavedPage++; ImGui.EndDisabled();
        var height = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 12f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginTable("##blocklist_saved_items", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new System.Numerics.Vector2(0, height)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 82f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in saved.Skip(_blocklistSavedPage * BlocklistPageSize).Take(BlocklistPageSize))
            {
                ImGui.TableNextRow(); ImGui.PushID($"saved-{item.Id}");
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(GetListQualityStatus(list, item));
                ImGui.TableSetColumnIndex(3);
                ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
                if (ImGui.SmallButton("Remove"))
                {
                    list.ItemKeys.Remove(Configuration.MakeSellKey(item.Id, false));
                    list.ItemKeys.Remove(Configuration.MakeSellKey(item.Id, true));
                    SaveConfig();
                }
                ImGui.EndDisabled(); ImGui.PopID();
            }
            ImGui.EndTable();
        }
        var knownIds = catalog.Keys.ToHashSet();
        var unresolved = list.ItemKeys.Where(key => !knownIds.Contains((uint)(key >> 1))).ToList();
        if (unresolved.Count > 0 && ImGui.CollapsingHeader($"Unresolved saved entries ({unresolved.Count})"))
        {
            foreach (var key in unresolved)
            {
                ImGui.PushID($"unresolved-{key}");
                ImGui.BulletText($"Unknown item #{(uint)(key >> 1)} ({((key & 1UL) != 0 ? "HQ" : "NQ")})");
                ImGui.SameLine(); ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
                if (ImGui.SmallButton("Remove")) { list.ItemKeys.Remove(key); SaveConfig(); }
                ImGui.EndDisabled(); ImGui.PopID();
            }
        }
        ImGui.EndPopup();
    }

    private void DrawVendorItemEditor(Configuration.VendorAllowlist list)
    {
        if (BeginScreenFillingDropdown("##vendor_add_items", "Add Items", preferBelow: true))
        {
            DrawVendorAddItemsPopup(list);
            ImGui.EndPopup();
        }
        DrawVendorSavedItems(list);
    }

    private void DrawVendorAddItemsPopup(Configuration.VendorAllowlist list)
    {
        var categories = _vendorCatalog!.Categories;
        if (!_vendorCategoriesInitialized)
        {
            _vendorCategories.UnionWith(categories);
            _vendorCategoriesInitialized = true;
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##vendor_search", "Search item name or ID", ref _vendorSearch, 100);
        var showFilters = _vendorFiltersOpen;
        if (ImGui.Button(showFilters ? "Filters ▲" : "Filters ▼"))
            _vendorFiltersOpen = !showFilters;
        ImGui.SameLine();
        ImGui.Checkbox("Show allowed only", ref _vendorAllowedOnly);
        if (_vendorFiltersOpen)
        {
            ImGui.BeginChild("##vendor_add_filters", new System.Numerics.Vector2(0, 190f * ImGuiHelpers.GlobalScale), true);
            ImGui.SetNextItemWidth(VendorInputWidth);
            if (ImGui.BeginCombo("##vendor_categories", _vendorCategories.Count == categories.Count ? "All categories" : $"{_vendorCategories.Count} categories"))
            {
                if (ImGui.SmallButton("All")) { _vendorCategories.Clear(); _vendorCategories.UnionWith(categories); }
                ImGui.SameLine(); if (ImGui.SmallButton("None")) _vendorCategories.Clear();
                foreach (var category in categories)
                {
                    var included = _vendorCategories.Contains(category);
                    if (ImGui.Checkbox(category, ref included))
                    {
                        if (included) _vendorCategories.Add(category); else _vendorCategories.Remove(category);
                    }
                }
                ImGui.EndCombo();
            }
            var itemType = _vendorItemType switch { 1 => "Gear", 2 => "Non-gear", _ => "All types" };
            ImGui.SetNextItemWidth(VendorInputWidth);
            if (ImGui.BeginCombo("Item type", itemType))
            {
                if (ImGui.Selectable("All types", _vendorItemType == 0)) _vendorItemType = 0;
                if (ImGui.Selectable("Gear", _vendorItemType == 1)) _vendorItemType = 1;
                if (ImGui.Selectable("Non-gear", _vendorItemType == 2)) _vendorItemType = 2;
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(VendorInputWidth);
            if (ImGui.BeginCombo("Rarity", _vendorRarities.Count == 0 ? "Any" : $"{_vendorRarities.Count} selected"))
            {
                if (ImGui.SmallButton("Any")) _vendorRarities.Clear();
                for (byte rarity = 1; rarity <= 5; rarity++)
                {
                    var chosen = _vendorRarities.Contains(rarity);
                    var label = rarity switch { 1 => "White", 2 => "Green", 3 => "Blue", 4 => "Purple", _ => "Orange" };
                    if (ImGui.Checkbox($"{label} ({rarity})", ref chosen))
                    {
                        if (chosen) _vendorRarities.Add(rarity); else _vendorRarities.Remove(rarity);
                    }
                }
                ImGui.EndCombo();
            }
            var minLevel = _vendorMinItemLevel;
            var maxLevel = _vendorMaxItemLevel;
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Min item level", ref minLevel)) _vendorMinItemLevel = Math.Clamp(minLevel, 0, 999);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Max item level", ref maxLevel)) _vendorMaxItemLevel = Math.Clamp(maxLevel, 0, 999);
            if (ImGui.SmallButton("Reset filters"))
            {
                _vendorCategories.Clear(); _vendorCategories.UnionWith(categories);
                _vendorItemType = 0; _vendorRarities.Clear();
                _vendorMinItemLevel = 0; _vendorMaxItemLevel = 999;
            }
            ImGui.EndChild();
        }

        var items = GetFilteredVendorItems(list).ToList();
        var snapshot = string.Join("|", _vendorSelectedListId, _vendorSearch, _vendorItemType, _vendorHqOnly, _vendorNqOnly,
            _vendorAllowedOnly, _vendorMinItemLevel, _vendorMaxItemLevel,
            string.Join(",", _vendorCategories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", _vendorRarities.OrderBy(value => value)));
        if (!string.Equals(snapshot, _vendorFilterSnapshot, StringComparison.Ordinal))
        {
            _vendorFilterSnapshot = snapshot;
            _vendorPage = 0;
        }
        var targets = GetVendorTargets(items.Select(item => item.Id)).ToList();
        var toAllow = targets.Where(target => !list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        var toRemove = targets.Where(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        ImGui.TextDisabled($"{items.Count} matching eligible items • {toRemove.Select(target => target.ItemId).Distinct().Count()} allowed in list");
        ImGui.TextUnformatted("Allow quality:"); ImGui.SameLine();
        if (ImGui.RadioButton("Both", !_vendorHqOnly && !_vendorNqOnly)) { _vendorHqOnly = false; _vendorNqOnly = false; }
        ImGui.SameLine(); if (ImGui.RadioButton("NQ", _vendorNqOnly)) { _vendorNqOnly = true; _vendorHqOnly = false; }
        ImGui.SameLine(); if (ImGui.RadioButton("HQ", _vendorHqOnly)) { _vendorHqOnly = true; _vendorNqOnly = false; }
        if (ImGui.Button($"Allow filtered ({toAllow.Count})"))
        {
            _config.SetVendorAllowlistKeys(_vendorSelectedListId, toAllow.Select(target => Configuration.MakeSellKey(target.ItemId, target.Hq)), true);
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"Remove filtered ({toRemove.Count})"))
        {
            _config.SetVendorAllowlistKeys(_vendorSelectedListId, toRemove.Select(target => Configuration.MakeSellKey(target.ItemId, target.Hq)), false);
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Allow all eligible"))
        {
            _vendorPendingAllowAllListId = _vendorSelectedListId;
            _vendorPendingAllowAllKeys = _vendorCatalog!.Items.Where(item => item.NpcSalePrice > 0)
                .SelectMany(item => item.CanBeHq
                    ? new[] { Configuration.MakeSellKey(item.Id, false), Configuration.MakeSellKey(item.Id, true) }
                    : new[] { Configuration.MakeSellKey(item.Id, false) })
                .Distinct().ToArray();
            ImGui.OpenPopup("Confirm broad vendor allowlist");
        }
        if (ImGui.BeginPopupModal("Confirm broad vendor allowlist", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var additions = _vendorPendingAllowAllKeys.Count(key => _config.VendorAllowlists.TryGetValue(_vendorPendingAllowAllListId, out var target) && !target.ItemKeys.Contains(key));
            ImGui.TextWrapped($"Add {additions} item-quality entries to '{_config.VendorAllowlists.GetValueOrDefault(_vendorPendingAllowAllListId)?.Name}' from the current eligible catalog? New items are not auto-allowed. RR protections and threshold still apply; preview does not sell.");
            if (ImGui.Button("Add entries"))
            {
                if (_config.VendorAllowlists.TryGetValue(_vendorPendingAllowAllListId, out var target))
                {
                    _config.SetVendorAllowlistKeys(_vendorPendingAllowAllListId, _vendorPendingAllowAllKeys, true);
                    _config.Save();
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        var pageCount = Math.Max(1, (items.Count + VendorPageSize - 1) / VendorPageSize);
        _vendorPage = Math.Clamp(_vendorPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_vendorPage == 0); if (ImGui.SmallButton("Previous")) _vendorPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_vendorPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_vendorPage >= pageCount - 1); if (ImGui.SmallButton("Next")) _vendorPage++; ImGui.EndDisabled();
        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - 12f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginTable("##vendor_add_table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new System.Numerics.Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 78f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 125f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("In list", ImGuiTableColumnFlags.WidthFixed, 75f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in items.Skip(_vendorPage * VendorPageSize).Take(VendorPageSize))
            {
                var itemTargets = GetVendorTargets(new[] { item.Id }).ToList();
                var membershipCount = itemTargets.Count(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)));
                var all = itemTargets.Count > 0 && membershipCount == itemTargets.Count;
                var mixed = membershipCount > 0 && membershipCount < itemTargets.Count;
                ImGui.TableNextRow(); ImGui.PushID((int)item.Id);
                ImGui.TableSetColumnIndex(0);
                var action = mixed ? "Mixed" : all ? "Remove" : "Add";
                if (ImGui.Selectable($"{action}##vendor_add", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    var allow = mixed || !all;
                    foreach (var target in itemTargets)
                    {
                        var key = Configuration.MakeSellKey(target.ItemId, target.Hq);
                        if (allow) list.ItemKeys.Add(key); else list.ItemKeys.Remove(key);
                    }
                    _config.Save();
                }
                if (ImGui.IsItemHovered() && mixed) ImGui.SetTooltip("Mixed NQ/HQ state; click to allow the missing selected quality.");
                ImGui.TableSetColumnIndex(1);
                if (ImGui.Selectable($"{item.Name}##vendor_item", false, ImGuiSelectableFlags.DontClosePopups))
                {
                    var allow = mixed || !all;
                    foreach (var target in itemTargets)
                    {
                        var key = Configuration.MakeSellKey(target.ItemId, target.Hq);
                        if (allow) list.ItemKeys.Add(key); else list.ItemKeys.Remove(key);
                    }
                    _config.Save();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name} (#{item.Id}) • NPC sale {item.NpcSalePrice} gil");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(GetVendorQualityStatus(list, item));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void DrawVendorSavedItems(Configuration.VendorAllowlist list)
    {
        var catalog = _vendorCatalog!.Items.Where(item => item.NpcSalePrice > 0).ToDictionary(item => item.Id);
        var saved = list.ItemKeys.Select(key => (uint)(key >> 1)).Where(catalog.ContainsKey).Distinct()
            .Select(id => catalog[id]).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToList();
        if (!BeginScreenFillingDropdown("##vendor_saved_items_combo", $"Saved Items ({saved.Count} items, {list.ItemKeys.Count} qualities)", preferBelow: true)) return;
        var pageCount = Math.Max(1, (saved.Count + VendorPageSize - 1) / VendorPageSize);
        _vendorSavedPage = Math.Clamp(_vendorSavedPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_vendorSavedPage == 0); if (ImGui.SmallButton("Previous saved")) _vendorSavedPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_vendorSavedPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_vendorSavedPage >= pageCount - 1); if (ImGui.SmallButton("Next saved")) _vendorSavedPage++; ImGui.EndDisabled();
        var height = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 12f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginTable("##vendor_saved_table", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new System.Numerics.Vector2(0, height)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("NPC value", ImGuiTableColumnFlags.WidthFixed, 75f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in saved.Skip(_vendorSavedPage * VendorPageSize).Take(VendorPageSize))
            {
                ImGui.TableNextRow(); ImGui.PushID($"vendor-saved-{item.Id}");
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(GetVendorQualityStatus(list, item));
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.NpcSalePrice.ToString("N0"));
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton("Remove"))
                {
                    list.ItemKeys.Remove(Configuration.MakeSellKey(item.Id, false));
                    list.ItemKeys.Remove(Configuration.MakeSellKey(item.Id, true));
                    _config.Save();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        var known = catalog.Keys.ToHashSet();
        var unresolved = list.ItemKeys.Where(key => !known.Contains((uint)(key >> 1))).ToList();
        if (unresolved.Count > 0 && ImGui.CollapsingHeader($"Unresolved saved entries ({unresolved.Count})"))
        {
            foreach (var key in unresolved)
            {
                ImGui.PushID($"vendor-unresolved-{key}");
                ImGui.BulletText($"Item #{(uint)(key >> 1)} ({((key & 1UL) != 0 ? "HQ" : "NQ")})");
                ImGui.SameLine(); if (ImGui.SmallButton("Remove")) { list.ItemKeys.Remove(key); _config.Save(); }
                ImGui.PopID();
            }
        }
        ImGui.EndPopup();
    }
}
