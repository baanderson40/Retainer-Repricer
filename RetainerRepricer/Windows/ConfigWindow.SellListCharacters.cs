using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Bindings.ImGui;

namespace RetainerRepricer.Windows;

public sealed partial class ConfigWindow
{
    private ulong _sellListEditingCharacter;
    private ulong _sellListLastLoggedInCharacter;

    private void SelectSellListCharacter(ulong cid)
    {
        if (_sellListEditingCharacter == cid) return;
        _sellListEditingCharacter = cid;
        _confirmClearSellList = false;
        _forceCollapseSellListRows = true;
        _sellCatalogPending = Array.Empty<SellCatalogTarget>();
        _sellCatalogPendingCharacter = 0;
        _sellCatalogConfirmRequested = false;
        _sellCatalogPage = 0;
    }

    private bool DrawSellListCharacterSelector()
    {
        var characters = _plugin.GetAutoRetainerCharacters()
            .Where(data => data.CID != 0).DistinctBy(data => data.CID).ToList();
        if (_config.NormalizeCharacterDisplayOrder(characters.Select(data => data.CID))) SaveConfig();
        var positions = _config.CharacterDisplayOrder.Select((cid, index) => (cid, index))
            .ToDictionary(entry => entry.cid, entry => entry.index);
        characters = characters.Where(data => !_config.IsCharacterBlacklisted(data.CID))
            .OrderBy(data => positions[data.CID]).ToList();

        var loggedInCid = _plugin.CurrentCharacterId;
        if (_sellListLastLoggedInCharacter != loggedInCid)
        {
            _sellListLastLoggedInCharacter = loggedInCid;
            SelectSellListCharacter(characters.Any(data => data.CID == loggedInCid)
                ? loggedInCid : characters.FirstOrDefault()?.CID ?? 0);
        }
        if (_sellListEditingCharacter == 0 || characters.All(data => data.CID != _sellListEditingCharacter))
            SelectSellListCharacter(characters.Any(data => data.CID == loggedInCid)
                ? loggedInCid : characters.FirstOrDefault()?.CID ?? 0);
        if (characters.Count == 0)
        {
            ImGui.TextWrapped("No available AutoRetainer characters. Blacklisted characters are hidden.");
            return false;
        }

        var selected = characters.First(data => data.CID == _sellListEditingCharacter);
        // Populate display metadata from AR, without changing assignments or automation settings.
        _config.GetCharacterSettings(selected.CID, selected.Name, selected.World);
        ImGui.SetNextItemWidth(BlocklistInputWidth);
        if (ImGui.BeginCombo("Character##sell_list_character", GetCharacterDisplayName(selected.CID, _config.Characters.GetValueOrDefault(selected.CID))))
        {
            foreach (var data in characters)
            {
                var label = string.IsNullOrWhiteSpace(data.Name)
                    ? GetCharacterDisplayName(data.CID, _config.Characters.GetValueOrDefault(data.CID))
                    : $"{data.Name}@{data.World}";
                if (ImGui.Selectable($"{label}##{data.CID}", data.CID == _sellListEditingCharacter))
                {
                    _config.GetCharacterSettings(data.CID, data.Name, data.World);
                    SelectSellListCharacter(data.CID);
                }
            }
            ImGui.EndCombo();
        }
        return true;
    }

    private List<string> GetSellListRetainerNames(ulong cid)
        => _config.GetAllRetainerNames(cid)
            .Concat(_plugin.GetAutoRetainerCharacters().Where(data => data.CID == cid)
                .SelectMany(data => data.RetainerData).Select(retainer => retainer.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
}
