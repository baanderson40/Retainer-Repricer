using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Interop;
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

        Plugin.Log.Verbose(
            "[RR][ContextMenu] Inventory menu opened: itemId={ItemId}, hq={IsHq}, inSellList={IsInSellList}, sellable={IsSellable}, modifier={Modifier}, svcLShift={SvcLShift}, svcRShift={SvcRShift}, svcLCtrl={SvcLCtrl}, svcRCtrl={SvcRCtrl}, svcLAlt={SvcLAlt}, svcRAlt={SvcRAlt}, winLShift={WinLShift}, winRShift={WinRShift}, winLCtrl={WinLCtrl}, winRCtrl={WinRCtrl}, winLAlt={WinLAlt}, winRAlt={WinRAlt}",
            itemId,
            isHq,
            isInSellList,
            isSellable,
            _config.ContextMenuQuickAddModifier,
            Svc.KeyState[VirtualKey.LSHIFT],
            Svc.KeyState[VirtualKey.RSHIFT],
            Svc.KeyState[VirtualKey.LCONTROL],
            Svc.KeyState[VirtualKey.RCONTROL],
            Svc.KeyState[VirtualKey.LMENU],
            Svc.KeyState[VirtualKey.RMENU],
            GenericHelpers.IsKeyPressed(LimitedKeys.LeftShiftKey),
            GenericHelpers.IsKeyPressed(LimitedKeys.RightShiftKey),
            GenericHelpers.IsKeyPressed(LimitedKeys.LeftControlKey),
            GenericHelpers.IsKeyPressed(LimitedKeys.RightControlKey),
            GenericHelpers.IsKeyPressed(LimitedKeys.LeftAltKey),
            GenericHelpers.IsKeyPressed(LimitedKeys.RightAltKey));

        if (!isInSellList && isSellable && TryQuickAddFromModifier(itemId, isHq, itemName))
            isInSellList = true;

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
                var defaultPriority = _config.GetAppendSortOrder();
                var allowPreserveToggle = _config.AutoPruneMissingInventory;

                if (!_config.EnableContextMenuPopup)
                {
                    AddSellListEntryFromMenu(itemId, isHq, itemName, 1, defaultPriority, keepPreserved: false, allowPreserveToggle);
                    return;
                }

                var clickPosition = ImGui.GetMousePos();
                _minCountPopup.Show(itemId, isHq, itemName, (id, hq, minCount, priority, keep) =>
                {
                    AddSellListEntryFromMenu(id, hq, itemName, minCount, priority, keep, allowPreserveToggle);
                }, clickPosition, defaultPriority, _plugin.SmartSortEnabled, allowPreserveToggle);
            }
        });
    }

    #endregion

    #region Helpers

    private bool TryQuickAddFromModifier(uint itemId, bool isHq, string itemName)
    {
        if (!IsQuickAddModifierHeld())
        {
            Plugin.Log.Verbose("[RR][ContextMenu] Quick add skipped: configured modifier not held for item {ItemId} (HQ={IsHq})",
                itemId, isHq);
            return false;
        }

        var defaultPriority = _config.GetAppendSortOrder();
        Plugin.Log.Verbose("[RR][ContextMenu] Quick add triggered for item {ItemId} (HQ={IsHq}) with priority {Priority}",
            itemId, isHq, defaultPriority);
        return AddSellListEntryFromMenu(itemId, isHq, itemName, 1, defaultPriority, keepPreserved: false, allowPreserveToggle: false,
            source: "quick_add");
    }

    private bool IsQuickAddModifierHeld()
        => _config.ContextMenuQuickAddModifier switch
        {
            ContextMenuQuickAddModifier.None => false,
            ContextMenuQuickAddModifier.Shift => IsAnyWinApiKeyHeld(LimitedKeys.LeftShiftKey, LimitedKeys.RightShiftKey),
            ContextMenuQuickAddModifier.LeftShift => IsWinApiKeyHeld(LimitedKeys.LeftShiftKey),
            ContextMenuQuickAddModifier.RightShift => IsWinApiKeyHeld(LimitedKeys.RightShiftKey),
            ContextMenuQuickAddModifier.Ctrl => IsAnyWinApiKeyHeld(LimitedKeys.LeftControlKey, LimitedKeys.RightControlKey),
            ContextMenuQuickAddModifier.LeftCtrl => IsWinApiKeyHeld(LimitedKeys.LeftControlKey),
            ContextMenuQuickAddModifier.RightCtrl => IsWinApiKeyHeld(LimitedKeys.RightControlKey),
            ContextMenuQuickAddModifier.Alt => IsAnyWinApiKeyHeld(LimitedKeys.LeftAltKey, LimitedKeys.RightAltKey),
            ContextMenuQuickAddModifier.LeftAlt => IsWinApiKeyHeld(LimitedKeys.LeftAltKey),
            ContextMenuQuickAddModifier.RightAlt => IsWinApiKeyHeld(LimitedKeys.RightAltKey),
            _ => false,
        };

    private static bool IsAnyWinApiKeyHeld(LimitedKeys firstKey, LimitedKeys secondKey)
        => IsWinApiKeyHeld(firstKey) || IsWinApiKeyHeld(secondKey);

    private static bool IsWinApiKeyHeld(LimitedKeys key)
        => GenericHelpers.IsKeyPressed(key);

    private bool AddSellListEntryFromMenu(
        uint itemId,
        bool isHq,
        string itemName,
        int minCount,
        int priority,
        bool keepPreserved,
        bool allowPreserveToggle,
        string source = "menu")
    {
        if (!_config.TryAddSellItemWithMinCount(itemId, isHq, itemName, minCount, priority))
            return false;

        if (allowPreserveToggle)
            _config.SetSellItemPreserveFlag(itemId, isHq, keepPreserved);

        Plugin.Log.Information("[RR][ContextMenu] Added item {ItemId} (HQ={IsHq}, minCount={MinCount}, source={Source}) to Sell List",
            itemId, isHq, minCount, source);
        _config.Save();
        if (_plugin.SmartSortEnabled)
            _ = _plugin.RequestSmartSortAsync("item_added", force: true);

        return true;
    }

    #endregion
}
