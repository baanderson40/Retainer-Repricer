using System;
using System.Collections.Generic;
using System.Linq;

using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace RetainerRepricer.Services;

internal sealed class ItemBlocklistCatalog
{
    internal sealed record Entry(uint Id, string Name, uint CategoryId, string Category, bool CanBeHq, uint ItemLevel, byte Rarity, bool IsGear, uint NpcSalePrice);

    private readonly List<Entry> _items;
    internal IReadOnlyList<Entry> Items => _items;
    internal IReadOnlyList<string> Categories { get; }

    internal ItemBlocklistCatalog()
    {
        var categories = Svc.Data.GetExcelSheet<ItemUICategory>()!
            .Where(row => !string.IsNullOrWhiteSpace(row.Name.ToString()))
            .ToDictionary(row => row.RowId, row => row.Name.ToString());
        Categories = categories.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        _items = Svc.Data.GetExcelSheet<Item>()!
            .Where(item => item.RowId != 0 && !string.IsNullOrWhiteSpace(item.Name.ToString()) && !item.IsUntradable)
            .Select(item =>
            {
                var categoryId = item.ItemUICategory.RowId;
                return new Entry(item.RowId, item.Name.ToString().Trim(), categoryId,
                    categories.GetValueOrDefault(categoryId, "Other"), item.CanBeHq, item.LevelItem.RowId, item.Rarity,
                    item.EquipSlotCategory.RowId != 0, item.PriceLow);
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id).ToList();
    }
}
