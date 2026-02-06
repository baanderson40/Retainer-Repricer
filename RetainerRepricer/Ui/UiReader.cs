using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    public const char RetainerSell_HqGlyphChar = '\uE03C'; // 

    // =========================================================
    // Addon / Unit / NodeList helpers
    // =========================================================
    private AtkUnitBase* GetUnitBase(string addonName, int index = 1)
    {
        var addon = _gui.GetAddonByName(addonName, index);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return null;

        return unit;
    }

    public AtkComponentBase* GetAddonComponent(string addonName, int componentNodeId)
    {
        var unit = GetUnitBase(addonName, 1);
        if (unit == null) return null;

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;
        if (nl == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;
            if (n->NodeId != componentNodeId) continue;

            var compNode = (AtkComponentNode*)n;
            return compNode->Component;
        }

        return null;
    }

    // =========================================================
    // Generic addon text node reading
    // =========================================================
    public string? ReadAddonTextNode(string addonName, int nodeId)
    {
        var unit = GetUnitBase(addonName, 1);
        if (unit == null) return null;

        var uld = unit->UldManager;
        var count = uld.NodeListCount;
        if (count <= 0 || uld.NodeList == null) return null;

        for (int i = 0; i < count; i++)
        {
            var node = uld.NodeList[i];
            if (node == null) continue;
            if (node->NodeId != nodeId) continue;
            if (node->Type != NodeType.Text) return null;

            return ((AtkTextNode*)node)->NodeText.ToString();
        }

        return null;
    }

    // =========================================================
    // Component child text node reading
    // =========================================================
    public string? ReadComponentTextNode(AtkComponentBase* comp, ushort nodeId)
    {
        if (comp == null) return null;

        var uld = comp->UldManager;
        var nodes = uld.NodeList;
        var count = uld.NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId != nodeId) continue;
            if (n->Type != NodeType.Text) return null;

            return ((AtkTextNode*)n)->NodeText.ToString();
        }

        return null;
    }

    // =========================================================
    // Generic renderer node readers (list item renderers)
    // =========================================================
    public string? ReadRendererText(AtkComponentListItemRenderer* renderer, ushort nodeId)
        => GetRendererTextByNodeId(renderer, nodeId);

    public string? GetRendererTextByNodeId(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = renderer->UldManager;
        var nodes = uld.NodeList;
        var count = uld.NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId != nodeId) continue;
            if (n->Type != NodeType.Text) return null;

            return ((AtkTextNode*)n)->NodeText.ToString();
        }

        return null;
    }

    public AtkResNode* GetRendererNodeById(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = renderer->UldManager;
        var nodes = uld.NodeList;
        var count = uld.NodeListCount;
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
        var unit = GetUnitBase("ItemSearchResult", 1);
        if (unit == null) return null;

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;
        if (nl == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;
            if (n->NodeId != 26) continue; // known node id for AtkComponentList

            var compNode = (AtkComponentNode*)n;
            var comp = compNode->Component;
            if (comp == null) return null;

            return (AtkComponentList*)comp;
        }

        return null;
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
    // RetainerList: AtkComponentList access
    // =========================================================
    public AtkComponentList* GetRetainerList()
    {
        var unit = GetUnitBase("RetainerList", 1);
        if (unit == null) return null;

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;
        if (nl == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;
            if (n->NodeId != NodePaths.RetainerListNodeId) continue;

            var compNode = (AtkComponentNode*)n;
            var comp = compNode->Component;
            if (comp == null) return null;

            return (AtkComponentList*)comp;
        }

        return null;
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
    {
        var raw = ReadRetainerSellItemNameRaw();
        return RetainerSellNameContainsHqGlyph(raw);
    }

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

    // =========================================================
    // Parsing helpers (moved from Plugin; keep behavior identical)
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
}
