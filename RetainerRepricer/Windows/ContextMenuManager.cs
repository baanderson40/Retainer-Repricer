using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;

namespace RetainerRepricer;

internal sealed class ContextMenuManager : IDisposable
{
    #region UI constants

    // Prefix icon shown in the left column.
    private const SeIconChar PrefixIcon = SeIconChar.BoxedLetterR;

    // UI color indices (easy to tweak later).
    private const ushort PrefixColor = 31;        // orange/gold
    private const ushort RemoveColor = 539;       // brighter red
    private const ushort DisabledColor = 703;     // grey

    private const string AddLabel = " + Add to Sell List";
    private const string RemoveLabel = "- Remove from Sell List";
    private const string NotSellableLabel = " Not Sellable";

    #endregion

    #region Fields

    private readonly Configuration _config;

    #endregion

    #region Lifecycle

    public ContextMenuManager(Configuration config)
    {
        _config = config;
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
        => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    #endregion

    #region Context menu injection

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!_config.PluginEnabled)
            return;

        if (args.MenuType != ContextMenuType.Inventory)
            return;

        if (args.Target is not MenuTargetInventory inv)
            return;

        if (inv.TargetItem == null)
            return;

        // BaseItemId is already normalized by Dalamud.
        var itemId = inv.TargetItem.Value.BaseItemId;
        if (itemId == 0)
            return;

        var isInSellList = _config.HasSellItem(itemId);

        // Pull Lumina once (name + tradable/marketable gate).
        var itemRow = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var itemName = itemRow?.Name.ToString().Trim() ?? string.Empty;

        // Match the usual behavior: untradable items get a disabled entry.
        // If we can't read the row, treat it as not sellable.
        var isSellable = itemRow.HasValue && !itemRow.Value.IsUntradable;

        // If it's already tracked, always allow removing it (even if untradable).
        if (isInSellList)
        {
            args.AddMenuItem(new MenuItem
            {
                IsEnabled = true,
                IsReturn = false,
                IsSubmenu = false,

                Prefix = PrefixIcon,
                PrefixColor = PrefixColor,

                Name = new SeStringBuilder()
                    .AddUiForeground(RemoveLabel, RemoveColor)
                    .Build(),

                OnClicked = _ =>
                {
                    if (_config.RemoveSellItem(itemId))
                        _config.Save();
                }
            });

            return;
        }

        if (!isSellable)
        {
            args.AddMenuItem(new MenuItem
            {
                IsEnabled = false,
                IsReturn = false,
                IsSubmenu = false,

                Prefix = PrefixIcon,
                PrefixColor = PrefixColor,

                Name = new SeStringBuilder()
                    .AddUiForeground(NotSellableLabel, DisabledColor)
                    .Build(),
            });

            return;
        }

        args.AddMenuItem(new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            IsSubmenu = false,

            Prefix = PrefixIcon,
            PrefixColor = PrefixColor,

            Name = new SeStringBuilder()
                .AddUiForeground(AddLabel, PrefixColor)
                .Build(),

            OnClicked = _ =>
            {
                // Prefer Lumina name if available; empty is fine.
                if (_config.TryAddSellItem(itemId, itemName))
                    _config.Save();
            }
        });
    }

    #endregion
}
