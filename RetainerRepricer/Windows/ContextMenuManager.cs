using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;

using RetainerRepricer.Windows;

namespace RetainerRepricer;

/// <summary>
/// Injects the sell-list actions into Dalamud context menus so items can be toggled without leaving the bell UI.
/// </summary>
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

    private readonly Plugin _plugin;
    private readonly Configuration _config;
    private readonly MinCountPopup _minCountPopup;

    #endregion

    #region Lifecycle

    public ContextMenuManager(Plugin plugin, Configuration config, MinCountPopup minCountPopup)
    {
        _plugin = plugin;
        _config = config;
        _minCountPopup = minCountPopup;
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
        var isHq = inv.TargetItem.Value.IsHq;

        var isInSellList = _config.HasSellItem(itemId, isHq);

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
                    if (_config.RemoveSellItem(itemId, isHq))
                    {
                        Plugin.Log.Information("[RR][ContextMenu] Removed item {ItemId} (HQ={IsHq}) from Sell List", itemId, isHq);
                        _config.Save();
                    }
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

                OnClicked = clickedArgs =>
            {
                var clickPosition = ImGui.GetMousePos();
                var defaultPriority = _config.GetAppendSortOrder();
                var allowPreserveToggle = _config.AutoPruneMissingInventory;
                _minCountPopup.Show(itemId, isHq, itemName, (id, hq, minCount, priority, keep) =>
                {
                    if (_config.TryAddSellItemWithMinCount(id, hq, itemName, minCount, priority))
                    {
                        if (allowPreserveToggle)
                            _config.SetSellItemPreserveFlag(id, hq, keep);

                        Plugin.Log.Information("[RR][ContextMenu] Added item {ItemId} (HQ={IsHq}, minCount={MinCount}) to Sell List", id, hq, minCount);
                        _config.Save();
                        if (_plugin.SmartSortEnabled)
                            _ = _plugin.RequestSmartSortAsync("item_added", force: true);
                    }
                }, clickPosition, defaultPriority, _plugin.SmartSortEnabled, allowPreserveToggle);
            }
        });
    }

    #endregion
}
