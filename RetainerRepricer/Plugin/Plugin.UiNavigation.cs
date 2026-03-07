using System;

using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer;

/// <summary>
/// Centralizes helper methods for checking addon visibility and firing Dalamud callbacks.
/// </summary>
public unsafe sealed partial class Plugin
{
    private bool IsAddonVisible(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return false;

        var u = (AtkUnitBase*)a.Address;
        return u != null && u->IsVisible;
    }

    private bool IsAddonOpen(string name)
        => !GameGui.GetAddonByName(name, 1).IsNull;

    private void CloseAddonIfOpen(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return;

        var u = (AtkUnitBase*)a.Address;
        if (u == null || !u->IsVisible) return;

        Callback.Fire(u, updateState: true, -1);
    }

    private void CloseMarketWindows()
    {
        CloseAddonIfOpen("ItemHistory");
        CloseAddonIfOpen("ItemSearchResult");
    }

    private bool MarketWindowsStillOpen()
        => IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemHistory");

    private void CloseRetainerSellIfOpen()
    {
        var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
        if (!sellAddon.IsNull)
        {
            new AddonMaster.RetainerSell(sellAddon.Address).Cancel();
            return;
        }

        CloseAddonIfOpen("RetainerSell");
    }

    private bool TryClickRetainerListEntry(int index)
    {
        var addon = GameGui.GetAddonByName("RetainerList", 1);
        if (addon.IsNull) return false;

        try
        {
            var rl = new AddonMaster.RetainerList(addon.Address);
            var retainers = rl.Retainers;

            if (index < 0 || index >= retainers.Length) return false;

            var ok = retainers[index].Select();
            Log.Debug(ok
                ? $"[RL] Select retainer index={index}"
                : $"[RL] Retainer entry inactive index={index}");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RL] RetainerList select failed.");
            return false;
        }
    }

    private bool TrySelectSellItems()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        const int sellItemsIndex = 2;

        try
        {
            var ss = new AddonMaster.SelectString(addon.Address);
            if (sellItemsIndex < 0 || sellItemsIndex >= ss.EntryCount) return false;

            ss.Entries[sellItemsIndex].Select();
            Log.Debug($"[SS] Select Sell items index={sellItemsIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SS] SelectString select failed.");
            return false;
        }
    }

    private bool TryAdvanceTalk()
    {
        var addon = GameGui.GetAddonByName("Talk", 1);
        if (addon.IsNull)
        {
            Log.Debug("[Talk] Addon not open.");
            return false;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
        {
            Log.Debug("[Talk] Addon not visible.");
            return false;
        }

        new AddonMaster.Talk(addon.Address).Click();
        Log.Debug("[Talk] Click advance");
        return true;
    }

    private bool FireRetainerSellListOpenItem(int slotIndex0)
    {
        var addon = GameGui.GetAddonByName("RetainerSellList", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, slotIndex0, 1);
        Log.Verbose($"[RSL] Open item callback (0, {slotIndex0}, 1)");
        return true;
    }

    private bool FireContextMenuDismiss()
    {
        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, 0);
        Callback.Fire(unit, updateState: true, 1, 0);

        Log.Verbose("[CTX] Dismiss callbacks fired");
        return true;
    }

    private bool FireItemSearchResultOpenFilter()
    {
        var addon = GameGui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 1);
        Log.Verbose("[ISR] Open filter (callback 1)");
        return true;
    }

    private bool FireItemSearchFilterToggleHq()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 1, 1);
        Log.Verbose("[ISF] Toggle HQ (callback 1,1)");
        return true;
    }

    private bool FireItemSearchFilterAccept()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0);
        Log.Verbose("[ISF] Accept filter (callback 0)");
        return true;
    }
}
