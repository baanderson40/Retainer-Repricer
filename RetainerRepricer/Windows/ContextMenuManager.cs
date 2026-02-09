using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;

namespace RetainerRepricer;

internal sealed class ContextMenuManager : IDisposable
{
    // Prefix icon in left column (true prefix column control)
    private const SeIconChar PrefixIcon = SeIconChar.BoxedLetterR;

    // UI color indices (tweak later)
    private const ushort ColorPrefix = 32;   // orange/gold
    private const ushort ColorRemove = 539;  // brighter red
    private const ushort ColorDisabled = 703; // fallback grey

    private readonly Configuration _cfg;

    public ContextMenuManager(Configuration cfg)
    {
        _cfg = cfg;
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory)
            return;

        if (args.Target is not MenuTargetInventory inv)
            return;

        if (inv.TargetItem == null)
            return;

        // Use BaseItemId (already normalized)
        var itemId = inv.TargetItem.Value.BaseItemId;
        if (itemId == 0)
            return;

        var isInSellList = _cfg.HasSellItem(itemId);

        // Pull Lumina data once (for name + tradable/marketable gate)
        var itemRow = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var itemName = itemRow?.Name.ToString().Trim() ?? string.Empty;

        // v1 gate: match other plugins (untradable => greyed out)
        // NOTE: If Item row is missing, treat as not marketable (disable add).
        var marketable = itemRow.HasValue && !itemRow.Value.IsUntradable;

        // If already in sell list, always allow REMOVE (even if untradable)
        if (isInSellList)
        {
            args.AddMenuItem(new MenuItem
            {
                IsEnabled = true,
                IsReturn = false,
                IsSubmenu = false,

                Prefix = PrefixIcon,
                PrefixColor = ColorRemove,

                Name = new SeStringBuilder()
                    .AddUiForeground("- Remove from Sell List", ColorRemove)
                    .Build(),

                OnClicked = _ =>
                {
                    if (_cfg.RemoveSellItem(itemId))
                        _cfg.Save();
                }
            });

            return;
        }

        // disable if not marketable
        if (!marketable)
        {
            args.AddMenuItem(new MenuItem
            {
                IsEnabled = false,
                IsReturn = false,
                IsSubmenu = false,

                Prefix = PrefixIcon,
                PrefixColor = ColorPrefix,

                Name = new SeStringBuilder()
                    .AddUiForeground(" Not Sellable", ColorDisabled)
                    .Build(),
            });

            return;
        }

        // ADD
        args.AddMenuItem(new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            IsSubmenu = false,

            Prefix = PrefixIcon,
            PrefixColor = ColorPrefix,

            Name = new SeStringBuilder()
                .AddText("+ Add to Sell List")
                .Build(),

            OnClicked = _ =>
            {
                // Prefer Lumina name if available; fall back to empty string
                if (_cfg.AddSellItem(itemId, itemName))
                    _cfg.Save();
            }
        });
    }

    public void Dispose()
    {
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
    }
}
