using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace RetainerRepricer.Ui;

internal sealed unsafe class UiReader
{
    private readonly IGameGui _gui;

    public UiReader(IGameGui gui) => _gui = gui;

    // =========================================================
    // Constants (RetainerSell name payload parsing)
    // =========================================================
    // Observed HQ marker glyph inside RetainerSell item name payload.
    // This is NOT the last character due to SeString/control payload bytes.
    public const char RetainerSell_HqGlyphChar = '\uE03C';


    public enum ItemSearchResultStatus
    {
        None,
        NoItemsFound,
        PleaseWaitRetry,
        OtherMessage,
    }

    // =========================================================
    // Core addon/unit helpers
    // =========================================================
    private AtkUnitBase* GetVisibleUnitBase(string addonName, int index = 1)
    {
        var addon = _gui.GetAddonByName(addonName, index);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return null;

        return unit;
    }

    private static AtkResNode* FindNodeById(AtkUldManager* uld, int nodeId)
    {
        if (uld == null) return null;

        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    private static AtkResNode* FindNodeById(AtkComponentBase* comp, ushort nodeId)
    {
        if (comp == null) return null;

        var uld = &comp->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    private static string? ReadTextNode(AtkResNode* node)
    {
        if (node == null) return null;
        if (node->Type != NodeType.Text) return null;

        return ((AtkTextNode*)node)->NodeText.ToString();
    }

    // =========================================================
    // Addon / Component helpers
    // =========================================================
    public AtkComponentBase* GetAddonComponent(string addonName, int componentNodeId)
    {
        var unit = GetVisibleUnitBase(addonName, 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, componentNodeId);
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        return compNode->Component;
    }

    // =========================================================
    // Generic addon text node reading
    // =========================================================
    public string? ReadAddonTextNode(string addonName, int nodeId)
    {
        var unit = GetVisibleUnitBase(addonName, 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, nodeId);
        return ReadTextNode(n);
    }

    // =========================================================
    // Component child text node reading
    // =========================================================
    public string? ReadComponentTextNode(AtkComponentBase* comp, ushort nodeId)
    {
        var n = FindNodeById(comp, nodeId);
        return ReadTextNode(n);
    }

    // =========================================================
    // Generic renderer node readers (list item renderers)
    // =========================================================
    public string? ReadRendererText(AtkComponentListItemRenderer* renderer, ushort nodeId)
        => GetRendererTextByNodeId(renderer, nodeId);

    public string? GetRendererTextByNodeId(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = &renderer->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId != nodeId) continue;

            return ReadTextNode(n);
        }

        return null;
    }

    public AtkResNode* GetRendererNodeById(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = &renderer->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    // =========================================================
    // Market board: ItemSearchResult list access + HQ renderer icon
    // =========================================================
    public AtkComponentList* GetMarketList()
    {
        // NOTE: do not require visible here; list can be valid while animating in.
        var addon = _gui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, NodePaths.ItemSearchResult_ListNodeId); // list component node
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        var comp = compNode->Component;
        return comp != null ? (AtkComponentList*)comp : null;
    }

    // (Kept private; plugin has its own TryReadMarketRow and uses RowIsHq/ReadRendererText)
    private bool TryReadMarketRow(int rowIndex, out int unitPrice, out string seller, out bool isHq)
    {
        unitPrice = 0;
        seller = string.Empty;
        isHq = false;

        var list = GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0 || rowIndex < 0 || rowIndex >= count) return false;

        var r = list->GetItemRenderer(rowIndex);
        if (r == null) return false;

        // CRITICAL: renderer uld can be null/empty while rows are constructing
        if (r->UldManager.NodeList == null || r->UldManager.NodeListCount <= 0)
            return false;

        isHq = RowIsHq(r);

        var unitRaw = ReadRendererText(r, NodePaths.UnitPriceNodeId);
        var sellerRaw = ReadRendererText(r, NodePaths.SellerNodeId);

        var parsed = ParseGil(unitRaw);
        if (parsed == null || parsed.Value <= 0) return false;

        unitPrice = parsed.Value;
        seller = (sellerRaw ?? string.Empty).Trim('\'', ' ');
        return true;
    }

    public bool RowIsHq(AtkComponentListItemRenderer* renderer)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null) return false;

        // HQ -> DrawFlags = 0x0
        // NQ -> DrawFlags = 0x100 (observed)
        return n->DrawFlags == 0;
    }

    public void DumpHqIconState(AtkComponentListItemRenderer* renderer, int rowIndex, Action<string> log)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null)
        {
            log($"[HQ] row {rowIndex}: node {NodePaths.HqIconNodeId} not found");
            return;
        }

        log($"[HQ] row {rowIndex}: draw=0x{n->DrawFlags:X} a={n->Color.A}");
    }

    // =========================================================
    // ItemSearchResult: error/status message (NodeId 5)
    // =========================================================
    public string GetItemSearchResultErrorMessage()
    {
        // Message is empty/null when no error/status.
        var raw = ReadAddonTextNode("ItemSearchResult", NodePaths.ItemSearchResult_ErrorMessageNodeId);
        return NormalizeStatusText(raw);
    }

    public ItemSearchResultStatus GetItemSearchResultStatus(out string message)
    {
        message = GetItemSearchResultErrorMessage();
        if (string.IsNullOrWhiteSpace(message))
            return ItemSearchResultStatus.None;

        // Exact strings you care about (match your screenshots).
        // Use OrdinalIgnoreCase + Contains to survive punctuation/spacing variations.
        if (message.Contains("No items found", StringComparison.OrdinalIgnoreCase))
            return ItemSearchResultStatus.NoItemsFound;

        if (message.Contains("Please wait and try your search again", StringComparison.OrdinalIgnoreCase))
            return ItemSearchResultStatus.PleaseWaitRetry;

        return ItemSearchResultStatus.OtherMessage;
    }

    private static string NormalizeStatusText(string? raw)
    {
        // Similar spirit to NormalizeItemName, but for normal UI text messages:
        // Keep printable ASCII; strip control/payload chars; trim.
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch >= 0x20 && ch <= 0x7E)
                sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    // =========================================================
    // Inventory helpers (Selling / new listings)
    // =========================================================

    // Containers we scan for sellable items
    private static readonly InventoryType[] SellScanContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    /// <summary>
    /// Find an itemId in player inventory bags and return container + slot.
    /// </summary>
    public bool TryFindItemInInventory(uint itemId, out int container, out int slot)
    {
        container = 0;
        slot = 0;

        if (itemId == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        foreach (var type in SellScanContainers)
        {
            var cont = inv->GetInventoryContainer(type);
            if (cont == null || !cont->IsLoaded)
                continue;

            for (var i = 0; i < cont->Size; i++)
            {
                var s = cont->GetInventorySlot(i);
                if (s == null)
                    continue;

                if (s->ItemId != itemId)
                    continue;

                if (s->Quantity <= 0)
                    continue;

                container = (int)type;
                slot = i;
                return true;
            }
        }

        return false;
    }

    // =========================================================
    // RetainerList: AtkComponentList access
    // =========================================================
    public AtkComponentList* GetRetainerList()
    {
        var unit = GetVisibleUnitBase("RetainerList", 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, NodePaths.RetainerListNodeId);
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        var comp = compNode->Component;
        return comp != null ? (AtkComponentList*)comp : null;
    }

    // =========================================================
    // RetainerSell: readers + parsing/normalization
    // =========================================================
    public string? ReadRetainerSellItemNameRaw()
        => ReadAddonTextNode("RetainerSell", NodePaths.RetainerSell_ItemNameNodeId);

    // Back-compat: keep existing method name
    public string? ReadRetainerSellItemName()
        => ReadRetainerSellItemNameRaw();

    public string NormalizeItemName(string? raw)
    {
        // Goal: produce a readable name. Keep ASCII letters/punct/spaces.
        // Do NOT rely on "last char" because payload includes markup bytes.
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            // keep visible ascii
            if (ch >= 0x20 && ch <= 0x7E)
            {
                sb.Append(ch);
            }
            // keep HQ glyph if you want it present for debugging; caller can strip it
            else if (ch == RetainerSell_HqGlyphChar)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim();
    }

    public bool RetainerSellNameContainsHqGlyph(string? raw)
        => !string.IsNullOrEmpty(raw) && raw.IndexOf(RetainerSell_HqGlyphChar) >= 0;

    public bool IsRetainerSellItemHq()
        => RetainerSellNameContainsHqGlyph(ReadRetainerSellItemNameRaw());

    public string GetRetainerSellItemNameDisplay(bool stripHqGlyph = true)
    {
        var raw = ReadRetainerSellItemNameRaw();
        var cleaned = NormalizeItemName(raw);

        if (stripHqGlyph)
            cleaned = cleaned.Replace(RetainerSell_HqGlyphChar.ToString(), string.Empty).Trim();

        return cleaned;
    }

    public string? ReadRetainerSellAskingPriceText()
    {
        var comp = GetAddonComponent("RetainerSell", NodePaths.RetainerSell_AskingPriceNumericNodeId);
        if (comp == null) return null;

        return ReadComponentTextNode(comp, NodePaths.RetainerSell_AskingPriceTextNodeId);
    }

    public int? ReadRetainerSellAskingPrice()
        => ParseGil(ReadRetainerSellAskingPriceText());

    // =========================================================
    // RetainerSellList: readers + parsing
    // =========================================================
    public string? ReadRetainerSellListCountText()
        => ReadAddonTextNode("RetainerSellList", NodePaths.RetainerSellList_CountNodeId);

    public int? ReadRetainerSellListListedCount()
        => ParseListedCount(ReadRetainerSellListCountText());

    public (int? listed, int? max) ReadRetainerSellListListedAndMax()
    {
        var raw = ReadRetainerSellListCountText();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        // Parse "N/20" (or whatever max becomes later)
        int left = 0, right = 0;
        bool anyLeft = false, anyRight = false;
        bool onRight = false;

        foreach (var ch in raw)
        {
            if (ch == '/') { onRight = true; continue; }

            if (ch >= '0' && ch <= '9')
            {
                if (!onRight)
                {
                    anyLeft = true;
                    left = (left * 10) + (ch - '0');
                }
                else
                {
                    anyRight = true;
                    right = (right * 10) + (ch - '0');
                }
            }
        }

        return (anyLeft ? left : null, anyRight ? right : null);
    }
    /// <summary>
    /// Clicks InventoryGrid slot to open RetainerSell (new listing path).
    /// Equivalent to `/pcall InventoryGrid true 0 slot container`.
    /// </summary>
    public bool TryOpenRetainerSellFromInventory(int container, int slot)
    {
        var idx = FindVisibleAddonIndex("InventoryGrid", 10);
        if (idx < 0)
            return false;

        var addon = _gui.GetAddonByName("InventoryGrid", idx);
        if (addon.IsNull)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
            return false;

        // Callback signature observed in SND / AutoRetainer:
        Callback.Fire(unit, updateState: true, 15, container, slot);
        return true;
    }


    // =========================================================
    // Parsing helpers
    // =========================================================
    public static int? ParseGil(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        long value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > int.MaxValue) return int.MaxValue;
            }
        }

        return any ? (int)value : null;
    }

    public static int? ParseListedCount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        int value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch == '/') break;

            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > 999) return 999;
            }
        }

        return any ? value : null;
    }

    // =========================================================
    // Debug helpers
    // =========================================================
    public void DumpAddonNodeList(string addonName, Action<string> log)
    {
        var addon = _gui.GetAddonByName(addonName, 1);
        if (addon.IsNull)
        {
            log("[NodeList] addon not open");
            return;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null)
        {
            log("[NodeList] unit null");
            return;
        }

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;

        log($"[NodeList] {addonName} NodeListCount={count}");

        if (nl == null || count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;

            log($"[NodeList] i={i} id={n->NodeId} type={n->Type} x={n->X} y={n->Y}");
        }
    }

    public static string DumpNonAsciiCodepoints(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "<null/empty>";

        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (ch < 0x20 || ch > 0x7E)
                sb.Append($"U+{(int)ch:X4} ");
        }
        return sb.Length == 0 ? "<none>" : sb.ToString().Trim();
    }

    private int FindVisibleAddonIndex(string name, int max = 5)
    {
        for (int i = 1; i <= max; i++)
        {
            var a = _gui.GetAddonByName(name, i);
            if (a.IsNull) continue;

            var u = (AtkUnitBase*)a.Address;
            if (u != null && u->IsVisible)
                return i;
        }
        return -1;
    }
}
