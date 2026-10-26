using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using RetainerRepricer.Services;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace RetainerRepricer.Windows;

public sealed partial class ConfigWindow
{
    private static float BlocklistInputWidth => 220f * ImGuiHelpers.GlobalScale;

    private ItemBlocklistCatalog? _blocklistCatalog;
    private readonly HashSet<string> _blocklistCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<byte> _blocklistRarities = new();
    private string _blocklistSearch = string.Empty;
    private string _newBlocklistName = string.Empty;
    private string _renameBlocklistName = string.Empty;
    private string _renameBlocklistId = string.Empty;
    private string _selectedBlocklistId = string.Empty;
    private ulong _selectedBlocklistCharacter;
    private bool _blocklistHqOnly;
    private bool _blocklistNqOnly;
    private bool _blocklistOnly;
    private int _blocklistItemType;
    private bool _blocklistAssignmentsView;
    private bool _blocklistCategoriesInitialized;
    private bool _blocklistFiltersOpen;
    private int _blocklistPage;
    private string _blocklistFilterSnapshot = string.Empty;
    private const int BlocklistPageSize = 200;
    private int _blocklistMinItemLevel;
    private int _blocklistMaxItemLevel = 999;
    private bool _blocklistConfirmOpen;
    private string _blocklistConfirmId = string.Empty;
    private ulong[] _blocklistConfirmKeys = Array.Empty<ulong>();
    private ulong[] _blocklistConfirmCharacters = Array.Empty<ulong>();
    private bool _blocklistConfirmAdd;

    private readonly record struct BlocklistTarget(uint ItemId, bool Hq);

    private void DrawItemBlocklistsTab()
    {
        _blocklistCatalog ??= new ItemBlocklistCatalog();
        _config.AutoListBlocklists ??= new(StringComparer.OrdinalIgnoreCase);
        if (_selectedBlocklistCharacter == 0)
            _selectedBlocklistCharacter = _plugin.CurrentCharacterId;

        ImGui.TextDisabled("Blocks automatic inventory listing only. Existing listings and explicit Sell List assignments are unchanged.");
        if (ImGui.RadioButton("Manage lists", !_blocklistAssignmentsView) && _blocklistAssignmentsView)
        {
            _blocklistAssignmentsView = false;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Character assignments", _blocklistAssignmentsView) && !_blocklistAssignmentsView)
        {
            _blocklistAssignmentsView = true;
        }
        ImGui.Separator();

        if (_blocklistAssignmentsView)
            DrawBlocklistAssignments();
        else
            DrawBlocklistEditor();

        DrawBlocklistConfirmation();
    }

    private void DrawBlocklistEditorPrevious()
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
        }
        else
        {
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
                    {
                        _selectedBlocklistId = pair.Key;
                    }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Duplicate"))
            {
                var name = GetUniqueBlocklistName($"{selected.Name} (copy)");
                var id = Guid.NewGuid().ToString("N");
                _config.TryCreateAutoListBlocklist(id, name, selected.ItemKeys);
                _selectedBlocklistId = id;
                SaveConfig();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete") && _plugin.CanEditAutoListBlocklists)
                ImGui.OpenPopup("Delete item blocklist?");
            if (ImGui.BeginPopupModal("Delete item blocklist?", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var consumers = GetCharactersAssignedTo(_selectedBlocklistId).ToList();
                ImGui.TextWrapped($"Delete '{selected.Name}' ({selected.ItemKeys.Count} entries)? It is assigned to {consumers.Count} character(s); their affected items may become eligible for auto-listing.");
                if (!_plugin.CanEditAutoListBlocklists) ImGui.TextColored(new System.Numerics.Vector4(1, .5f, .3f, 1), "Stop automation before deleting a list.");
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.SameLine();
                ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
                if (ImGui.Button("Delete list"))
                {
                    _config.AutoListBlocklists.Remove(_selectedBlocklistId);
                    foreach (var settings in _config.Characters.Values)
                        settings?.AutoListBlocklistIds?.Remove(_selectedBlocklistId);
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
        }

        if (lists.Count == 0 || !_config.AutoListBlocklists.TryGetValue(_selectedBlocklistId, out var current)) return;

        ImGui.Separator();
        ImGui.TextUnformatted("FIND ITEMS");
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        ImGui.InputText("Search item name or ID", ref _blocklistSearch, 100);
        var categories = _blocklistCatalog!.Categories;
        if (!_blocklistCategoriesInitialized)
        {
            _blocklistCategories.UnionWith(categories);
            _blocklistCategoriesInitialized = true;
        }
        var activeFilterCount = (_blocklistCategories.Count != categories.Count ? 1 : 0) +
                                (_blocklistItemType != 0 ? 1 : 0) +
                                (_blocklistRarities.Count > 0 ? 1 : 0) +
                                (_blocklistMinItemLevel > 0 || _blocklistMaxItemLevel < 999 ? 1 : 0);
        if (ImGui.Button(activeFilterCount == 0 ? "Filters" : $"Filters ({activeFilterCount})"))
            _blocklistFiltersOpen = !_blocklistFiltersOpen;
        ImGui.SameLine();
        ImGui.Checkbox("Show blocked only", ref _blocklistOnly);
        if (_blocklistFiltersOpen)
        {
            ImGui.BeginChild("##blocklist_filters", new System.Numerics.Vector2(0, 190), true);
            if (ImGui.SmallButton("Reset filters"))
            {
                _blocklistCategories.Clear(); _blocklistCategories.UnionWith(categories);
                _blocklistItemType = 0;
                _blocklistRarities.Clear(); _blocklistMinItemLevel = 0; _blocklistMaxItemLevel = 999;
            }

            var itemTypeLabel = _blocklistItemType switch { 1 => "Gear only", 2 => "Non-gear only", _ => "All items" };
            if (ImGui.CollapsingHeader($"Item type ({itemTypeLabel})###blocklist_item_type"))
            {
                if (ImGui.RadioButton("All items", _blocklistItemType == 0)) _blocklistItemType = 0;
                if (ImGui.RadioButton("Gear only", _blocklistItemType == 1)) _blocklistItemType = 1;
                if (ImGui.RadioButton("Non-gear only", _blocklistItemType == 2)) _blocklistItemType = 2;
            }
            ImGui.Separator();

            var raritySummary = _blocklistRarities.Count == 0 ? "All" : $"{_blocklistRarities.Count} selected";
            if (ImGui.CollapsingHeader($"Rarity ({raritySummary})###blocklist_rarity"))
            {
                if (ImGui.SmallButton("All rarities")) _blocklistRarities.Clear();
                ImGui.SameLine();
                if (ImGui.SmallButton("Green+"))
                {
                    _blocklistRarities.Clear();
                    for (byte rarity = 2; rarity <= 5; rarity++) _blocklistRarities.Add(rarity);
                }
                for (byte rarity = 1; rarity <= 5; rarity++)
                {
                    var selectedRarity = _blocklistRarities.Contains(rarity);
                    var rarityLabel = rarity switch { 1 => "White", 2 => "Green", 3 => "Blue", 4 => "Purple", _ => "Orange" };
                    if (ImGui.Checkbox($"{rarityLabel} ({rarity})", ref selectedRarity))
                    {
                        if (selectedRarity) _blocklistRarities.Add(rarity); else _blocklistRarities.Remove(rarity);
                    }
                }
            }

            ImGui.Separator();
            var itemLevelSummary = _blocklistMinItemLevel == 0 && _blocklistMaxItemLevel == 999
                ? "Any" : $"{_blocklistMinItemLevel}–{_blocklistMaxItemLevel}";
            if (ImGui.CollapsingHeader($"Item level ({itemLevelSummary})###blocklist_item_level"))
            {
                ImGui.TextUnformatted("Item level (not required equip level)");
                var minLevel = _blocklistMinItemLevel;
                var maxLevel = _blocklistMaxItemLevel;
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("Minimum##itemlevel", ref minLevel))
                {
                    _blocklistMinItemLevel = Math.Clamp(minLevel, 0, 999);
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90);
                if (ImGui.InputInt("Maximum##itemlevel", ref maxLevel))
                {
                    _blocklistMaxItemLevel = Math.Clamp(maxLevel, 0, 999);
                }
                if (_blocklistMinItemLevel > _blocklistMaxItemLevel)
                    ImGui.TextColored(new System.Numerics.Vector4(1, .4f, .3f, 1), "Minimum must not exceed maximum.");
            }

            ImGui.Separator();
            var categorySummary = _blocklistCategories.Count == categories.Count
                ? "All" : $"{_blocklistCategories.Count} selected";
            if (ImGui.CollapsingHeader($"Categories ({categorySummary})###blocklist_categories"))
            {
                if (ImGui.SmallButton("All categories")) _blocklistCategories.UnionWith(categories);
                ImGui.SameLine();
                if (ImGui.SmallButton("No categories")) _blocklistCategories.Clear();
                foreach (var category in categories)
                {
                    var enabled = _blocklistCategories.Contains(category);
                    if (ImGui.Checkbox(category, ref enabled))
                    {
                        if (enabled) _blocklistCategories.Add(category); else _blocklistCategories.Remove(category);
                    }
                }
            }
            ImGui.EndChild();
        }
        var filteredItems = GetFilteredBlocklistItems(current).ToList();
        var filterSnapshot = string.Join("|", _selectedBlocklistId, _blocklistSearch, _blocklistItemType, _blocklistHqOnly, _blocklistNqOnly,
            _blocklistOnly, _blocklistMinItemLevel, _blocklistMaxItemLevel,
            string.Join(",", _blocklistCategories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", _blocklistRarities.OrderBy(value => value)));
        if (!string.Equals(filterSnapshot, _blocklistFilterSnapshot, StringComparison.Ordinal))
        {
            _blocklistFilterSnapshot = filterSnapshot;
            _blocklistPage = 0;
        }
        var filteredTargets = GetBlocklistTargets(filteredItems.Select(item => item.Id), _blocklistHqOnly, _blocklistNqOnly).ToList();
        var blockTargets = filteredTargets
            .Where(target => !current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)))
            .ToList();
        var unblockTargets = filteredTargets
            .Where(target => current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)))
            .ToList();
        var blockedItems = filteredTargets
            .Where(target => current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)))
            .Select(target => target.ItemId)
            .Distinct()
            .Count();
        ImGui.TextDisabled($"{filteredItems.Count} matching items • {blockedItems} blocked in this list");

        ImGui.TextUnformatted("Block quality:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Both", !_blocklistHqOnly && !_blocklistNqOnly)) { _blocklistHqOnly = false; _blocklistNqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("NQ", _blocklistNqOnly)) { _blocklistNqOnly = true; _blocklistHqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("HQ", _blocklistHqOnly)) { _blocklistHqOnly = true; _blocklistNqOnly = false; }

        var affected = GetCharactersAssignedTo(_selectedBlocklistId).ToArray();
        if (!_plugin.CanEditAutoListBlocklists) ImGui.TextColored(new System.Numerics.Vector4(1, .7f, .2f, 1), "Stop automation before editing blocklists.");
        ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists || blockTargets.Count == 0);
        if (ImGui.Button($"Block filtered ({blockTargets.Count} quality entries)")) RequestBlocklistMutation(blockTargets, true, affected);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists || unblockTargets.Count == 0);
        if (ImGui.Button($"Unblock filtered ({unblockTargets.Count} quality entries)")) RequestBlocklistMutation(unblockTargets, false, affected);
        ImGui.EndDisabled();

        var pageCount = Math.Max(1, (filteredItems.Count + BlocklistPageSize - 1) / BlocklistPageSize);
        _blocklistPage = Math.Clamp(_blocklistPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_blocklistPage == 0);
        if (ImGui.SmallButton("Previous")) _blocklistPage--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"Page {_blocklistPage + 1}/{pageCount}");
        ImGui.SameLine();
        ImGui.BeginDisabled(_blocklistPage >= pageCount - 1);
        if (ImGui.SmallButton("Next")) _blocklistPage++;
        ImGui.EndDisabled();

        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - 16f);
        if (ImGui.BeginTable("##blocklist_items", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new System.Numerics.Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Blocked", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("In list", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            var pageItems = filteredItems.Skip(_blocklistPage * BlocklistPageSize).Take(BlocklistPageSize);
            foreach (var item in pageItems)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)item.Id);
                ImGui.TableSetColumnIndex(0);
                var itemTargets = GetBlocklistTargets(item, _blocklistHqOnly, _blocklistNqOnly).ToList();
                var blockedCount = itemTargets.Count(target => current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)));
                var isBlocked = itemTargets.Count > 0 && blockedCount == itemTargets.Count;
                var isMixed = blockedCount > 0 && blockedCount < itemTargets.Count;
                if (isMixed)
                {
                    if (ImGui.SmallButton("Mixed##blocked"))
                    {
                        var missingTargets = itemTargets
                            .Where(target => !current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)))
                            .ToList();
                        RequestBlocklistMutation(missingTargets, true, affected);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mixed NQ/HQ state. Click to block both supported qualities.");
                }
                else
                {
                    var changed = ImGui.Checkbox("##blocked", ref isBlocked);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{(_blocklistHqOnly ? "HQ" : _blocklistNqOnly ? "NQ" : "Both qualities")}: click to {(isBlocked ? "unblock" : "block")}.");
                    if (changed)
                    {
                        var mutationTargets = itemTargets
                            .Where(target => current.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)) != isBlocked)
                            .ToList();
                        RequestBlocklistMutation(mutationTargets, isBlocked, affected);
                    }
                }
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name} (#{item.Id})");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(GetListQualityStatus(current, item));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        var knownIds = _blocklistCatalog!.Items.Select(item => item.Id).ToHashSet();
        var unresolved = current.ItemKeys.Select(key => (key, itemId: (uint)(key >> 1)))
            .Where(entry => !knownIds.Contains(entry.itemId)).ToList();
        if (unresolved.Count > 0 && ImGui.CollapsingHeader($"Unresolved saved entries ({unresolved.Count})"))
        {
            foreach (var (key, itemId) in unresolved)
            {
                ImGui.PushID($"unknown-{key}");
                ImGui.BulletText($"Unknown item #{itemId} ({((key & 1UL) != 0 ? "HQ" : "NQ")})");
                ImGui.SameLine();
                ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
                if (ImGui.SmallButton("Remove block"))
                {
                    current.ItemKeys.Remove(key);
                    SaveConfig();
                }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }

    }

    private IEnumerable<ItemBlocklistCatalog.Entry> GetFilteredBlocklistItems(Configuration.AutoListBlocklist list)
    {
        var search = _blocklistSearch.Trim();
        var numeric = uint.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId);
        return _blocklistCatalog!.Items.Where(item => _blocklistCategories.Contains(item.Category) &&
            (_blocklistItemType == 0 || (_blocklistItemType == 1 ? item.IsGear : !item.IsGear)) &&
            _blocklistMinItemLevel <= _blocklistMaxItemLevel &&
            item.ItemLevel >= _blocklistMinItemLevel && item.ItemLevel <= _blocklistMaxItemLevel &&
            (_blocklistRarities.Count == 0 || _blocklistRarities.Contains(item.Rarity)) &&
            (search.Length == 0 || (numeric ? item.Id == itemId : item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))) &&
            (!_blocklistHqOnly || item.CanBeHq) &&
            (!_blocklistOnly || (_blocklistHqOnly
                ? item.CanBeHq && list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true))
                : _blocklistNqOnly
                    ? list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false))
                    : list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false)) || (item.CanBeHq && list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true))))));
    }

    private string GetListQualityStatus(Configuration.AutoListBlocklist list, ItemBlocklistCatalog.Entry item)
    {
        var nq = list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false));
        var hq = list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true));
        return (nq, hq) switch { (true, true) => "Both", (true, false) => "NQ", (false, true) => "HQ", _ => "—" };
    }

    private IEnumerable<BlocklistTarget> GetBlocklistTargets(IEnumerable<uint> ids, bool hqOnly, bool nqOnly)
    {
        var byId = _blocklistCatalog!.Items.ToDictionary(item => item.Id);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item)) continue;
            foreach (var target in GetBlocklistTargets(item, hqOnly, nqOnly))
                yield return target;
        }
    }

    private static IEnumerable<BlocklistTarget> GetBlocklistTargets(ItemBlocklistCatalog.Entry item, bool hqOnly, bool nqOnly)
    {
        if (!hqOnly) yield return new BlocklistTarget(item.Id, false);
        if (!nqOnly && item.CanBeHq) yield return new BlocklistTarget(item.Id, true);
    }

    private void RequestBlocklistMutation(List<BlocklistTarget> targets, bool add, ulong[] characters)
    {
        var keys = targets.Select(target => Configuration.MakeSellKey(target.ItemId, target.Hq)).Distinct().ToArray();
        if (keys.Length == 0) return;
        if (keys.Length > 1 || characters.Length > 1)
        {
            _blocklistConfirmId = _selectedBlocklistId;
            _blocklistConfirmKeys = keys;
            _blocklistConfirmCharacters = characters;
            _blocklistConfirmAdd = add;
            _blocklistConfirmOpen = true;
            ImGui.OpenPopup("Confirm blocklist change");
            return;
        }
        ApplyBlocklistMutation(_selectedBlocklistId, keys, add);
    }

    private void DrawBlocklistConfirmation()
    {
        if (!_blocklistConfirmOpen || !ImGui.BeginPopupModal("Confirm blocklist change", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped($"{(_blocklistConfirmAdd ? "Block" : "Unblock")} {_blocklistConfirmKeys.Length} item-quality entries in '{_config.AutoListBlocklists.GetValueOrDefault(_blocklistConfirmId)?.Name}'. Affects {_blocklistConfirmCharacters.Length} assigned character(s). Category selection expands to current items only.");
        if (!_plugin.CanEditAutoListBlocklists) ImGui.TextColored(new System.Numerics.Vector4(1, .5f, .3f, 1), "Automation is active; operation cancelled.");
        if (ImGui.Button("Cancel") || !_plugin.CanEditAutoListBlocklists)
        {
            _blocklistConfirmOpen = false; ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
        if (ImGui.Button(_blocklistConfirmAdd ? "Block entries" : "Unblock entries"))
        {
            if (_config.AutoListBlocklists.ContainsKey(_blocklistConfirmId)) ApplyBlocklistMutation(_blocklistConfirmId, _blocklistConfirmKeys, _blocklistConfirmAdd);
            _blocklistConfirmOpen = false; ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled(); ImGui.EndPopup();
    }

    private void ApplyBlocklistMutation(string listId, IEnumerable<ulong> keys, bool add)
    {
        if (!_plugin.CanEditAutoListBlocklists) return;
        var changed = _config.SetBlocklistKeys(listId, keys, add);
        if (changed > 0) SaveConfig();
        Plugin.Log.Information("[RR][Blocklists] {Action} {Count} item-quality entries in list {ListId}.", add ? "Blocked" : "Unblocked", changed, listId);
    }

    private void DrawBlocklistAssignments()
    {
        var lists = _config.AutoListBlocklists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var characters = _plugin.GetAutoRetainerCharacters().Where(data => data.CID != 0).ToList();
        foreach (var pair in _config.Characters)
            if (pair.Key != 0 && characters.All(data => data.CID != pair.Key))
                characters.Add(new AutoRetainerAPI.Configuration.OfflineCharacterData { CID = pair.Key, Name = pair.Value?.Name ?? string.Empty, World = pair.Value?.World ?? string.Empty });
        characters = characters.DistinctBy(data => data.CID).OrderBy(data => GetCharacterDisplayName(data.CID, _config.Characters.GetValueOrDefault(data.CID)), StringComparer.OrdinalIgnoreCase).ToList();
        if (lists.Count == 0) { ImGui.TextDisabled("Create a blocklist first."); return; }
        if (_selectedBlocklistCharacter == 0 || characters.All(data => data.CID != _selectedBlocklistCharacter)) _selectedBlocklistCharacter = characters.FirstOrDefault()?.CID ?? 0;
        var currentName = GetCharacterDisplayName(_selectedBlocklistCharacter, _config.Characters.GetValueOrDefault(_selectedBlocklistCharacter));
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("Character", currentName))
        {
            foreach (var data in characters)
            {
                var label = GetCharacterDisplayName(data.CID, _config.GetCharacterSettings(data.CID, data.Name, data.World));
                if (ImGui.Selectable($"{label}##{data.CID}", data.CID == _selectedBlocklistCharacter)) _selectedBlocklistCharacter = data.CID;
            }
            ImGui.EndCombo();
        }
        if (_selectedBlocklistCharacter == 0) return;
        var settings = _config.GetCharacterSettings(_selectedBlocklistCharacter);
        var assignmentHeight = Math.Min(280f, Math.Max(120f, ImGui.GetContentRegionAvail().Y * 0.45f));
        if (ImGui.BeginChild("##blocklist_assignment_list", new System.Numerics.Vector2(0, assignmentHeight), true))
        {
            foreach (var pair in lists)
            {
                var assigned = settings.AutoListBlocklistIds.Contains(pair.Key);
                ImGui.BeginDisabled(!_plugin.CanEditAutoListBlocklists);
                if (ImGui.Checkbox($"{pair.Value.Name} ({pair.Value.ItemKeys.Count} entries)##assign-{pair.Key}", ref assigned))
                {
                    if (assigned) settings.AutoListBlocklistIds.Add(pair.Key); else settings.AutoListBlocklistIds.Remove(pair.Key);
                    SaveConfig();
                }
                ImGui.EndDisabled();
            }
        }
        ImGui.EndChild();
        var effective = _config.GetEffectiveAutoListBlocklist(_selectedBlocklistCharacter);
        ImGui.TextDisabled($"Effective blocklist: {effective.Count} item-quality entries.");
        if (ImGui.Button("View effective blocked items"))
        {
            _blocklistAssignmentsView = false;
            _selectedBlocklistId = lists.FirstOrDefault(pair => settings.AutoListBlocklistIds.Contains(pair.Key)).Key ?? string.Empty;
            _blocklistOnly = true;
        }
    }

    private IEnumerable<ulong> GetCharactersAssignedTo(string listId)
        => _config.Characters.Where(pair => pair.Value?.AutoListBlocklistIds?.Contains(listId) == true).Select(pair => pair.Key);

    private string GetUniqueBlocklistName(string baseName)
    {
        var name = baseName;
        var suffix = 2;
        while (_config.AutoListBlocklists.Values.Any(list => string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";
        return name;
    }

    internal void OpenBlocklistsForCharacter(ulong cid)
    {
        _selectedBlocklistCharacter = cid;
        _blocklistAssignmentsView = true;
        _pendingTabFocus = ConfigTabFocus.ItemBlocklists;
        IsOpen = true;
    }

    internal void OpenBlocklistsForItem(ulong cid, string listId, uint itemId)
    {
        _selectedBlocklistCharacter = cid;
        _selectedBlocklistId = listId;
        _blocklistAssignmentsView = false;
        _blocklistOnly = true;
        _blocklistSearch = itemId.ToString(CultureInfo.InvariantCulture);
        _pendingTabFocus = ConfigTabFocus.ItemBlocklists;
        IsOpen = true;
    }
}
