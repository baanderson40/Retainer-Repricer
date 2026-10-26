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
    private static float VendorInputWidth => 220f * ImGuiHelpers.GlobalScale;
    private const int VendorPageSize = 200;
    private ItemBlocklistCatalog? _vendorCatalog;
    private readonly HashSet<string> _vendorCategories = new(StringComparer.OrdinalIgnoreCase);
    private string _vendorListName = string.Empty;
    private string _vendorRenameName = string.Empty;
    private string _vendorRenameId = string.Empty;
    private string _vendorSelectedListId = string.Empty;
    private string _vendorSearch = string.Empty;
    private bool _vendorAssignmentsView;
    private bool _vendorCategoriesInitialized;
    private bool _vendorFiltersOpen;
    private bool _vendorAllowedOnly;
    private bool _vendorHqOnly;
    private bool _vendorNqOnly;
    private int _vendorItemType;
    private int _vendorPage;
    private string _vendorFilterSnapshot = string.Empty;
    private ulong _vendorSelectedCharacter;
    private bool _showVendorSkipped;

    private readonly record struct VendorTarget(uint ItemId, bool Hq);

    private void DrawVendorPreviewTab()
    {
        _vendorCatalog ??= new ItemBlocklistCatalog();
        _config.VendorAllowlists ??= new(StringComparer.OrdinalIgnoreCase);
        if (_vendorSelectedCharacter == 0) _vendorSelectedCharacter = _plugin.CurrentCharacterId;

        ImGui.TextDisabled("Preview and manual vending are available. Applying a list to the active character refreshes its preview; automatic selling requires the opt-in below.");
        var autoSell = _config.AutoSellVendorPreviewCandidates;
        if (ImGui.Checkbox("Automatically sell candidates after applying a list", ref autoSell))
        {
            _config.AutoSellVendorPreviewCandidates = autoSell;
            _config.Save();
        }
        ImGui.TextWrapped("When enabled, applying a vendor list to the active character refreshes a preview and may immediately sell eligible whole stacks to the currently open retainer. Manual preview refresh does not auto-sell. Sales are irreversible; RR protections and fresh price checks still apply; AR Protect List is not honored.");
        if (ImGui.RadioButton("Manage lists", !_vendorAssignmentsView)) _vendorAssignmentsView = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Character assignments", _vendorAssignmentsView)) _vendorAssignmentsView = true;
        ImGui.Separator();

        if (_vendorAssignmentsView) DrawVendorAssignments();
        else DrawVendorListEditor();
    }

    private void DrawVendorListEditor()
    {
        var lists = _config.VendorAllowlists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ImGui.TextUnformatted("CREATE LIST");
        ImGui.SetNextItemWidth(VendorInputWidth);
        ImGui.InputText("New list name", ref _vendorListName, 100);
        ImGui.SameLine();
        if (ImGui.Button("Create"))
        {
            var id = Guid.NewGuid().ToString("N");
            if (_config.TryCreateVendorAllowlist(id, _vendorListName))
            {
                _vendorSelectedListId = id;
                _vendorListName = string.Empty;
                _config.Save();
                lists = _config.VendorAllowlists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        if (lists.Count == 0)
        {
            ImGui.TextDisabled("No vendor allowlists yet. Create one to start.");
            DrawVendorPreviewControls();
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("EDIT LIST");
        if (string.IsNullOrEmpty(_vendorSelectedListId) || !_config.VendorAllowlists.ContainsKey(_vendorSelectedListId))
            _vendorSelectedListId = lists[0].Key;
        var selected = _config.VendorAllowlists[_vendorSelectedListId];
        ImGui.SetNextItemWidth(VendorInputWidth);
        if (ImGui.BeginCombo("List", selected.Name))
        {
            foreach (var pair in lists)
                if (ImGui.Selectable($"{pair.Value.Name} ({pair.Value.ItemKeys.Count})##{pair.Key}", pair.Key == _vendorSelectedListId))
                    _vendorSelectedListId = pair.Key;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var name = GetUniqueVendorListName($"{selected.Name} (copy)");
            var id = Guid.NewGuid().ToString("N");
            if (_config.TryCreateVendorAllowlist(id, name, selected.ItemKeys))
            {
                _vendorSelectedListId = id;
                _config.Save();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete")) ImGui.OpenPopup("Delete vendor allowlist?");
        if (ImGui.BeginPopupModal("Delete vendor allowlist?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var consumers = GetCharactersAssignedToVendorList(_vendorSelectedListId).Count();
            ImGui.TextWrapped($"Delete '{selected.Name}' ({selected.ItemKeys.Count} entries)? It is assigned to {consumers} character(s).");
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Delete list"))
            {
                _config.VendorAllowlists.Remove(_vendorSelectedListId);
                foreach (var settings in _config.Characters.Values) settings?.VendorAllowlistIds?.Remove(_vendorSelectedListId);
                _vendorSelectedListId = string.Empty;
                _config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!string.Equals(_vendorRenameId, _vendorSelectedListId, StringComparison.Ordinal))
        {
            _vendorRenameId = _vendorSelectedListId;
            _vendorRenameName = selected.Name;
        }
        ImGui.SetNextItemWidth(VendorInputWidth);
        ImGui.InputText("Rename", ref _vendorRenameName, 100);
        ImGui.SameLine();
        if (ImGui.Button("Apply name") && _config.TryRenameVendorAllowlist(_vendorSelectedListId, _vendorRenameName)) _config.Save();
        ImGui.TextDisabled($"{selected.ItemKeys.Select(key => (uint)(key >> 1)).Distinct().Count()} items • {selected.ItemKeys.Count} quality entries • used by {GetCharactersAssignedToVendorList(_vendorSelectedListId).Count()} characters");
        var maximumAllowed = GetVendorListThresholdCap(_vendorSelectedListId);
        if (selected.MaximumStackMarketValue > maximumAllowed)
        {
            selected.MaximumStackMarketValue = maximumAllowed;
            _config.Save();
        }
        var listThreshold = selected.MaximumStackMarketValue;
        ImGui.SetNextItemWidth(VendorInputWidth);
        if (ImGui.InputInt("Vendor value limit — total stack market value (gil)", ref listThreshold))
        {
            selected.MaximumStackMarketValue = Math.Clamp(listThreshold, 0, maximumAllowed);
            _config.Save();
        }
        ImGui.TextDisabled($"0 disables vending from this list. Items in multiple assigned lists use the lowest applicable list limit. Maximum for assigned characters: {maximumAllowed:N0} gil.");

        DrawVendorItemEditor(selected);
        DrawVendorPreviewControls(_vendorSelectedListId);
    }

    private void DrawVendorItemEditorPrevious(Configuration.VendorAllowlist list)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("FIND ITEMS");
        ImGui.SetNextItemWidth(VendorInputWidth);
        ImGui.InputText("Search item name or ID", ref _vendorSearch, 100);
        if (!_vendorCategoriesInitialized)
        {
            _vendorCategories.UnionWith(_vendorCatalog!.Categories);
            _vendorCategoriesInitialized = true;
        }
        var categories = _vendorCatalog!.Categories;
        if (ImGui.Button(_vendorFiltersOpen ? "Hide filters" : "Filters")) _vendorFiltersOpen = !_vendorFiltersOpen;
        ImGui.SameLine();
        ImGui.Checkbox("Show allowed only", ref _vendorAllowedOnly);
        if (_vendorFiltersOpen)
        {
            ImGui.BeginChild("##vendor_filters", new System.Numerics.Vector2(0, 155), true);
            if (ImGui.SmallButton("Reset filters"))
            {
                _vendorCategories.Clear(); _vendorCategories.UnionWith(categories);
                _vendorItemType = 0;
            }
            if (ImGui.RadioButton("All item types", _vendorItemType == 0)) _vendorItemType = 0;
            ImGui.SameLine();
            if (ImGui.RadioButton("Gear", _vendorItemType == 1)) _vendorItemType = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton("Non-gear", _vendorItemType == 2)) _vendorItemType = 2;
            if (ImGui.CollapsingHeader($"Categories ({_vendorCategories.Count} selected)###vendor_categories"))
            {
                if (ImGui.SmallButton("All categories")) _vendorCategories.UnionWith(categories);
                ImGui.SameLine();
                if (ImGui.SmallButton("No categories")) _vendorCategories.Clear();
                foreach (var category in categories)
                {
                    var enabled = _vendorCategories.Contains(category);
                    if (ImGui.Checkbox(category, ref enabled))
                    {
                        if (enabled) _vendorCategories.Add(category); else _vendorCategories.Remove(category);
                    }
                }
            }
            ImGui.EndChild();
        }

        var filtered = GetFilteredVendorItems(list).ToList();
        var snapshot = string.Join("|", _vendorSelectedListId, _vendorSearch, _vendorItemType, _vendorHqOnly, _vendorNqOnly,
            _vendorAllowedOnly, string.Join(",", _vendorCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)));
        if (!string.Equals(snapshot, _vendorFilterSnapshot, StringComparison.Ordinal))
        {
            _vendorFilterSnapshot = snapshot;
            _vendorPage = 0;
        }
        var targets = GetVendorTargets(filtered.Select(item => item.Id)).ToList();
        var toAllow = targets.Where(target => !list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        var toRemove = targets.Where(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq))).ToList();
        ImGui.TextDisabled($"{filtered.Count} matching eligible items • {toRemove.Select(target => target.ItemId).Distinct().Count()} allowed in this list");
        ImGui.TextUnformatted("Allow quality:"); ImGui.SameLine();
        if (ImGui.RadioButton("Both", !_vendorHqOnly && !_vendorNqOnly)) { _vendorHqOnly = false; _vendorNqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("NQ", _vendorNqOnly)) { _vendorNqOnly = true; _vendorHqOnly = false; }
        ImGui.SameLine();
        if (ImGui.RadioButton("HQ", _vendorHqOnly)) { _vendorHqOnly = true; _vendorNqOnly = false; }

        if (ImGui.Button($"Allow filtered ({toAllow.Count} quality entries)"))
        {
            _config.SetVendorAllowlistKeys(_vendorSelectedListId, toAllow.Select(t => Configuration.MakeSellKey(t.ItemId, t.Hq)), true);
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"Remove filtered ({toRemove.Count} quality entries)"))
        {
            _config.SetVendorAllowlistKeys(_vendorSelectedListId, toRemove.Select(t => Configuration.MakeSellKey(t.ItemId, t.Hq)), false);
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Allow all eligible items")) ImGui.OpenPopup("Confirm broad vendor allowlist");
        if (ImGui.BeginPopupModal("Confirm broad vendor allowlist", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var allKeys = GetVendorTargets(_vendorCatalog!.Items.Where(item => item.NpcSalePrice > 0).Select(item => item.Id))
                .Select(target => Configuration.MakeSellKey(target.ItemId, target.Hq)).Distinct().ToList();
            var additions = allKeys.Count(key => !list.ItemKeys.Contains(key));
            ImGui.TextWrapped($"Add {additions} item-quality entries from the current catalog? New game items are not auto-allowed. RR protections and threshold still apply. Preview does not sell anything.");
            if (ImGui.Button("Add entries"))
            {
                _config.SetVendorAllowlistKeys(_vendorSelectedListId, allKeys, true);
                _config.Save(); ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        var pageCount = Math.Max(1, (filtered.Count + VendorPageSize - 1) / VendorPageSize);
        _vendorPage = Math.Clamp(_vendorPage, 0, pageCount - 1);
        ImGui.BeginDisabled(_vendorPage == 0); if (ImGui.SmallButton("Previous")) _vendorPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"Page {_vendorPage + 1}/{pageCount}"); ImGui.SameLine();
        ImGui.BeginDisabled(_vendorPage >= pageCount - 1); if (ImGui.SmallButton("Next")) _vendorPage++; ImGui.EndDisabled();

        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - 16f);
        if (ImGui.BeginTable("##vendor_items", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new System.Numerics.Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Allowed", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("In list", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var item in filtered.Skip(_vendorPage * VendorPageSize).Take(VendorPageSize))
            {
                ImGui.TableNextRow(); ImGui.PushID((int)item.Id);
                var itemTargets = GetVendorTargets(new[] { item.Id }).ToList();
                var allowedCount = itemTargets.Count(target => list.ItemKeys.Contains(Configuration.MakeSellKey(target.ItemId, target.Hq)));
                var isAllowed = itemTargets.Count > 0 && allowedCount == itemTargets.Count;
                ImGui.TableSetColumnIndex(0);
                if (allowedCount > 0 && allowedCount < itemTargets.Count)
                {
                    if (ImGui.SmallButton("Mixed##allowed"))
                    {
                        foreach (var target in itemTargets) list.ItemKeys.Add(Configuration.MakeSellKey(target.ItemId, target.Hq));
                        _config.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mixed NQ/HQ state. Click to allow both selected qualities.");
                }
                else if (ImGui.Checkbox("##allowed", ref isAllowed))
                {
                    foreach (var target in itemTargets)
                    {
                        var key = Configuration.MakeSellKey(target.ItemId, target.Hq);
                        if (isAllowed) list.ItemKeys.Add(key); else list.ItemKeys.Remove(key);
                    }
                    _config.Save();
                }
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name} (#{item.Id}) • NPC sale {item.NpcSalePrice} gil");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Category);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(GetVendorQualityStatus(list, item));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private IEnumerable<ItemBlocklistCatalog.Entry> GetFilteredVendorItems(Configuration.VendorAllowlist list)
    {
        var search = _vendorSearch.Trim();
        var numeric = uint.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        return _vendorCatalog!.Items.Where(item => item.NpcSalePrice > 0 && _vendorCategories.Contains(item.Category) &&
            (_vendorItemType == 0 || (_vendorItemType == 1 ? item.IsGear : !item.IsGear)) &&
            (search.Length == 0 || (numeric ? item.Id == id : item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))) &&
            (!_vendorHqOnly || item.CanBeHq) &&
            (!_vendorAllowedOnly || (_vendorHqOnly
                ? item.CanBeHq && list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true))
                : _vendorNqOnly ? list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false))
                : list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false)) || (item.CanBeHq && list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true))))));
    }

    private IEnumerable<VendorTarget> GetVendorTargets(IEnumerable<uint> ids)
    {
        var items = _vendorCatalog!.Items.Where(item => item.NpcSalePrice > 0).ToDictionary(item => item.Id);
        foreach (var id in ids)
        {
            if (!items.TryGetValue(id, out var item)) continue;
            if (!_vendorHqOnly) yield return new VendorTarget(id, false);
            if (!_vendorNqOnly && item.CanBeHq) yield return new VendorTarget(id, true);
        }
    }

    private string GetVendorQualityStatus(Configuration.VendorAllowlist list, ItemBlocklistCatalog.Entry item)
    {
        var nq = list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, false));
        var hq = item.CanBeHq && list.ItemKeys.Contains(Configuration.MakeSellKey(item.Id, true));
        return (nq, hq) switch { (true, true) => "Both", (true, false) => "NQ", (false, true) => "HQ", _ => "—" };
    }

    private void DrawVendorAssignments()
    {
        var lists = _config.VendorAllowlists.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var characters = _plugin.GetAutoRetainerCharacters().Where(data => data.CID != 0).ToList();
        foreach (var pair in _config.Characters)
            if (pair.Key != 0 && characters.All(data => data.CID != pair.Key))
                characters.Add(new AutoRetainerAPI.Configuration.OfflineCharacterData { CID = pair.Key, Name = pair.Value?.Name ?? string.Empty, World = pair.Value?.World ?? string.Empty });
        characters = characters.DistinctBy(data => data.CID).OrderBy(data => GetCharacterDisplayName(data.CID, _config.Characters.GetValueOrDefault(data.CID)), StringComparer.OrdinalIgnoreCase).ToList();
        if (lists.Count == 0) { ImGui.TextDisabled("Create an allowlist first."); return; }
        if (_vendorSelectedCharacter == 0 || characters.All(data => data.CID != _vendorSelectedCharacter)) _vendorSelectedCharacter = characters.FirstOrDefault()?.CID ?? 0;
        var characterName = GetCharacterDisplayName(_vendorSelectedCharacter, _config.Characters.GetValueOrDefault(_vendorSelectedCharacter));
        ImGui.SetNextItemWidth(VendorInputWidth);
        if (ImGui.BeginCombo("Character", characterName))
        {
            foreach (var data in characters)
            {
                var label = GetCharacterDisplayName(data.CID, _config.GetCharacterSettings(data.CID, data.Name, data.World));
                if (ImGui.Selectable($"{label}##{data.CID}", data.CID == _vendorSelectedCharacter)) _vendorSelectedCharacter = data.CID;
            }
            ImGui.EndCombo();
        }
        if (_vendorSelectedCharacter == 0) return;
        var settings = _config.GetCharacterSettings(_vendorSelectedCharacter);
        var height = Math.Min(280f, Math.Max(120f, ImGui.GetContentRegionAvail().Y * .45f));
        if (ImGui.BeginChild("##vendor_assignment_list", new System.Numerics.Vector2(0, height), true))
        {
            foreach (var pair in lists)
            {
                var assigned = settings.VendorAllowlistIds.Contains(pair.Key);
                if (ImGui.Checkbox($"{pair.Value.Name} ({pair.Value.ItemKeys.Count} entries)##vendor_assign-{pair.Key}", ref assigned))
                {
                    if (assigned) settings.VendorAllowlistIds.Add(pair.Key); else settings.VendorAllowlistIds.Remove(pair.Key);
                    _config.Save();
                    if (assigned) _plugin.StartVendorPreviewForAppliedList(_vendorSelectedCharacter);
                }
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"Market-list threshold: {settings.AutoListMinimumStackValue} gil. Per-list limits compare total stack market value; overlapping permissions use the lowest assigned list limit.");
        ImGui.TextDisabled($"Effective permission: {_config.GetEffectiveVendorAllowlist(_vendorSelectedCharacter).Count} item-quality entries.");
        DrawVendorPreviewControls();
    }

    private void DrawVendorPreviewControls(string? selectedListId = null)
    {
        if (!ImGui.CollapsingHeader("Preview and vending")) return;

        ImGui.Separator();
        if (ImGui.Button(_plugin.VendorPreviewRunning ? "Refreshing…" : "Refresh preview") && !_plugin.VendorPreviewRunning)
            _plugin.StartVendorPreview(selectedListId);
        ImGui.SameLine();
        ImGui.BeginDisabled(!_plugin.VendorPreviewRunning);
        if (ImGui.Button("Cancel refresh")) _plugin.CancelVendorPreview();
        ImGui.EndDisabled();
        ImGui.TextWrapped(_plugin.VendorPreviewStatus);
        if (_plugin.VendorPreviewListId is { } previewListId && _config.VendorAllowlists.TryGetValue(previewListId, out var previewList))
        {
            var assignedToCurrentCharacter = _config.GetCharacterSettings(_plugin.CurrentCharacterId).VendorAllowlistIds.Contains(previewListId);
            var assignmentStatus = assignedToCurrentCharacter ? " (assigned)" : " (not assigned; preview only; apply it to enable selling)";
            ImGui.TextDisabled($"Preview scope: list '{previewList.Name}'{assignmentStatus}.");
        }
        if (_plugin.VendorPreviewStale) ImGui.TextColored(new System.Numerics.Vector4(1f, .65f, .2f, 1f), "STALE SNAPSHOT — refresh before relying on these results.");
        if (_plugin.VendorPreviewCapturedUtc != default)
            ImGui.TextDisabled($"Snapshot: {_plugin.VendorPreviewCapturedUtc.ToLocalTime():g}; inventory {(_plugin.VendorPreviewInventoryComplete ? "complete" : "incomplete")}; {_plugin.VendorPreviewResults.Count} stacks.");
        var candidates = _plugin.VendorPreviewResults.Where(result => result.IsCandidate).ToList();
        ImGui.TextDisabled($"Candidates: {candidates.Count} stack(s), {candidates.Sum(result => result.Stack.Quantity)} item(s), estimated NPC proceeds {candidates.Sum(result => result.EstimatedNpcProceeds ?? 0):N0} gil.");
        ImGui.BeginDisabled(!_plugin.CanStartVendorSale);
        if (ImGui.Button(_plugin.VendorSaleRunning ? "Selling…" : $"Sell preview candidates ({candidates.Count} stacks)"))
        {
            ImGui.OpenPopup("Confirm vendor sale batch");
        }
        ImGui.EndDisabled();
        if (_plugin.VendorSaleRunning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop selling")) _plugin.StopVendorSale();
        }
        if (ImGui.BeginPopupModal("Confirm vendor sale batch", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Sell {candidates.Count} whole stack(s) ({candidates.Sum(result => result.Stack.Quantity)} items) to the currently open retainer? Estimated NPC proceeds: {candidates.Sum(result => result.EstimatedNpcProceeds ?? 0):N0} gil.");
            ImGui.TextWrapped("Each stack sells immediately without an in-game confirmation and cannot be recovered. Keep this character and retainer open. RR/AutoRetainer automation must be stopped. AR Protect List is not honored; RR allowlists, blocklists, Sell List protections, and fresh market checks are applied.");
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Sell whole stacks"))
            {
                _plugin.StartVendorSale();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!_plugin.CanStartVendorSale && !_plugin.VendorSaleRunning)
            ImGui.TextDisabled(_plugin.VendorPreviewListId != null
                ? "Selling this list preview requires that list to be assigned to the active character, plus a fresh complete preview, open retainer, and stopped RR/AutoRetainer automation."
                : "Selling requires a fresh complete preview, a logged-in character with an open retainer, and stopped RR/AutoRetainer automation.");
        ImGui.TextWrapped(_plugin.VendorSaleStatus);
        ImGui.Checkbox("Show skipped items", ref _showVendorSkipped);
        if (ImGui.BeginTable("##vendor_results", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new System.Numerics.Vector2(0, 230)))
        {
            foreach (var title in new[] { "Result", "Item", "Quality", "Qty", "Market unit", "Market stack", "NPC proceeds" }) ImGui.TableSetupColumn(title);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var result in _plugin.VendorPreviewResults.Where(result => _showVendorSkipped || result.IsCandidate))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(result.Reason.ToString());
                ImGui.TableNextColumn(); ImGui.Text(result.Stack.Name);
                ImGui.TableNextColumn(); ImGui.Text(result.Stack.IsHq ? "HQ" : "NQ");
                ImGui.TableNextColumn(); ImGui.Text(result.Stack.Quantity.ToString());
                ImGui.TableNextColumn(); ImGui.Text(result.UnitMarketValue?.ToString("N2") ?? "—");
                ImGui.TableNextColumn(); ImGui.Text(result.StackMarketValue?.ToString("N2") ?? "—");
                ImGui.TableNextColumn(); ImGui.Text(result.EstimatedNpcProceeds?.ToString("N0") ?? "—");
            }
            ImGui.EndTable();
        }
    }

    private IEnumerable<ulong> GetCharactersAssignedToVendorList(string listId)
        => _config.Characters.Where(pair => pair.Value?.VendorAllowlistIds?.Contains(listId) == true).Select(pair => pair.Key);

    private string GetUniqueVendorListName(string baseName)
    {
        var name = baseName;
        var suffix = 2;
        while (_config.VendorAllowlists.Values.Any(list => string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";
        return name;
    }

    private int GetVendorListThresholdCap(string listId)
    {
        var assigned = GetCharactersAssignedToVendorList(listId).ToList();
        if (assigned.Count == 0) return 999_999_999;
        return assigned.Select(cid => _config.GetCharacterSettings(cid).AutoListMinimumStackValue).Min();
    }
}
